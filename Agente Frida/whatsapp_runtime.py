"""Managed WhatsApp reader. JSONL protocol v2; legacy --search stays cache-only."""
import argparse
import contextlib
import importlib.util
import json
import os
import queue
import re
import sqlite3
import sys
import threading
import time
import uuid
from datetime import datetime, timezone

import whatsapp_reader as legacy
from whatsapp_dom import WebUI, CHATS_JS, MESSAGES_JS, SEARCH_ROWS_JS, normalized, digest, chat_name_matches

START_SECONDS, LOAD_SECONDS, PAIR_SECONDS, COLLECT_SECONDS, IDLE_SECONDS = 30, 60, 180, 120, 600
# An empty chat search is double-checked by reading this many loaded history pages.
SCAN_PAGES = 8
# Group overview (read_recent_groups): WhatsApp renders only ~5-15 messages and each
# scroll goes back one screen, so a busy group needs many pages to reach "ontem".
# Each group gets a time budget (within COLLECT_SECONDS) instead of a page count.
RECENT_GROUPS, RECENT_GROUP_MESSAGES, RECENT_GROUP_SECONDS, RECENT_SETTLE_SECONDS = 6, 10, 18, .6
# Homologation forces short collection blocks to exercise cursors; production keeps 120 s.
try:
    COLLECT_SECONDS = max(5, min(120, int(os.environ.get("FRIDA_WHATSAPP_COLLECT_SECONDS", COLLECT_SECONDS))))
except ValueError:
    pass


class ReaderFailure(legacy.ReaderFailure):
    def __init__(self, status, message, **data):
        super().__init__(status, message)
        self.data = data


CACHE_VERSION = 3


def init_cache():
    os.makedirs(os.path.dirname(os.path.abspath(legacy.CACHE_DB_PATH)), exist_ok=True)
    with legacy.cache_connection() as conn:
        version = conn.execute("PRAGMA user_version").fetchone()[0]
        if version >= CACHE_VERSION:
            return
        if conn.execute("SELECT count(*) FROM sqlite_master WHERE type='table'").fetchone()[0]:
            backup = legacy.CACHE_DB_PATH + ".pre-v" + str(CACHE_VERSION) + "-" + uuid.uuid4().hex[:8] + ".bak"
            with contextlib.closing(sqlite3.connect(backup)) as dest:
                conn.backup(dest)
        conn.execute("CREATE TABLE IF NOT EXISTS messages_v2 (message_key TEXT PRIMARY KEY, chat_id TEXT NOT NULL, chat_name TEXT, chat_type TEXT, timestamp TEXT, text TEXT NOT NULL, payload TEXT NOT NULL, fetched_at TEXT NOT NULL)")
        conn.execute("CREATE INDEX IF NOT EXISTS messages_v2_chat_date ON messages_v2(chat_id,timestamp)")
        conn.execute("CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(message_key UNINDEXED,text,tokenize='unicode61 remove_diacritics 2')")
        conn.execute("CREATE TABLE IF NOT EXISTS search_cursors (token TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, payload TEXT NOT NULL, updated_at TEXT NOT NULL)")
        # v3: chat ids anchored to a rendered message changed with scroll position.
        # Those keys cannot be deduplicated or resumed; drop them (backup above).
        stale = [row[0] for row in conn.execute("SELECT message_key, payload FROM messages_v2").fetchall()
                 if json.loads(row[1]).get("identity_scope") == "message_anchor"]
        for key in stale:
            conn.execute("DELETE FROM messages_v2 WHERE message_key=?", (key,))
            conn.execute("DELETE FROM messages_fts WHERE message_key=?", (key,))
        if stale:
            conn.execute("DELETE FROM search_cursors")
        conn.execute("PRAGMA user_version=" + str(CACHE_VERSION))
    legacy.init_cache_db()


def save_records(records):
    with legacy.cache_connection() as conn:
        for record in records:
            if not record.get("source_verified") or not record.get("message_id") or not record.get("chat_id"):
                raise ReaderFailure("identity_unverified", "Registro sem identidade de origem.")
            key = digest(record["chat_id"] + "|" + record["message_id"])
            now = datetime.now(timezone.utc).isoformat()
            conn.execute("INSERT OR REPLACE INTO messages_v2 VALUES (?,?,?,?,?,?,?,?)", (key, record["chat_id"], record["chat"], record["chat_type"], record.get("timestamp"), record["text"], json.dumps(record, ensure_ascii=False), now))
            conn.execute("DELETE FROM messages_fts WHERE message_key=?", (key,))
            conn.execute("INSERT INTO messages_fts(message_key,text) VALUES (?,?)", (key, record["text"]))


def query_cache(request):
    clauses, params = [], []
    if request.get("query"):
        clauses.append("m.message_key IN (SELECT message_key FROM messages_fts WHERE messages_fts MATCH ?)")
        params.append('"' + request["query"].replace('"', '""') + '"')
    if request.get("chat_id"):
        clauses.append("m.chat_id=? COLLATE NOCASE")
        params.append(request["chat_id"])
    if request.get("chat_type", "all") != "all":
        clauses.append("m.chat_type=?")
        params.append(request["chat_type"])
    for field, operator in (("date_from", ">="), ("date_to", "<")):
        if request.get(field):
            clauses.append("julianday(m.timestamp)" + operator + "julianday(?)")
            params.append(request[field])
    exact_clauses = list(clauses)
    exact_params = list(params)
    if request.get("chat_name"):
        exact_clauses.append("m.chat_name=? COLLATE NOCASE")
        exact_params.append(request["chat_name"])
    where = " WHERE " + " AND ".join(exact_clauses) if exact_clauses else ""
    limit = request.get("limit", 20)
    with legacy.cache_connection() as conn:
        direction = "ASC" if request.get("order") == "oldest" else "DESC"
        rows = conn.execute("SELECT m.payload FROM messages_v2 m" + where + " ORDER BY julianday(m.timestamp) " + direction + " LIMIT ?", exact_params + [limit]).fetchall()
        result = [json.loads(row[0]) for row in rows]
        if not result and request.get("chat_name"):
            base_where = " WHERE " + " AND ".join(clauses) if clauses else ""
            candidates = conn.execute("SELECT m.chat_name, m.payload FROM messages_v2 m" + base_where + " ORDER BY julianday(m.timestamp) " + direction, params).fetchall()
            for chat_name, payload in candidates:
                if chat_name_matches(chat_name or "", request["chat_name"]):
                    result.append(json.loads(payload))
                    if len(result) >= limit:
                        break
    if not result and not any(request.get(k) for k in ("chat_id", "date_from", "date_to")) and request.get("chat_type", "all") == "all":
        result = legacy.query_cached_messages(request.get("chat_name"), request.get("query"), limit)
        for item in result:
            item["source_verified"] = False
    return result


def fingerprint(request):
    # Once the conversation is resolved, its verified id replaces the spoken name.
    keys = ("operation", "query", "chat_id", "chat_type", "date_from", "date_to", "order") if request.get("chat_id") else \
           ("operation", "query", "chat_name", "chat_id", "chat_type", "date_from", "date_to", "order")
    return digest(json.dumps({k: request.get(k, "") for k in keys}, sort_keys=True))


def load_cursor(request):
    token = request.get("cursor")
    if not token:
        return {"seen": [], "rows": [], "offset": 0}
    with legacy.cache_connection() as conn:
        row = conn.execute("SELECT fingerprint,payload FROM search_cursors WHERE token=?", (token,)).fetchone()
    if not row or row[0] != fingerprint(request):
        raise ReaderFailure("invalid_cursor", "O ponto de retomada nao pertence a esta pesquisa.")
    return json.loads(row[1])


def save_cursor(request, state):
    token = request.get("cursor") or uuid.uuid4().hex
    with legacy.cache_connection() as conn:
        conn.execute("INSERT OR REPLACE INTO search_cursors VALUES (?,?,?,?)", (token, fingerprint(request), json.dumps(state), datetime.now(timezone.utc).isoformat()))
    return token


SYNONYM_GROUPS = (
    {"peca", "pecas", "pc", "pcs"},
    {"quantidade", "quantidades", "qtd", "qtde"},
    {"equipamento", "equipamentos", "eqpto", "equip"},
    {"numero", "numeros", "num", "nro"},
)


def query_text_matches(text, query):
    norm_text = normalized(text)
    norm_query = normalized(query)
    if not norm_query:
        return True
    if norm_query in norm_text:
        return True
    text_tokens = set(re.split(r"[^a-z0-9]+", norm_text))
    query_tokens = [t for t in re.split(r"[^a-z0-9]+", norm_query) if t and t not in ("de", "da", "do", "das", "dos", "e", "o", "a", "os", "as")]
    if not query_tokens:
        return False
    for qt in query_tokens:
        if qt in text_tokens or qt in norm_text:
            continue
        group = next((g for g in SYNONYM_GROUPS if qt in g), None)
        if group and any(syn in text_tokens for syn in group):
            continue
        return False
    return True


def matches_request(item, request):
    if request.get("chat_type", "all") not in ("all", item.get("chat_type")):
        return False
    if request.get("chat_id") and item.get("chat_id") != request["chat_id"]:
        return False
    if request.get("chat_name") and not chat_name_matches(item.get("chat"), request["chat_name"]):
        return False
    if request.get("query") and not query_text_matches(item.get("text"), request["query"]):
        return False
    if request.get("query"):
        for number in re.findall(r'\b\d+\b', request["query"]):
            if not re.search(r'(?<!\d)' + re.escape(number) + r'(?!\d)', item.get("text", "")):
                return False
    if request.get("date_from") or request.get("date_to"):
        if not item.get("timestamp"):
            return False
        stamp = datetime.fromisoformat(item["timestamp"])
        if request.get("date_from") and stamp < datetime.fromisoformat(request["date_from"]):
            return False
        if request.get("date_to") and stamp >= datetime.fromisoformat(request["date_to"]):
            return False
    return True


def validate_request(request):
    request = dict(request)
    operation = request.get("operation")
    if operation not in ("status", "pair", "list_groups", "list_chats", "read_unread", "read_group", "read_chat", "read_recent_groups", "search", "search_cache", "diagnostics"):
        raise ReaderFailure("unsupported_read", "Operacao desconhecida.")
    request["chat_name"] = request.get("chat_name") or request.get("group_name") or ""
    request["chat_type"] = request.get("chat_type") or ("group" if operation in ("read_group", "list_groups") else "all")
    if request["chat_type"] not in ("all", "group", "contact"):
        raise ReaderFailure("invalid_filter", "Tipo de conversa invalido.")
    if operation in ("read_chat", "read_group") and not request["chat_name"]:
        raise ReaderFailure("missing_chat", "Informe a conversa.")
    if (operation == "search" or (operation == "search_cache" and not request["chat_name"])) and not str(request.get("query") or "").strip():
        raise ReaderFailure("missing_query", "Informe o termo de busca.")
    for field in ("date_from", "date_to"):
        if request.get(field):
            try:
                if not datetime.fromisoformat(request[field]).tzinfo:
                    raise ValueError()
            except (ValueError, TypeError):
                raise ReaderFailure("invalid_date", "Data deve conter fuso horario.")
    if request.get("date_from") and request.get("date_to") and datetime.fromisoformat(request["date_from"]) >= datetime.fromisoformat(request["date_to"]):
        raise ReaderFailure("invalid_date", "Intervalo de datas invalido.")
    if operation == "read_recent_groups" and (request["chat_type"] != "group" or not request.get("date_from") or not request.get("date_to") or request["chat_name"]):
        raise ReaderFailure("invalid_filter", "A leitura de grupos recentes requer grupos e um período definido.")
    try:
        request["chat_candidate"] = int(request.get("chat_candidate") or 0)
    except (ValueError, TypeError):
        raise ReaderFailure("invalid_filter", "Opcao de conversa invalida.")
    if not 0 <= request["chat_candidate"] <= 20 or (request["chat_candidate"] and not request["chat_name"]):
        raise ReaderFailure("invalid_filter", "Opcao de conversa invalida.")
    request["limit"] = max(1, min(100, int(request.get("limit", 20))))
    request["order"] = request.get("order") or "newest"
    if request["order"] not in ("newest", "oldest"):
        raise ReaderFailure("invalid_order", "Ordenacao invalida.")
    return request


class ReaderSession:
    def __init__(self, emit=lambda event: None, cancel=None):
        self.emit, self.cancel = emit, cancel or threading.Event()
        self.driver, self.lock, self.request_id = None, None, ""
        self.stage, self.stage_started, self.started = "idle", time.monotonic(), time.monotonic()
        self.metrics, self.collect_deadline = {}, None
        self.authenticated = False

    def progress(self, stage, message, timeout=None):
        now = time.monotonic()
        self.metrics[self.stage] = self.metrics.get(self.stage, 0) + round((now - self.stage_started) * 1000)
        self.stage, self.stage_started = stage, now
        self.emit({"event": "progress", "request_id": self.request_id, "stage": stage, "message": message,
                   "timeout_seconds": timeout or COLLECT_SECONDS, "elapsed_ms": round((now - self.started) * 1000)})

    def checkpoint(self):
        if self.cancel.is_set():
            raise ReaderFailure("user_cancelled", "Consulta cancelada pelo usuario.")
        if self.collect_deadline and time.monotonic() >= self.collect_deadline:
            raise ReaderFailure("collection_deadline", "Limite deste bloco de coleta.")

    def close(self):
        self.authenticated = False
        if self.driver:
            legacy.close_driver(self.driver)
            self.driver = None
        if self.lock:
            self.lock.__exit__()
            self.lock = None

    def connect(self, pair=False, embedded=False):
        if self.driver and pair:
            legacy.close_driver(self.driver)
            self.driver = None
        if not self.lock:
            self.lock = legacy.ProfileLock(legacy.PROFILE_DIR)
            self.lock.__enter__()
        if not self.driver:
            self.authenticated = False
            self.progress("browser_start", "Iniciando leitor do WhatsApp.", START_SECONDS)
            self.driver = legacy.get_driver(headless=not pair or embedded)
        self.progress("pairing" if pair else "authentication", "Leia o QR Code pelo celular." if pair else "Verificando sessao do WhatsApp.", PAIR_SECONDS + 20 if pair else LOAD_SECONDS + 20)
        status = "LOGGED_IN" if self.authenticated and not pair and self.driver.find_elements("id", "pane-side") else legacy.check_login_status(self.driver, LOAD_SECONDS if not pair else 5)
        if pair and status != "LOGGED_IN":
            deadline = time.monotonic() + PAIR_SECONDS
            previous_qr = None
            while time.monotonic() < deadline:
                self.checkpoint()
                if self.driver.find_elements("id", "pane-side"):
                    status = "LOGGED_IN"
                    break
                if embedded:
                    elements = self.driver.find_elements("css selector", "canvas[aria-label*='Scan'], canvas[aria-label*='QR'], div[data-ref]")
                    if elements:
                        qr = elements[0].screenshot_as_base64
                        if qr != previous_qr:
                            self.emit({"event": "qr", "request_id": self.request_id, "png_base64": qr})
                            previous_qr = qr
                time.sleep(.2)
        if status != "LOGGED_IN":
            self.authenticated = False
            raise ReaderFailure("needs_auth" if status == "NEEDS_QR" and not pair else "timeout", "Sessao precisa de QR Code." if status == "NEEDS_QR" and not pair else "O WhatsApp nao confirmou a conexao no prazo.")
        self.authenticated = True
        return {"status": "success", "authenticated": True}

    def recent_groups(self, request):
        ui = WebUI(self.driver, ReaderFailure, self.checkpoint, self.progress)
        self.progress("collecting", "Lendo mensagens nos grupos recentes do WhatsApp.", COLLECT_SECONDS)
        self.collect_deadline = time.monotonic() + COLLECT_SECONDS
        records, issues, inspected = {}, [], 0
        try:
            ui.reset_search()
            try:
                legacy.select_groups_filter(self.driver)
                rows = self.driver.execute_script(CHATS_JS, True)
            except legacy.ReaderFailure as exc:
                if exc.status != "dom_changed":
                    raise
                # Custom lists can replace the Groups tab. Use previously verified
                # names only to select candidates; verify each header and read live.
                with legacy.cache_connection() as conn:
                    known = {normalized(name) for (name,) in conn.execute(
                        "SELECT DISTINCT chat_name FROM messages_v2 WHERE chat_type='group'")}
                visible = self.driver.execute_script(CHATS_JS, False)
                if not isinstance(visible, list):
                    raise ReaderFailure("dom_changed", "A lista de conversas não pôde ser lida.")
                rows = [dict(row, is_group=True) for row in visible if normalized(row.get("name")) in known]
                if not rows:
                    raise ReaderFailure("missing_groups_filter", "A lista visível não contém grupos já identificados. Informe o nome do grupo que deseja consultar.")
                issues.append("visible_known_groups")
            if not isinstance(rows, list) or (not rows and not ui.empty_state()):
                raise ReaderFailure("dom_changed", "O leitor não conseguiu identificar a lista de grupos.")
            names = list(dict.fromkeys(row["name"] for row in rows if row.get("is_group") and row.get("name")))[:RECENT_GROUPS]
            for position, name in enumerate(names):
                self.checkpoint()
                try:
                    identity = ui.open_chat(name, kind="group")
                    if normalized(identity["chat"]) != normalized(name) or identity["chat_type"] != "group":
                        raise ReaderFailure("chat_mismatch", "O cabeçalho não confirmou o grupo.")
                    # Split what is left of the collection block among the remaining groups.
                    share = (self.collect_deadline - time.monotonic()) / (len(names) - position)
                    messages = self.period_messages(ui, identity, request, max(1.0, min(RECENT_GROUP_SECONDS, share)))
                    inspected += 1
                    recent = sorted((m for m in messages if m.get("source_verified") and matches_request(m, request)),
                                    key=lambda m: m["timestamp"], reverse=True)[:RECENT_GROUP_MESSAGES]
                    records.update((m["reference"], m) for m in recent)
                except ReaderFailure as exc:
                    if exc.status in ("user_cancelled", "collection_deadline"):
                        raise
                    issues.append(exc.status)
            if names and not inspected:
                raise ReaderFailure("dom_changed", "Nenhum dos grupos pôde ser conferido: " + ", ".join(sorted(set(issues))))
        except ReaderFailure as exc:
            if exc.status != "collection_deadline" or not inspected:
                raise
            issues.append(exc.status)
        finally:
            self.collect_deadline = None
        messages = sorted(records.values(), key=lambda m: m["timestamp"], reverse=True)[:request["limit"]]
        save_records(messages)
        return {"status": "partial", "source": "browser_dom_snapshot", "messages": messages,
                "coverage": {"complete": False, "scope": "recent_group_sample", "reason": "visible_history_boundary",
                             "groups_read": inspected, "issues": sorted(set(issues)),
                             "date_from": request["date_from"], "date_to": request["date_to"]}}

    def period_messages(self, ui, identity, request, seconds):
        # Scroll back one screen at a time (never skipping, so no in-period message is
        # jumped over) until a message older than the period start is loaded, enough
        # newest in-period messages are collected, history ends or the budget runs out.
        # The DOM is virtualized, so messages are accumulated across pages.
        start = datetime.fromisoformat(request["date_from"])
        stop_at = time.monotonic() + seconds
        found, stuck = {}, 0
        while True:
            batch = ui.messages(identity)
            found.update((m["reference"], m) for m in batch)
            in_period = sum(1 for m in found.values() if m.get("source_verified") and matches_request(m, request))
            if in_period >= RECENT_GROUP_MESSAGES or time.monotonic() >= stop_at or any(
                    m.get("timestamp") and datetime.fromisoformat(m["timestamp"]) < start for m in batch):
                break
            move = ui.scroll("#main", -1)
            if not move:
                break
            # At the top of the loaded history WhatsApp may still be fetching older
            # messages; only three still pages in a row mean the history really ended.
            stuck = stuck + 1 if not move.get("moved") and move.get("boundary") else 0
            if stuck >= 3:
                break
            deadline = min(time.monotonic() + RECENT_SETTLE_SECONDS, stop_at)
            if self.collect_deadline:
                deadline = min(deadline, self.collect_deadline)
            while time.monotonic() < deadline:
                self.checkpoint()
                time.sleep(.1)
        return list(found.values())

    def collect(self, request):
        state = load_cursor(request)
        seen, visited = set(state["seen"]), set(state["rows"])
        records, chats, issues = [], [], []
        ui = WebUI(self.driver, ReaderFailure, self.checkpoint, self.progress)
        operation = request["operation"]
        self.progress("collecting", "Pesquisando mensagens no WhatsApp.", COLLECT_SECONDS)
        self.collect_deadline = time.monotonic() + COLLECT_SECONDS
        complete, reason, identity = False, "", None
        last_page, unchanged, offset = None, 0, state.get("offset", 0)
        attribution = []
        try:
            if operation in ("read_chat", "read_group") or (operation == "search" and request["chat_name"]):
                # On resume the verified id finds the conversation even if list order changed.
                candidate = 0 if request.get("chat_id") else request.get("chat_candidate", 0)
                identity = ui.open_chat(request["chat_name"], request.get("chat_id", ""), request["chat_type"], candidate)
                request = dict(request, chat_id=identity["chat_id"], chat_name=identity["chat"])
            if operation == "search":
                ui.search(request["query"], within=bool(identity))
            elif operation not in ("read_chat", "read_group"):
                ui.reset_search()
                if request["chat_type"] == "group":
                    legacy.select_groups_filter(self.driver)
            scan, scan_pages = False, 0
            while True:
                self.checkpoint()
                batch = []
                reopened_search = False
                if operation in ("read_chat", "read_group") or scan:
                    candidates = ui.messages(identity)
                    page_key = [x["message_id"] for x in candidates]
                    for item in candidates:
                        if item["reference"] not in seen and matches_request(item, request):
                            batch.append(item)
                    scan_pages += 1
                elif operation == "search":
                    rows = self.driver.execute_script(SEARCH_ROWS_JS, bool(identity)) or []
                    page_key = [x["key"] for x in rows]
                    if not identity and "history_sync_in_progress" not in issues and ui.sync_in_progress():
                        # Initial history download indicator: WhatsApp's index is still filling.
                        issues.append("history_sync_in_progress")
                    if not rows and ui.empty_state(within=bool(identity)):
                        if identity:
                            # Observed: the chat index can report "Nenhuma mensagem" for a
                            # message already rendered. Confirm against the loaded history.
                            scan = True
                            continue
                        if "history_sync_in_progress" in issues:
                            reason = "history_sync_in_progress"
                            break
                        complete, reason = True, "confirmed_empty"
                        break
                    if not rows and not ui.search_active(request["query"], within=bool(identity)):
                        if records:
                            reason = "search_panel_changed"
                            break
                        raise ReaderFailure("dom_changed", "Resultados vazios sem confirmacao de ausencia.")
                    for row in rows:
                        self.checkpoint()
                        row_key = digest(row["key"])
                        if row_key in visited:
                            continue
                        try:
                            try:
                                row["element"].click()
                            except Exception as click_exc:
                                if type(click_exc).__name__ in ("ElementClickInterceptedException", "ElementNotInteractableException", "MoveTargetOutOfBoundsException"):
                                    self.driver.execute_script("arguments[0].scrollIntoView({block:'center'}); arguments[0].click();", row["element"])
                                else:
                                    raise
                            target_id = row.get("message_id")
                            target_text = (row.get("text") or "").strip('\u202a\u202b\u202c\u200e\u200f ')
                            def target_loaded():
                                if target_id:
                                    # Rendered is enough; extractability is checked below, once.
                                    return bool(self.driver.execute_script("return !!document.querySelector('#main [data-id=\"'+CSS.escape(arguments[0])+'\"]');", target_id))
                                for item in self.driver.execute_script(MESSAGES_JS) or []:
                                    if normalized(target_text or request["query"]) in normalized(item.get("text")):
                                        return True
                                return False
                            ui.wait(target_loaded, seconds=20, label="conteudo da mensagem selecionada")
                            selected = ui.chat_identity(request["chat_name"] or row.get("name") or None)
                            if identity:
                                if selected["chat_type"] != identity["chat_type"] or normalized(selected["chat"]) != normalized(identity["chat"]):
                                    raise ReaderFailure("chat_mismatch", "Resultado abriu outra conversa.")
                                # The verified chat's own search drawer navigates only within
                                # that chat. Keep its identity while older messages load.
                                selected = identity
                            candidates = ui.messages(selected)
                            if target_id and not any(x["message_id"] == target_id for x in candidates):
                                # WhatsApp matched it, but its text/date could not be verified.
                                issues.append("unextractable_hit")
                            if (request.get("date_from") or request.get("date_to")) and any(not x.get("timestamp") for x in candidates):
                                issues.append("unparsed_timestamp")
                            batch.extend(x for x in candidates if x["reference"] not in seen and matches_request(x, request))
                            visited.add(row_key)
                            if identity and not ui.search_active(request["query"], within=True):
                                # Narrow layouts close the search drawer when a hit is
                                # opened. Restore the same query, then reacquire rows.
                                ui.search(request["query"], within=True)
                                reopened_search = True
                                break
                        except ReaderFailure as exc:
                            if exc.status in ("collection_deadline", "user_cancelled"):
                                raise
                            # Recorded once; retrying the same hit on every page only burns the deadline.
                            issues.append(exc.status)
                            visited.add(row_key)
                        except Exception as exc:
                            if type(exc).__name__ not in ("StaleElementReferenceException", "ElementClickInterceptedException", "ElementNotInteractableException"):
                                raise
                            issues.append("stale_search_row" if type(exc).__name__ == "StaleElementReferenceException" else "intercepted_search_row")
                            visited.add(row_key)
                else:
                    current = self.driver.execute_script(CHATS_JS, request["chat_type"] == "group")
                    if not isinstance(current, list):
                        raise ReaderFailure("dom_changed", "Lista nao reconhecida.")
                    page_key = [x["chat_id"] or x["name"] + "|" + x["snippet"] for x in current]
                    if not current and ui.empty_state():
                        complete, reason = True, "confirmed_empty"
                        break
                    if not current:
                        raise ReaderFailure("dom_changed", "Lista vazia sem confirmacao de ausencia.")
                    for item in current:
                        key = digest(item["chat_id"] or item["name"] + "|" + item["snippet"])
                        if key not in seen and (operation != "read_unread" or item["unread"] > 0):
                            if request["chat_type"] == "contact":
                                try:
                                    verified = ui.open_chat(item["name"], kind="contact")
                                    item.update(verified)
                                    item["is_group"] = False
                                except ReaderFailure as exc:
                                    issues.append(exc.status)
                                    continue
                            seen.add(key)
                            chats.append(item)
                if batch:
                    unique = {x["reference"]: x for x in batch if x["reference"] not in seen}
                    batch = list(unique.values())
                    save_records(batch)
                    records.extend(batch)
                    seen.update(unique)
                    self.emit({"event": "batch", "request_id": self.request_id, "source": "browser_dom_snapshot", "messages": batch})
                if reopened_search:
                    unchanged = 0
                    continue
                history = operation in ("read_chat", "read_group") or scan
                if scan and scan_pages >= SCAN_PAGES:
                    break
                if operation in ("read_chat", "read_group"):
                    reached_date_start = bool(request.get("date_from")) and any(
                        c.get("timestamp") and datetime.fromisoformat(c["timestamp"]) < datetime.fromisoformat(request["date_from"])
                        for c in candidates
                    )
                    if reached_date_start or scan_pages >= SCAN_PAGES or len(records) >= request.get("limit", 40):
                        reason = "visible_history_boundary"
                        break
                elif operation in ("read_unread", "list_chats", "list_groups"):
                    if len(chats) >= request.get("limit", 20) or offset >= 4:
                        complete = not issues
                        reason = "search_results_exhausted" if complete else "visible_history_boundary"
                        break
                selector = "#main" if history else ('[data-testid="drawer-right"], [data-testid="chat-search"]' if identity else "#pane-side")
                move = ui.scroll(selector, -1 if history else 1)
                unchanged = unchanged + 1 if page_key == last_page else 0
                last_page, offset = page_key, offset + 1
                if not move:
                    raise ReaderFailure("dom_changed", "Painel de resultados nao reconhecido.")
                if not move["moved"] and move["boundary"] and unchanged >= 3:
                    complete = not history and not issues
                    reason = "visible_history_boundary" if not complete else "search_results_exhausted"
                    break
                # Let the virtualized list settle. Bounded polling also checks cancellation.
                deadline = min(self.collect_deadline, time.monotonic() + .8)
                while time.monotonic() < deadline:
                    self.checkpoint()
                    time.sleep(.1)
        except ReaderFailure as exc:
            if exc.status not in ("collection_deadline", "user_cancelled") and not records and not chats:
                raise
            reason = exc.status
        except Exception as exc:
            if not records and not chats:
                raise
            reason = "browser_error"
            issues.append(type(exc).__name__)
        finally:
            self.collect_deadline = None
        if operation in ("read_chat", "read_group") and not request.get("cursor"):
            cache_req = dict(request, limit=max(request.get("limit", 50), 80))
            for cached_item in query_cache(cache_req):
                ref = cached_item.get("reference")
                if ref and ref not in seen and cached_item.get("source_verified") and matches_request(cached_item, request):
                    records.append(cached_item)
                    seen.add(ref)
        if operation == "search" and not identity and records:
            attribution = self.check_homonyms(ui, records)
        if scan and reason not in ("collection_deadline", "user_cancelled"):
            # The chat index said "no messages": an empty double-check confirms it; hits
            # found only by reading the history mean the index was behind.
            complete, reason = (True, "confirmed_empty") if not records else (False, "search_index_missed_messages")
        if issues:
            complete, reason = False, reason or "unverified_results"
        records.sort(key=lambda x: x.get("timestamp") or "", reverse=request["order"] == "newest")
        state.update(seen=list(seen), rows=list(visited), offset=offset)
        # "continue" cannot help while WhatsApp itself is still syncing history.
        resumable = not complete and ("history_sync_in_progress" not in issues or reason == "collection_deadline")
        cursor = save_cursor(request, state) if resumable else None
        key = "chats" if operation in ("list_chats", "list_groups") else "unread_chats" if operation == "read_unread" else "results" if operation == "search" else "messages"
        return {"schema_version": 2, "status": "success" if complete else "partial", "source": "browser_dom_snapshot", key: chats if key in ("chats", "unread_chats") else records,
                "coverage": {"complete": complete, "scope": "accessible_web_search" if operation == "search" else "loaded_web_history", "reason": reason, "issues": sorted(set(issues)), "pages": offset, "returned": len(records) + len(chats)},
                "cursor": cursor, "groups_filter_verified": request["chat_type"] == "group", "group_header_verified": bool(identity), "group": identity["chat"] if identity else "",
                "chat_identity": identity, "limited_to_loaded": not complete, "attribution_warnings": attribution}

    def check_homonyms(self, ui, records, budget_seconds=15, max_chats=8):
        """Global hits carry only a display name. Flag names shared by several
        conversations instead of implying which one sent the message."""
        warnings, chats = [], {}
        for item in records:
            chats.setdefault((normalized(item["chat"]), item["chat_type"]), item["chat"])
        deadline = time.monotonic() + budget_seconds
        for (key, kind), name in list(chats.items())[:max_chats]:
            if time.monotonic() >= deadline:
                warnings.append({"chat": name, "chat_type": kind, "status": "unchecked"})
                continue
            try:
                count = len([x for x in ui.find_chats(name) if x.get("exact")])
            except ReaderFailure as exc:
                if exc.status == "user_cancelled":
                    raise
                warnings.append({"chat": name, "chat_type": kind, "status": "unchecked"})
                continue
            if count > 1:
                warnings.append({"chat": name, "chat_type": kind, "status": "homonymous", "conversations": count})
                changed = [x for x in records if normalized(x["chat"]) == key and x["chat_type"] == kind]
                for item in changed:
                    item["identity_scope"] = "ambiguous_display_name"
                    item["attribution_warning"] = str(count) + " conversas tem este nome; a origem exata nao foi distinguida."
                save_records(changed)
        for name in list(chats.values())[max_chats:]:
            warnings.append({"chat": name, "status": "unchecked"})
        return warnings

    def execute(self, request):
        self.request_id = str(request.get("request_id") or uuid.uuid4().hex)
        self.started, self.stage_started, self.metrics = time.monotonic(), time.monotonic(), {}
        self.stage, self.collect_deadline = "validation", None
        try:
            request = validate_request(request)
            init_cache()
            operation = request["operation"]
            if operation == "search_cache":
                result = {"status": "cached", "source": "local_cache_snapshot", "results": query_cache(request), "coverage": {"complete": False, "reason": "cache_only"}}
            elif operation == "diagnostics":
                result = {"status": "success", "python": sys.executable, "selenium_available": importlib.util.find_spec("selenium") is not None, "profile": legacy.PROFILE_DIR, "cache": legacy.CACHE_DB_PATH, "browser_running": self.driver is not None}
            else:
                if request.get("embedded"):
                    self.connect(pair=operation == "pair", embedded=True)
                else:
                    self.connect(pair=operation == "pair")
                if operation == "read_recent_groups":
                    result = self.recent_groups(request)
                else:
                    result = {"status": "LOGGED_IN", "authenticated": True} if operation == "status" else {"status": "success", "authenticated": True} if operation == "pair" else self.collect(request)
        except legacy.ReaderFailure as exc:
            result = {"status": exc.status, "message": exc.message, **getattr(exc, "data", {})}
        except Exception as exc:
            result = {"status": "timeout" if "Timeout" in type(exc).__name__ else "browser_error", "message": "Falha no leitor: " + type(exc).__name__, "detail": str(exc)[:1600]}
        self.metrics[self.stage] = self.metrics.get(self.stage, 0) + round((time.monotonic() - self.stage_started) * 1000)
        result.update(schema_version=2, request_id=self.request_id, stage=self.stage, timings_ms=self.metrics, elapsed_ms=round((time.monotonic() - self.started) * 1000))
        return result


def serve():
    pending, cancel, output_lock = queue.Queue(), threading.Event(), threading.Lock()
    def emit(event):
        with output_lock:
            print(json.dumps(event, ensure_ascii=False), flush=True)
    session = ReaderSession(emit, cancel)
    def read_input():
        # Bytes, decoded per line: .NET may prefix the pipe with a UTF-8 BOM, and a
        # malformed line must be reported, never end the input loop.
        for raw in sys.stdin.buffer:
            try:
                line = raw.decode("utf-8-sig").strip()
                if not line:
                    continue
                request = json.loads(line)
                if not isinstance(request, dict):
                    raise ValueError()
                if request.get("operation") == "cancel":
                    if request.get("request_id") == session.request_id:
                        cancel.set()
                else:
                    pending.put(request)
            except (ValueError, TypeError):
                emit({"event": "protocol_error", "message": "JSON de entrada invalido."})
        cancel.set()
        pending.put(None)
    threading.Thread(target=read_input, daemon=True).start()
    emit({"event": "ready", "schema_version": 2, "pid": os.getpid()})
    try:
        while True:
            try:
                request = pending.get(timeout=IDLE_SECONDS)
            except queue.Empty:
                break
            if request is None:
                break
            cancel.clear()
            result = session.execute(request)
            emit({"event": "result", "request_id": session.request_id, "result": result})
    finally:
        session.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description="Leitor WhatsApp da Frida")
    options = parser.add_mutually_exclusive_group(required=True)
    for option in ("status", "pair", "list-groups", "list-chats", "read-unread", "serve", "diagnostics"):
        options.add_argument("--" + option, action="store_true")
    for option in ("read-group", "read-chat", "search", "search-live"):
        options.add_argument("--" + option)
    parser.add_argument("--limit", type=int, default=20)
    parser.add_argument("--chat-name", default="")
    parser.add_argument("--chat-type", default="all")
    parser.add_argument("--date-from", default="")
    parser.add_argument("--date-to", default="")
    parser.add_argument("--cursor", default="")
    parser.add_argument("--chat-candidate", type=int, default=0)
    args = parser.parse_args(argv)
    if args.serve:
        serve()
        return None
    operation = next(name for name in ("status", "pair", "list_groups", "list_chats", "read_unread", "read_group", "read_chat", "search", "search_live", "diagnostics") if getattr(args, name))
    request = {"operation": "search_cache" if operation == "search" else "search" if operation == "search_live" else operation, "query": args.search or args.search_live or "", "chat_name": args.read_group or args.read_chat or args.chat_name, "chat_type": "group" if operation in ("read_group", "list_groups") else args.chat_type, "date_from": args.date_from, "date_to": args.date_to, "cursor": args.cursor, "limit": args.limit, "chat_candidate": args.chat_candidate}
    session = ReaderSession(lambda event: print(json.dumps(event, ensure_ascii=False), file=sys.stderr, flush=True))
    try:
        return session.execute(request)
    finally:
        session.close()
