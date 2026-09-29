import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core.corpus import Corpus
from core.llm import LLMBackend, OpenAICompatBackend, RuleBasedBackend, create_backend, extract_json, sanitize_output
from core.types import GameState, Intent

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REAL_CSV = os.path.join(SERVICE_DIR, "..", "voice-corpus", "lines.csv")


class ExtractJsonTest(unittest.TestCase):
    def test_plain(self):
        self.assertEqual(extract_json('{"a":1}'), '{"a":1}')

    def test_wrapped_in_markdown(self):
        text = '```json\n{"a": 1, "b": "x"}\n```'
        self.assertEqual(extract_json(text), '{"a": 1, "b": "x"}')

    def test_nested_objects(self):
        text = 'prefix {"action":{"intent":"BRAKE"},"reply":"好"} suffix'
        self.assertEqual(extract_json(text), '{"action":{"intent":"BRAKE"},"reply":"好"}')

    def test_no_json(self):
        self.assertEqual(extract_json("hello"), "hello")


class SanitizeOutputTest(unittest.TestCase):
    def test_normal_passthrough(self):
        out = sanitize_output(
            {"action": {"intent": "BRAKE", "steering": 0.1}, "reply": "刹车", "trust_delta": -2},
            {"max_reply_chars": 40},
        )
        self.assertEqual(out["action"]["intent"], "BRAKE")
        self.assertEqual(out["trust_delta"], -2)

    def test_missing_action_defaults_to_ignore(self):
        out = sanitize_output({}, {"max_reply_chars": 40})
        self.assertEqual(out["action"]["intent"], Intent.IGNORE.value)
        self.assertEqual(out["trust_delta"], 0)
        self.assertTrue(out["reply"])

    def test_reply_truncated(self):
        out = sanitize_output({"reply": "字" * 100}, {"max_reply_chars": 10})
        self.assertEqual(len(out["reply"]), 10)

    def test_trust_delta_clamped(self):
        out = sanitize_output({"trust_delta": 999}, {})
        self.assertEqual(out["trust_delta"], 10)
        out = sanitize_output({"trust_delta": -999}, {})
        self.assertEqual(out["trust_delta"], -10)

    def test_garbage_trust_delta(self):
        out = sanitize_output({"trust_delta": "abc"}, {})
        self.assertEqual(out["trust_delta"], 0)

    def test_intent_uppercased(self):
        out = sanitize_output({"action": {"intent": "turn_left"}}, {})
        self.assertEqual(out["action"]["intent"], "TURN_LEFT")


class StreamContractTest(unittest.TestCase):
    def test_action_first_then_reply_then_trust(self):
        corpus = Corpus(REAL_CSV)
        backend = RuleBasedBackend(corpus)
        chunks = list(backend.stream({"player_text": "加速", "game_state": GameState()}))
        keys = []
        for chunk in chunks:
            keys.append(next(iter(chunk.keys())))
        self.assertEqual(keys[0], "action")
        self.assertEqual(keys[1], "tts_hint")
        self.assertIn("reply_delta", keys)
        self.assertEqual(keys[-1], "trust_delta")

    def test_reply_stream_reassembles(self):
        ctx = {"player_text": "刹车", "game_state": GameState()}
        full = RuleBasedBackend(Corpus(REAL_CSV)).generate(ctx)
        chunks = list(RuleBasedBackend(Corpus(REAL_CSV)).stream(ctx, chunk_chars=2))
        reassembled = "".join(c["reply_delta"] for c in chunks if "reply_delta" in c)
        self.assertEqual(reassembled, full["reply"])


class FactoryTest(unittest.TestCase):
    def setUp(self):
        self.corpus = Corpus(REAL_CSV)

    def test_rule_forced(self):
        self.assertEqual(create_backend({"backend": "rule"}, self.corpus, "").name, "rule")

    def test_llama_forced_without_model_raises(self):
        with self.assertRaises(RuntimeError):
            create_backend({"backend": "llama_cpp", "model_path": "nope.gguf"}, self.corpus, "")

    def test_openai_forced_unreachable_raises(self):
        cfg = {"backend": "openai", "openai_base_url": "http://127.0.0.1:59999/v1"}
        with self.assertRaises(RuntimeError):
            create_backend(cfg, self.corpus, "")

    def test_unknown_backend_falls_back_to_rule(self):
        self.assertEqual(create_backend({"backend": "quantum"}, self.corpus, "").name, "rule")


class BackendInterfaceTest(unittest.TestCase):
    def test_custom_backend_can_stream(self):
        class Echo(LLMBackend):
            name = "echo"

            def generate(self, ctx):
                return {
                    "action": {"intent": "BRAKE", "steering": 0, "throttle": 0, "brake": 1, "hold_ms": 500, "confidence": 1},
                    "tts_hint": None,
                    "reply": "刹",
                    "trust_delta": 0,
                }

        chunks = list(Echo().stream({}, chunk_chars=1))
        self.assertEqual(chunks[0]["action"]["intent"], "BRAKE")
        self.assertTrue(any("reply_delta" in c for c in chunks))

    def test_openai_availability_probe_fails_fast(self):
        self.assertFalse(OpenAICompatBackend.is_available("http://127.0.0.1:59999/v1", timeout=0.5))


if __name__ == "__main__":
    unittest.main()
