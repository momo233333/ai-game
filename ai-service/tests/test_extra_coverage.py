import contextlib
import io
import json
import os
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import cli
from config import load_config
from core.asr import create_asr
from core.llm import LLMBackend, OpenAICompatBackend, create_backend
from core.corpus import Corpus
from core.tts import SynthesisResult, TTSRouter, VoiceLib
from core.types import clamp, emotion_from_value, to_dict
from pipeline import DialoguePipeline
from server import JsonServer, build_pipeline

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REAL_CSV = os.path.join(SERVICE_DIR, "..", "voice-corpus", "lines.csv")
MANIFEST = os.path.join(SERVICE_DIR, "..", "voice-corpus", "manifest.json")


def make_cfg():
    cfg = load_config()
    cfg["llm"]["backend"] = "rule"
    cfg["memory"]["db_path"] = os.path.join(tempfile.mkdtemp(), "extra_test.db")
    return cfg


class FakeBackend(LLMBackend):
    name = "fake"

    def __init__(self, payload: dict):
        self.payload = payload

    def generate(self, ctx):
        return dict(self.payload)


class OutputSafetyTest(unittest.TestCase):
    def test_llm_reply_with_insult_is_redacted(self):
        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR)
        pipeline.dialogue.backend = FakeBackend(
            {
                "action": {"intent": "TURN_LEFT", "steering": -0.5, "throttle": 0.3, "brake": 0, "hold_ms": 800, "confidence": 0.9},
                "tts_hint": None,
                "reply": "你这个傻逼快点",
                "trust_delta": 1,
            }
        )
        events = list(pipeline.handle_text("左转"))
        reply = "".join(e.data["text"] for e in events if e.kind == "llm_delta")
        self.assertNotIn("傻逼", reply)
        self.assertLessEqual(next(e for e in events if e.kind == "state_delta").data["trust_delta"], -3)


class DangerousIntentFromLLMTest(unittest.TestCase):
    def test_parser_refuses_and_replaces_reply(self):
        pipeline = DialoguePipeline(make_cfg(), SERVICE_DIR)
        pipeline.dialogue.backend = FakeBackend(
            {
                "action": {"intent": "RUN_RED_LIGHT", "steering": 0, "throttle": 1.0, "brake": 0, "hold_ms": 1000, "confidence": 1.0},
                "tts_hint": None,
                "reply": "冲过去咯",
                "trust_delta": -5,
            }
        )
        events = list(pipeline.handle_text("走吧"))
        action = next(e for e in events if e.kind == "drive_action").data
        self.assertTrue(action["refused"])
        self.assertEqual(action["intent"], "IGNORE")
        reply = "".join(e.data["text"] for e in events if e.kind == "llm_delta")
        self.assertEqual(reply, "这种话我不接，想都别想。")
        self.assertLessEqual(next(e for e in events if e.kind == "state_delta").data["trust_delta"], -5)


class RealtimeRouteTest(unittest.TestCase):
    class OkTTS:
        available = True
        name = "fake_tts"

        def synth(self, text, ref_text="", emotion="CALM"):
            return SynthesisResult(ok=True, pcm16=b"\x01\x02")

    class BrokenTTS:
        available = True
        name = "fake_tts"

        def synth(self, text, ref_text="", emotion="CALM"):
            return SynthesisResult(ok=False, reason="model_missing")

    class OffTTS:
        available = False
        name = "fake_tts"

    def test_realtime_success(self):
        router = TTSRouter({"tts": {"voicelib_enabled": False, "realtime_enabled": True}}, None, realtime=self.OkTTS())
        result = router.route("随便说点什么吧")
        self.assertEqual(result.kind, "realtime")
        self.assertEqual(result.pcm16, b"\x01\x02")
        self.assertFalse(result.from_voicelib)
        self.assertEqual(router.name, "fake_tts")

    def test_realtime_failure_reports_reason(self):
        router = TTSRouter({"tts": {"voicelib_enabled": False, "realtime_enabled": True}}, None, realtime=self.BrokenTTS())
        result = router.route("随便说点什么吧")
        self.assertEqual(result.kind, "realtime_failed")
        self.assertEqual(result.reason, "model_missing")

    def test_realtime_off_falls_to_silent(self):
        router = TTSRouter({"tts": {"voicelib_enabled": False, "realtime_enabled": True}}, None, realtime=self.OffTTS())
        result = router.route("随便说点什么吧")
        self.assertEqual(result.kind, "silent")
        self.assertEqual(router.name, "silent")


class VoiceLibWavTest(unittest.TestCase):
    def test_missing_wav_returns_none(self):
        lib = VoiceLib(MANIFEST)
        entry = lib.entries[0]
        entry.wav_path = "does_not_exist.wav"
        self.assertIsNone(lib.wav_bytes(entry))

    def test_manifest_missing_is_not_loaded(self):
        lib = VoiceLib(os.path.join(tempfile.gettempdir(), "no_manifest.json"))
        self.assertFalse(lib.loaded)
        self.assertEqual(lib.entries, [])


class AsrFactoryTest(unittest.TestCase):
    def test_none_backend(self):
        self.assertIsNone(create_asr({"asr": {"backend": "none"}}))

    def test_auto_without_funasr(self):
        self.assertIsNone(create_asr({"asr": {"backend": "auto"}}))


class TypesHelperTest(unittest.TestCase):
    def test_emotion_from_value(self):
        self.assertEqual(emotion_from_value("happy").value, "HAPPY")
        self.assertEqual(emotion_from_value("nonsense").value, "CALM")
        from core.types import Emotion

        self.assertEqual(emotion_from_value(Emotion.CALM), Emotion.CALM)

    def test_clamp(self):
        self.assertEqual(clamp(5, 0, 1), 1)
        self.assertEqual(clamp(-5, 0, 1), 0)
        self.assertEqual(clamp(0.5, 0, 1), 0.5)

    def test_to_dict_nested(self):
        data = to_dict({"a": [1, {"b": (2, 3)}]})
        self.assertEqual(data, {"a": [1, {"b": [2, 3]}]})


class _FakeLLMHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        body = json.dumps({"data": [{"id": "fake-model"}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        payload = json.loads(self.rfile.read(length))
        reply = {
            "action": {"intent": "BRAKE", "steering": 0, "throttle": 0, "brake": 0.8, "hold_ms": 700, "confidence": 0.9},
            "tts_hint": None,
            "reply": "刹住了",
            "trust_delta": 1,
        }
        body = json.dumps(
            {"choices": [{"message": {"content": json.dumps(reply, ensure_ascii=False)}}], "model": payload.get("model")}
        ).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


class OpenAIBackendTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), _FakeLLMHandler)
        cls.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.cfg = {"openai_base_url": f"http://127.0.0.1:{cls.port}/v1", "openai_model": "fake-model"}

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def test_probe_and_generate(self):
        base = self.cfg["openai_base_url"]
        self.assertTrue(OpenAICompatBackend.is_available(base, timeout=2))
        backend = OpenAICompatBackend(self.cfg, "sys")
        out = backend.generate({"player_text": "刹车", "max_reply_chars": 40})
        self.assertEqual(out["action"]["intent"], "BRAKE")
        self.assertEqual(out["reply"], "刹住了")
        self.assertEqual(out["trust_delta"], 1)

    def test_auto_selects_openai_when_available(self):
        corpus = Corpus(REAL_CSV)
        cfg = {"backend": "auto", "model_path": "missing.gguf", **self.cfg}
        backend = create_backend(cfg, corpus, "")
        self.assertEqual(backend.name, "openai")


class CliInteractiveTest(unittest.TestCase):
    def test_local_interactive_commands(self):
        buffer = io.StringIO()
        commands = ["/health", "/state", "/event collision", "前面路口左转", "/quit"]
        with mock.patch("builtins.input", side_effect=commands), contextlib.redirect_stdout(buffer):
            code = cli.main([])
        self.assertEqual(code, 0)
        out = buffer.getvalue()
        self.assertIn("llm_backend", out)
        self.assertIn("city_01", out)
        self.assertIn("-12", out)
        self.assertIn("[动作]", out)

    def test_local_unknown_event(self):
        buffer = io.StringIO()
        with mock.patch("builtins.input", side_effect=["/event explode", "/quit"]), contextlib.redirect_stdout(buffer):
            cli.main([])
        self.assertIn("未知事件", buffer.getvalue())


class CliRemoteTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cfg = make_cfg()
        cls.pipeline = build_pipeline(cfg)
        cls.server = JsonServer("127.0.0.1", 0, cls.pipeline)
        cls.port = cls.server.server_address[1]
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def test_remote_text(self):
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = cli.main(["--server", f"127.0.0.1:{self.port}", "--text", "右转"])
        self.assertEqual(code, 0)
        out = buffer.getvalue()
        self.assertIn("↱右转", out)
        self.assertIn("[TTS ]", out)

    def test_remote_interactive(self):
        buffer = io.StringIO()
        commands = ["/state", "/event collision", "加速", "/quit"]
        with mock.patch("builtins.input", side_effect=commands), contextlib.redirect_stdout(buffer):
            code = cli.main(["--server", f"127.0.0.1:{self.port}"])
        self.assertEqual(code, 0)
        out = buffer.getvalue()
        self.assertIn("[查询]", out)
        self.assertIn("(-12)", out)
        self.assertIn("[动作]", out)

    def test_remote_insult(self):
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            cli.main(["--server", f"127.0.0.1:{self.port}", "--text", "滚开"])
        self.assertIn("(-8)", buffer.getvalue())


if __name__ == "__main__":
    unittest.main()
