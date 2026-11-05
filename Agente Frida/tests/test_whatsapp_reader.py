import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, patch

SCRIPT = Path(__file__).resolve().parents[1] / "whatsapp_reader.py"
spec = importlib.util.spec_from_file_location("reader", SCRIPT)
reader = importlib.util.module_from_spec(spec)
sys.modules["whatsapp_reader"] = reader
spec.loader.exec_module(reader)


class ReaderTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.profile = os.path.join(self.temp.name, "profile")
        self.cache = os.path.join(self.temp.name, "cache.db")
        self.patches = [patch.object(reader, "PROFILE_DIR", self.profile), patch.object(reader, "CACHE_DB_PATH", self.cache)]
        for item in self.patches:
            item.start()
        reader.init_cache_db()

    def tearDown(self):
        for item in reversed(self.patches):
            item.stop()
        self.temp.cleanup()

    def test_import_has_no_profile_side_effect(self):
        self.assertFalse(os.path.exists(self.profile))

    def test_cache_query_does_not_take_browser_profile_lock(self):
        env = dict(os.environ, FRIDA_WHATSAPP_PROFILE=self.profile, FRIDA_WHATSAPP_CACHE=self.cache, PYTHONIOENCODING="utf-8")
        with reader.ProfileLock(self.profile):
            result = subprocess.run([sys.executable, str(SCRIPT), "--search", "nothing"], env=env, capture_output=True, text=True, encoding="utf-8", timeout=10)
            self.assertEqual(json.loads(result.stdout)["status"], "cached")
        with reader.ProfileLock(self.profile):
            pass

    def test_profile_lock_released_after_exception(self):
        with self.assertRaises(ValueError):
            with reader.ProfileLock(self.profile):
                raise ValueError("test")
        with reader.ProfileLock(self.profile):
            pass

    def test_cache_search_requires_no_browser(self):
        reader.save_messages_to_cache("Equipe", [{"sender": "dj", "time": "old", "text": "reuniao"}])
        with patch.object(reader, "get_driver", side_effect=AssertionError("Browser used")):
            result = reader.main(["--search", "reuniao"])
        self.assertEqual(result["status"], "cached")
        self.assertIn("fetched_at", result["results"][0])

    def test_cache_exact_group_avoids_mixing_partial_names(self):
        reader.save_messages_to_cache("PLB", [{"text": "one"}])
        reader.save_messages_to_cache("PLB INTERNO", [{"text": "two"}])
        self.assertEqual([x["text"] for x in reader.query_cached_messages(chat_name="PLB")], ["one"])

    def test_unknown_chat_is_not_saved_as_group(self):
        reader.save_chat_summary([{"name": "Pessoa"}])
        with contextlib.closing(sqlite3.connect(self.cache)) as conn:
            self.assertEqual(conn.execute("SELECT is_group FROM chat_summary").fetchone()[0], 0)

    def test_login_timeout_is_different_from_qr(self):
        with patch.object(reader, "check_login_status", return_value="TIMEOUT"):
            with self.assertRaises(reader.ReaderFailure) as failure:
                reader.require_login(MagicMock())
        self.assertEqual(failure.exception.status, "timeout")

    def test_navigation_timeout_still_checks_loaded_pane(self):
        driver = MagicMock()
        driver.get.side_effect = type("TimeoutException", (Exception,), {})("page load")
        driver.find_elements.return_value = [MagicMock()]
        self.assertEqual(reader.check_login_status(driver), "LOGGED_IN")

    def test_pair_timeout_releases_driver(self):
        driver = MagicMock()
        driver.find_elements.return_value = []
        with patch.object(reader, "get_driver", return_value=driver), patch.object(reader.time, "monotonic", side_effect=[0, 121]):
            with self.assertRaises(reader.ReaderFailure) as failure:
                reader.run_pair()
        self.assertEqual(failure.exception.status, "timeout")
        driver.quit.assert_called_once()

    def test_qr_requirement_reaches_parent(self):
        with patch.object(reader, "check_login_status", return_value="NEEDS_QR"):
            with self.assertRaises(reader.ReaderFailure) as failure:
                reader.require_login(MagicMock())
        self.assertEqual(failure.exception.status, "needs_auth")

    def test_browser_error_does_not_become_qr(self):
        with patch.object(reader, "get_driver", side_effect=reader.ReaderFailure("browser_error", "broken")):
            result = reader.main(["--status"])
        self.assertEqual(result["status"], "browser_error")

    def test_dependency_missing_reported(self):
        with patch.object(reader, "get_driver", side_effect=reader.ReaderFailure("dependency_missing", "missing")):
            self.assertEqual(reader.main(["--status"])["status"], "dependency_missing")

    def test_list_only_confirmed_groups_and_limited(self):
        driver = MagicMock()
        with patch.object(reader, "get_driver", return_value=driver), patch.object(reader, "require_login"), patch.object(reader, "collect_chats", return_value=[{"name": "A", "is_group": True}, {"name": "B", "is_group": True}]):
            result = reader.run_list_groups(1)
        self.assertEqual(result["chats"], [{"name": "A", "is_group": True}])
        self.assertTrue(result["limited_to_loaded"])
        driver.quit.assert_called_once()

    def test_collection_filters_unverified_contacts(self):
        driver = MagicMock()
        driver.execute_script.return_value = [{"name": "Group", "is_group": True}, {"name": "Contact", "is_group": False}, {"name": "Unknown"}]
        with patch.object(reader, "select_groups_filter", return_value=True):
            self.assertEqual(reader.collect_chats(driver), [{"name": "Group", "is_group": True}])

    def test_dom_failure_does_not_become_empty_list(self):
        driver = MagicMock()
        driver.execute_script.return_value = []
        driver.find_elements.return_value = []
        with patch.object(reader, "select_groups_filter", return_value=True):
            with self.assertRaises(reader.ReaderFailure) as failure:
                reader.collect_chats(driver)
        self.assertEqual(failure.exception.status, "dom_changed")

    def test_explicit_empty_list_is_valid(self):
        driver = MagicMock()
        driver.execute_script.return_value = []
        driver.find_elements.return_value = [MagicMock()]
        with patch.object(reader, "select_groups_filter", return_value=True):
            self.assertEqual(reader.collect_chats(driver), [])

    def test_group_filter_must_be_verified(self):
        driver = MagicMock()
        driver.find_elements.return_value = []
        with self.assertRaises(reader.ReaderFailure) as failure:
            reader.select_groups_filter(driver)
        self.assertEqual(failure.exception.status, "dom_changed")

    def test_pair_has_one_result_and_progress_on_stderr(self):
        driver = MagicMock()
        driver.find_elements.return_value = [MagicMock()]
        stdout, stderr = io.StringIO(), io.StringIO()
        with patch.object(reader, "get_driver", return_value=driver), contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            result = reader.run_pair()
        self.assertEqual(stdout.getvalue(), "")
        self.assertIn("QR", stderr.getvalue())
        self.assertTrue(result["authenticated"])
        driver.quit.assert_called_once()

    def test_cli_emits_exactly_one_json(self):
        env = dict(os.environ, FRIDA_WHATSAPP_PROFILE=self.profile, FRIDA_WHATSAPP_CACHE=self.cache, PYTHONIOENCODING="utf-8")
        result = subprocess.run([sys.executable, str(SCRIPT), "--search", "none"], env=env, capture_output=True, text=True, encoding="utf-8", timeout=10)
        self.assertEqual(len(result.stdout.strip().splitlines()), 1)
        self.assertEqual(json.loads(result.stdout)["status"], "cached")

    def test_reader_failure_closes_browser(self):
        driver = MagicMock()
        with patch.object(reader, "get_driver", return_value=driver), patch.object(reader, "require_login", side_effect=reader.ReaderFailure("needs_auth", "QR")):
            with self.assertRaises(reader.ReaderFailure):
                reader.run_list_groups()
        driver.quit.assert_called_once()


if __name__ == "__main__":
    unittest.main()
