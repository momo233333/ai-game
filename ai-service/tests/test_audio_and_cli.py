import contextlib
import io
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import cli
from config import load_config
from core.asr import ASRResult, TextStubASR
from core.vad import EnergyVAD
from pipeline import DialoguePipeline

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def make_cfg():
    cfg = load_config()
    cfg["llm"]["backend"] = "rule"
    cfg["memory"]["db_path"] = os.path.join(tempfile.mkdtemp(), "audio_test.db")
    return cfg


class FakeASR:
    name = "fake"

    def __init__(self, text: str, confidence: float):
        self.text = text
        self.confidence = confidence
        self.finalized = False

    def feed(self, pcm16: bytes):
        if self.text:
            yield ASRResult(self.text[: max(1, len(self.text) // 2)], self.confidence)

    def finalize(self) -> ASRResult:
        self.finalized = True
        return ASRResult(self.text, self.confidence)

    def reset(self):
        pass


class FakeVAD(EnergyVAD):
    name = "fake_vad"

    def __init__(self):
        super().__init__(threshold=0.02, min_silence_ms=0)
        self.frames = 0

    def is_speech(self, pcm16: bytes) -> bool:
        self.frames += 1
        self.speech_started = True
        return True

    def silence_exceeded(self) -> bool:
        return self.frames >= 2

    def has_enough_speech(self, min_ms: float = 0.0) -> bool:
        return True


class AudioDegradationTest(unittest.TestCase):
    def test_no_asr_yields_error_event(self):
        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR)
        events = list(pipeline.handle_audio([b"\x00" * 320]))
        self.assertEqual(events[0].kind, "error")
        self.assertEqual(events[0].data["code"], "asr_unavailable")

    def test_empty_audio_with_asr(self):
        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR, asr=FakeASR("左转", 0.9), vad=FakeVAD())
        events = list(pipeline.handle_audio([]))
        self.assertEqual(events[0].kind, "error")
        self.assertEqual(events[0].data["code"], "empty_audio")

    def test_high_confidence_path_reaches_action(self):
        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR, asr=FakeASR("往左打方向", 0.9), vad=FakeVAD())
        events = list(pipeline.handle_audio([b"\x01" * 320, b"\x02" * 320]))
        kinds = [e.kind for e in events]
        self.assertIn("asr_partial", kinds)
        self.assertIn("asr_final", kinds)
        self.assertIn("drive_action", kinds)
        action = next(e for e in events if e.kind == "drive_action").data
        self.assertEqual(action["intent"], "TURN_LEFT")

    def test_low_confidence_triggers_question_back(self):
        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR, asr=FakeASR("往左", 0.3), vad=FakeVAD())
        events = list(pipeline.handle_audio([b"\x01" * 320]))
        kinds = [e.kind for e in events]
        self.assertIn("drive_action", kinds)
        action = next(e for e in events if e.kind == "drive_action").data
        self.assertEqual(action["intent"], "QUESTION_BACK")
        self.assertNotIn("state_delta", kinds)
        final = next(e for e in events if e.kind == "asr_final")
        self.assertTrue(final.data["low_confidence"])

    def test_recognized_text_flows_into_dialogue(self):
        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR, asr=FakeASR("靠边停车", 0.95), vad=FakeVAD())
        events = list(pipeline.handle_audio([b"\x01" * 320]))
        kinds = [e.kind for e in events]
        self.assertIn("tts_audio", kinds)
        self.assertIn("state_delta", kinds)


class TextStubTest(unittest.TestCase):
    def test_stub_returns_configured_text(self):
        stub = TextStubASR("右转", 0.8)
        self.assertEqual(stub.finalize().text, "右转")
        self.assertEqual(list(stub.feed(b"")), [])


class CliSmokeTest(unittest.TestCase):
    def test_local_once_exits_zero(self):
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = cli.main(["--text", "前面路口左转"])
        self.assertEqual(code, 0)
        out = buffer.getvalue()
        self.assertIn("[动作]", out)
        self.assertIn("↰左转", out)
        self.assertIn("[状态]", out)

    def test_local_insult_blocked(self):
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = cli.main(["--text", "你这个傻逼"])
        self.assertEqual(code, 0)
        out = buffer.getvalue()
        self.assertIn("—观望", out)
        self.assertIn("(-8)", out)


if __name__ == "__main__":
    unittest.main()
