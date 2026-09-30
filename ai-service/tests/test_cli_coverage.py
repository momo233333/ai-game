"""cli.py 的分支覆盖：渲染、交互循环、语音管线降级、远端 JSON-TCP、main 分发。

设计原则：不依赖真麦克风 / ASR 模型 / 已启动的服务——远端用本机 stub socket，
语音链路用假 pipeline / 假 MicCapture / 假 PTT，全部可在 CI 上跑。
"""
import contextlib
import io
import json
import os
import socket
import sys
import tempfile
import threading
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import cli
from config import load_config
from core.types import TurnEvent

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SAMPLE_WAV = os.path.join(SERVICE_DIR, "tests", "samples", "left_turn.wav")


def make_cfg():
    cfg = load_config()
    cfg["llm"]["backend"] = "rule"
    cfg["memory"]["db_path"] = os.path.join(tempfile.mkdtemp(), "cli_cov.db")
    return cfg


class FakePipeline:
    """最小 pipeline：handle_text / handle_audio 返回一串事件。"""

    def __init__(self, events=None):
        self.events = events or [
            TurnEvent("drive_action", {"steering": -0.6, "throttle": 0.3, "brake": 0.0, "hold_ms": 900, "intent": "TURN_LEFT", "confidence": 0.9, "refused": False}),
            TurnEvent("llm_delta", {"text": "行，左转"}),
            TurnEvent("tts_audio", {"pcm16": b"", "from_voicelib": True, "text": "行，左转", "route": "voicelib", "sample_rate": 24000}),
            TurnEvent("state_delta", {"trust": 61.0, "trust_delta": 1.0, "emotion": "CALM"}),
        ]
        self.audio_calls = 0

    def handle_text(self, text):
        return iter(self.events)

    def handle_audio(self, chunks, stop_on_silence=True):
        self.audio_calls += 1
        list(chunks)  # 真消费一遍上游（wav_chunks / mic.frames）
        return iter(self.events)


class RenderEventTest(unittest.TestCase):
    def _render(self, event):
        buf = io.StringIO()
        cli.render_event(event, buf)
        return buf.getvalue()

    def test_error_event(self):
        out = self._render(TurnEvent("error", {"code": "asr_empty", "message": "未识别到语音"}))
        self.assertIn("[错误]", out)
        self.assertIn("asr_empty", out)

    def test_asr_partial_streaming_subtitle(self):
        out = self._render(TurnEvent("asr_partial", {"text": "转弯", "confidence": 0.7}))
        self.assertIn("[字幕]", out)
        self.assertIn("转弯", out)

    def test_asr_final_and_query_kinds(self):
        self.assertIn("左转", self._render(TurnEvent("asr_final", {"text": "左转", "confidence": 0.9})))
        self.assertIn("[查询]", self._render(TurnEvent("health", {"ok": True})))
        self.assertIn("[查询]", self._render(TurnEvent("ack", {"ok": True})))

    def test_drive_action_unknown_intent_kept_verbatim(self):
        out = self._render(
            TurnEvent("drive_action", {"steering": 0.0, "throttle": 0.0, "brake": 0.0, "hold_ms": 0, "intent": "SMOOTH_DRIVE", "refused": False})
        )
        self.assertIn("SMOOTH_DRIVE", out)

    def test_tts_realtime_route_label(self):
        out = self._render(TurnEvent("tts_audio", {"route": "realtime", "from_voicelib": False, "text": "好吧"}))
        self.assertIn("实时TTS/realtime", out)


class RunLocalInteractiveTest(unittest.TestCase):
    def test_slash_commands_and_quit(self):
        cfg = make_cfg()
        inputs = ["", "/health", "/state", "/event collision", "/event nope", "前面路口左转", "/quit"]
        buf = io.StringIO()
        with mock.patch("builtins.input", side_effect=inputs), contextlib.redirect_stdout(buf):
            code = cli.run_local(cfg, None)
        self.assertEqual(code, 0)
        out = buf.getvalue()
        self.assertIn('"ok"', out)  # /health
        self.assertIn('"trust"', out)  # /state
        self.assertIn("(-12)", out)  # /event collision 有效（撞车 -12）
        self.assertIn("未知事件", out)  # /event nope
        self.assertIn("[动作]", out)  # 普通一轮

    def test_eof_returns_zero(self):
        with mock.patch("builtins.input", side_effect=EOFError), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.run_local(make_cfg(), None), 0)

    def test_keyboard_interrupt_returns_zero(self):
        with mock.patch("builtins.input", side_effect=KeyboardInterrupt), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.run_local(make_cfg(), None), 0)


class VoicePipelineDegradeTest(unittest.TestCase):
    def test_import_error_returns_none(self):
        with mock.patch.dict(sys.modules, {"core.asr": None}), contextlib.redirect_stderr(io.StringIO()):
            self.assertIsNone(cli._build_voice_pipeline(make_cfg()))

    def test_asr_not_ready_returns_none(self):
        with mock.patch("core.asr.create_asr", return_value=None), contextlib.redirect_stderr(io.StringIO()):
            self.assertIsNone(cli._build_voice_pipeline(make_cfg()))

    def test_ready_pipeline_prints_backend(self):
        class StubASR:
            name = "stub"

        out = io.StringIO()
        with mock.patch("core.asr.create_asr", return_value=StubASR()), contextlib.redirect_stdout(out):
            pipeline = cli._build_voice_pipeline(make_cfg())
        self.assertIsNotNone(pipeline)
        self.assertIn("backend=stub", out.getvalue())
        pipeline.close()

    def test_run_mic_returns_1_when_pipeline_unavailable(self):
        with mock.patch.object(cli, "_build_voice_pipeline", return_value=None), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(cli.run_mic(make_cfg()), 1)


class FakeMic:
    def __init__(self, *args, **kwargs):
        pass

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

    def drain(self):
        pass

    def frames(self):
        return iter([b"\x01" * 320])


class FakeMicBoom:
    def __init__(self, *args, **kwargs):
        pass

    def __enter__(self):
        raise RuntimeError("no device")

    def __exit__(self, *exc):
        return False


class FakeMicInterrupt:
    def __init__(self, *args, **kwargs):
        pass

    def __enter__(self):
        raise KeyboardInterrupt

    def __exit__(self, *exc):
        return False


class RunMicTest(unittest.TestCase):
    def test_free_mode_delegates_to_free_loop(self):
        fake = FakePipeline()
        with mock.patch.object(cli, "_build_voice_pipeline", return_value=fake), mock.patch(
            "core.mic.MicCapture", FakeMic
        ), mock.patch.object(cli, "_free_talk_loop", return_value=0) as loop, contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.run_mic(make_cfg(), free=True), 0)
        loop.assert_called_once()

    def test_ptt_mode_delegates_to_ptt_loop(self):
        fake = FakePipeline()
        with mock.patch.object(cli, "_build_voice_pipeline", return_value=fake), mock.patch(
            "core.mic.MicCapture", FakeMic
        ), mock.patch.object(cli, "_ptt_loop", return_value=7) as loop, contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.run_mic(make_cfg(), free=False), 7)
        loop.assert_called_once()

    def test_device_error_returns_1(self):
        with mock.patch.object(cli, "_build_voice_pipeline", return_value=FakePipeline()), mock.patch(
            "core.mic.MicCapture", FakeMicBoom
        ), contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(cli.run_mic(make_cfg()), 1)

    def test_keyboard_interrupt_returns_0(self):
        with mock.patch.object(cli, "_build_voice_pipeline", return_value=FakePipeline()), mock.patch(
            "core.mic.MicCapture", FakeMicInterrupt
        ), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.run_mic(make_cfg()), 0)


class FakePtt:
    """按键序列：等一下 → 按下 → 录一段 → 松开 → 再等时被 Ctrl+C。"""

    key = 162
    _script = [False, True, True, False]

    def __init__(self):
        self.calls = 0

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

    def held(self):
        idx = self.calls
        self.calls += 1
        if idx < len(self._script):
            return self._script[idx]
        raise KeyboardInterrupt


class MicLoopsTest(unittest.TestCase):
    def test_free_talk_loop_renders_and_exits(self):
        class BoomPipeline(FakePipeline):
            def handle_audio(self, chunks, stop_on_silence=True):
                list(chunks)
                yield from self.events
                raise KeyboardInterrupt

        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            code = cli._free_talk_loop(BoomPipeline(), FakeMic())
        self.assertEqual(code, 0)
        self.assertIn("[动作]", buf.getvalue())

    def test_ptt_loop_full_cycle(self):
        fake = FakePipeline()
        buf = io.StringIO()
        with mock.patch("core.ptt.PttListener", FakePtt), mock.patch("time.sleep"), contextlib.redirect_stdout(buf):
            try:
                cli._ptt_loop(fake, FakeMic(), make_cfg())
            except KeyboardInterrupt:
                pass  # 循环尾部的第二次等待被 Ctrl+C 打断（真实使用同路径，由 run_mic 接住）
        out = buf.getvalue()
        self.assertIn("录音中", out)
        self.assertIn("[动作]", out)
        self.assertGreaterEqual(fake.audio_calls, 1)


class RunWavTest(unittest.TestCase):
    def test_missing_asr_returns_1(self):
        with mock.patch.object(cli, "_build_voice_pipeline", return_value=None), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(cli.run_wav(make_cfg(), SAMPLE_WAV), 1)

    def test_existing_wav_renders_events(self):
        buf = io.StringIO()
        with mock.patch.object(cli, "_build_voice_pipeline", return_value=FakePipeline()), contextlib.redirect_stdout(buf):
            code = cli.run_wav(make_cfg(), SAMPLE_WAV)
        self.assertEqual(code, 0)
        self.assertIn("[动作]", buf.getvalue())

    def test_missing_file_returns_1(self):
        err = io.StringIO()
        with mock.patch.object(cli, "_build_voice_pipeline", return_value=FakePipeline()), contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
            self.assertEqual(cli.run_wav(make_cfg(), os.path.join(SERVICE_DIR, "no", "such.wav")), 1)
        self.assertIn("文件不存在", err.getvalue())


class RemoteStubServer(threading.Thread):
    """每个请求一条连接：读一行 JSON，回若干帧后关闭（对齐 run_remote 的收帧循环）。"""

    def __init__(self, responder):
        super().__init__(daemon=True)
        self.responder = responder
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._sock.bind(("127.0.0.1", 0))
        self._sock.listen(16)
        self.port = self._sock.getsockname()[1]
        self._closed = False

    def run(self):
        while True:
            try:
                conn, _ = self._sock.accept()
            except OSError:
                return
            with conn:
                buf = b""
                while b"\n" not in buf:
                    chunk = conn.recv(4096)
                    if not chunk:
                        break
                    buf += chunk
                line = buf.split(b"\n", 1)[0]
                try:
                    request = json.loads(line.decode("utf-8"))
                except (ValueError, UnicodeDecodeError):
                    request = {}
                frames = self.responder(request)
                for frame in ["" ] + frames:  # 前导空行走 blank-line 分支
                    conn.sendall((frame + "\n").encode("utf-8"))

    def close(self):
        if not self._closed:
            self._closed = True
            try:
                self._sock.close()
            except OSError:
                pass


def default_responder(request):
    kind = request.get("type")
    if kind == "ping":
        return [
            json.dumps({"kind": "health", "data": {"ok": True, "llm_backend": "rule"}}, ensure_ascii=False),
        ]
    if kind == "state_query":
        return [json.dumps({"kind": "state", "data": {"trust": 60, "emotion": "CALM"}}, ensure_ascii=False)]
    if kind == "local_event":
        return [json.dumps({"kind": "state_delta", "data": {"trust": 48, "trust_delta": -12, "emotion": "ANNOYED"}}, ensure_ascii=False)]
    frames = [
        json.dumps({"kind": "error", "data": {"code": "demo", "message": "演示错误"}}, ensure_ascii=False),
        json.dumps({"kind": "drive_action", "data": {"steering": -0.6, "throttle": 0.3, "brake": 0.0, "hold_ms": 900, "intent": "TURN_LEFT", "confidence": 0.9, "refused": False}}, ensure_ascii=False),
        json.dumps({"kind": "llm_delta", "data": {"text": "行，左转"}}, ensure_ascii=False),
        json.dumps({"kind": "tts_audio", "data": {"route": "voicelib", "from_voicelib": True, "text": "行，左转", "sample_rate": 24000}}, ensure_ascii=False),
        json.dumps({"kind": "state_delta", "data": {"trust": 61, "trust_delta": 1, "emotion": "CALM"}}, ensure_ascii=False),
        json.dumps({"kind": "end"}, ensure_ascii=False),
    ]
    return frames


class RunRemoteTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = RemoteStubServer(default_responder)
        cls.server.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.close()

    def test_once_text_turn_renders_all_kinds(self):
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            code = cli.run_remote("127.0.0.1", self.server.port, "前面路口左转")
        self.assertEqual(code, 0)
        out = buf.getvalue()
        for marker in ("[查询]", "[错误]", "[动作]", "[TTS ]", "[状态]"):
            self.assertIn(marker, out)

    def test_interactive_slash_commands(self):
        inputs = ["/state", "/event collision", "", "前面路口左转", "/quit"]
        buf = io.StringIO()
        with mock.patch("builtins.input", side_effect=inputs), contextlib.redirect_stdout(buf):
            code = cli.run_remote("127.0.0.1", self.server.port, None)
        self.assertEqual(code, 0)
        out = buf.getvalue()
        self.assertIn('"trust"', out)  # state_query
        self.assertIn("(-12)", out)  # local_event 回执（state_delta）
        self.assertIn("[动作]", out)  # 普通文本轮次

    def test_interactive_eof_returns_zero(self):
        with mock.patch("builtins.input", side_effect=EOFError), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.run_remote("127.0.0.1", self.server.port, None), 0)


class MainDispatchTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = RemoteStubServer(default_responder)
        cls.server.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.close()

    def test_server_flag_uses_remote(self):
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            code = cli.main(["--server", f"127.0.0.1:{self.server.port}", "--text", "前面路口左转"])
        self.assertEqual(code, 0)
        self.assertIn("[动作]", buf.getvalue())

    def test_mic_flag_delegates(self):
        with mock.patch.object(cli, "run_mic", return_value=3) as run_mic:
            self.assertEqual(cli.main(["--mic", "--free"]), 3)
        run_mic.assert_called_once()
        self.assertTrue(run_mic.call_args.kwargs.get("free"))

    def test_wav_flag_delegates(self):
        with mock.patch.object(cli, "run_wav", return_value=4) as run_wav:
            self.assertEqual(cli.main(["--wav", "some.wav"]), 4)
        run_wav.assert_called_once()


if __name__ == "__main__":
    unittest.main()
