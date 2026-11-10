import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("score_voice", Path(__file__).with_name("score_voice_evaluation.py"))
score = importlib.util.module_from_spec(spec)
spec.loader.exec_module(score)


@unittest.skipUnless(importlib.util.find_spec("jiwer"), "Instale tests/requirements-voice-eval.txt")
class VoiceScoringTests(unittest.TestCase):
    def row(self, **fields):
        value = dict(id="1", variant="turbo", split="test", session="session-a", condition="normal",
                     reference="Não envie 3213 para Hilton.", hypothesis="Não envie 3213 para Hilton.",
                     status="Ok", operator_voice=True, speaker_accepted=True, elapsed_ms=100, estimated_usd=.001,
                     critical_terms=["Não", "3213", "Hilton"])
        value.update(fields)
        return value

    def group(self, rows):
        return next(r for r in score.summarize(rows)["groups"] if r["condition"] == "all")

    def test_rejected_operator_remains_in_denominator(self):
        result = self.group([self.row(speaker_accepted=False)])
        self.assertEqual(result["wer_stt"], 0)
        self.assertEqual(result["wer_with_speaker_gate"], 1)
        self.assertEqual(result["exact_commands"], 0)

    def test_negation_and_identifier_are_not_normalized_away(self):
        result = self.group([self.row(hypothesis="Envie 321 para Hilton")])
        self.assertGreater(result["wer_stt"], 0)
        self.assertEqual(result["critical_all_present"], 0)

    def test_silence_hallucination_is_counted(self):
        result = self.group([self.row(reference="", operator_voice=False, hypothesis="Abra")])
        self.assertIsNone(result["wer_stt"])
        self.assertEqual(result["negative_accepted_texts"], 1)

    def test_duplicate_or_session_leakage_is_rejected(self):
        with self.assertRaises(ValueError):
            score.summarize([self.row(), self.row()])
        with self.assertRaises(ValueError):
            score.summarize([self.row(), self.row(id="2", split="calibration")])

    def test_unpaired_results_do_not_claim_comparability(self):
        report = score.summarize([self.row(), self.row(id="2", variant="large")])
        self.assertFalse(report["paired_same_ids"])
        self.assertEqual(report["acceptance"], "pending_physical_validation")


if __name__ == "__main__":
    unittest.main()
