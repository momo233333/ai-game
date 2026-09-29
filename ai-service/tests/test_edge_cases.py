import os
import sys
import tempfile
import types
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from config import load_config
from core.asr import FunASRBackend, create_asr
from core.safety import AhoCorasick, SafetyFilter, SafetyVerdict
from core.types import DriveAction, Emotion, SceneContext, to_dict
from pipeline import DialoguePipeline

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def make_cfg():
    cfg = load_config()
    cfg["llm"]["backend"] = "rule"
    cfg["memory"]["db_path"] = os.path.join(tempfile.mkdtemp(), "edge_test.db")
    return cfg


class FakeASR:
    name = "fake"

    def __init__(self, text: str, confidence: float):
        self.text = text
        self.confidence = confidence

    def feed(self, pcm16: bytes):
        return iter(())

    def finalize(self):
        from core.asr import ASRResult

        return ASRResult(self.text, self.confidence)

    def reset(self):
        pass


class FakeVAD:
    name = "fake_vad"

    def __init__(self, never_end: bool = False):
        self.never_end = never_end

    def is_speech(self, pcm16: bytes):
        return True

    def silence_exceeded(self):
        return not self.never_end

    def has_enough_speech(self, min_ms: float = 0.0):
        return True

    def reset(self):
        pass


class PipelineLifecycleTest(unittest.TestCase):
    def test_close_releases_store(self):
        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR)
        pipeline.close()
        with self.assertRaises(Exception):
            pipeline._store._conn.execute("SELECT 1")

    def test_state_property_and_sync(self):
        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR)
        pipeline.sync_state({"trust": 42, "emotion": "HAPPY", "telemetry": {"speed_kmh": 55}})
        self.assertEqual(pipeline.state.trust, 42)
        self.assertEqual(pipeline.state.emotion_name(), "HAPPY")
        self.assertEqual(pipeline.state.telemetry.speed_kmh, 55)
        pipeline.close()

    def test_empty_chunk_sentinel_stops_loop(self):
        pipeline = DialoguePipeline(
            make_cfg(), SERVICE_DIR, asr=FakeASR("左转", 0.9), vad=FakeVAD(never_end=True)
        )
        events = list(pipeline.handle_audio([b"\x01" * 320, b"", b"\x02" * 320]))
        kinds = [e.kind for e in events]
        self.assertIn("drive_action", kinds)
        pipeline.close()

    def test_asr_empty_text_after_high_confidence(self):
        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR, asr=FakeASR("   ", 0.9), vad=FakeVAD())
        events = list(pipeline.handle_audio([b"\x01" * 320]))
        self.assertEqual(events[-1].kind, "error")
        self.assertEqual(events[-1].data["code"], "asr_empty")
        pipeline.close()


class SafetyNetBackstopTest(unittest.TestCase):
    def test_backstop_redacts_when_stream_check_bypassed(self):
        from core.llm import LLMBackend

        class Leaky(LLMBackend):
            name = "leaky"

            def generate(self, ctx):
                return {
                    "action": {"intent": "IGNORE", "steering": 0, "throttle": 0, "brake": 0, "hold_ms": 600, "confidence": 0.5},
                    "tts_hint": None,
                    "reply": "你这个傻逼",
                    "trust_delta": 1,
                }

        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR)
        pipeline.dialogue.backend = Leaky()
        pipeline.safety.check_streaming = lambda text: SafetyVerdict(blocked=False, hits=[], cleaned_text=text)
        events = list(pipeline.handle_text("你好"))
        tts = next(e for e in events if e.kind == "tts_audio").data
        self.assertNotIn("傻逼", tts["text"])
        self.assertLessEqual(next(e for e in events if e.kind == "state_delta").data["trust_delta"], -3)
        pipeline.close()


class SafetyEdgeTest(unittest.TestCase):
    def test_missing_wordlist_uses_defaults(self):
        safety = SafetyFilter(os.path.join(tempfile.gettempdir(), "no_words.txt"))
        self.assertIn("傻逼", safety.words)
        self.assertTrue(safety.check("你傻逼啊").blocked)

    def test_aho_corasick_classic_patterns(self):
        ac = AhoCorasick(["he", "she", "his", "hers"])
        self.assertEqual(sorted(ac.find("ushers")), ["he", "hers", "she"])
        self.assertEqual(sorted(ac.find("ushers!")), ["he", "hers", "she"])
        self.assertEqual(ac.find("xyz"), [])

    def test_partial_word_not_blocked_until_complete(self):
        safety = SafetyFilter(None)
        self.assertFalse(safety.check("白").blocked)
        self.assertTrue(safety.check("白痴").blocked)

    def test_streaming_blocks_on_prefix(self):
        safety = SafetyFilter(os.path.join(SERVICE_DIR, "data", "sensitive_words.txt"))
        self.assertTrue(safety.check_streaming("你是个神经").blocked)
        self.assertFalse(safety.check_streaming("正常句子").blocked)


class FunAsrStubTest(unittest.TestCase):
    def setUp(self):
        module = types.ModuleType("funasr")

        class AutoModel:
            def __init__(self, **kwargs):
                self.kwargs = kwargs

            def generate(self, input=None, is_final=False, chunk_size=None):
                if is_final:
                    return [{"text": "最终结果", "confidence": 0.92}]
                return [{"text": "中间结果", "confidence": 0.55}]

        module.AutoModel = AutoModel
        self._backup = sys.modules.get("funasr")
        sys.modules["funasr"] = module

    def tearDown(self):
        if self._backup is None:
            sys.modules.pop("funasr", None)
        else:
            sys.modules["funasr"] = self._backup

    def test_create_and_stream(self):
        backend = create_asr({"asr": {"backend": "funasr"}})
        self.assertIsInstance(backend, FunASRBackend)
        self.assertEqual(backend.name, "funasr")
        partials = list(backend.feed(b"\x00" * 320))
        self.assertEqual(partials[0].text, "中间结果")
        self.assertLess(partials[0].confidence, 0.6)
        backend.reset()
        final = backend.finalize()
        self.assertEqual(final.text, "最终结果")
        self.assertFalse(final.low_confidence)


class TypesDictTest(unittest.TestCase):
    def test_enum_value(self):
        self.assertEqual(to_dict(Emotion.HAPPY), "HAPPY")

    def test_plain_dataclass(self):
        scene = SceneContext(scene_id="highway")
        self.assertEqual(to_dict(scene)["scene_id"], "highway")
        action = DriveAction(intent="BRAKE", brake=0.9)
        data = to_dict(action)
        self.assertEqual(data["intent"], "BRAKE")
        self.assertAlmostEqual(data["brake"], 0.9)


if __name__ == "__main__":
    unittest.main()
