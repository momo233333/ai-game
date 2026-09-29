import contextlib
import io
import json
import os
import socket
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from config import load_config
from server import JsonServer, build_pipeline, serve


class JsonProtocolTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cfg = load_config()
        cfg["llm"]["backend"] = "rule"
        cfg["memory"]["db_path"] = os.path.join(tempfile.mkdtemp(), "server_test.db")
        cls.pipeline = build_pipeline(cfg)
        cls.server = JsonServer("127.0.0.1", 0, cls.pipeline)
        cls.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        time.sleep(0.1)

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def rpc(self, payload: dict) -> list[dict]:
        with socket.create_connection(("127.0.0.1", self.port), timeout=5) as sock:
            sock.sendall((json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8"))
            buf = b""
            messages: list[dict] = []
            sock.settimeout(5)
            while True:
                chunk = sock.recv(8192)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    if not line.strip():
                        continue
                    msg = json.loads(line.decode("utf-8"))
                    if msg.get("kind") == "end":
                        return messages
                    messages.append(msg)
        return messages

    def test_ping_health(self):
        messages = self.rpc({"type": "ping"})
        self.assertEqual(messages[0]["kind"], "health")
        self.assertTrue(messages[0]["data"]["ok"])
        self.assertEqual(messages[0]["data"]["llm_backend"], "rule")

    def test_text_turn_event_order(self):
        messages = self.rpc({"type": "text_turn", "text": "前面路口左转"})
        kinds = [m["kind"] for m in messages]
        self.assertEqual(kinds[0], "asr_final")
        self.assertIn("drive_action", kinds)
        self.assertLess(kinds.index("drive_action"), kinds.index("llm_delta"))
        self.assertLess(kinds.index("llm_delta"), kinds.index("tts_audio"))
        self.assertEqual(kinds[-1], "state_delta")
        action = next(m for m in messages if m["kind"] == "drive_action")
        self.assertEqual(action["data"]["intent"], "TURN_LEFT")

    def test_sync_and_query_state(self):
        ack = self.rpc(
            {
                "type": "sync_state",
                "state": {
                    "trust": 33,
                    "emotion": "ANNOYED",
                    "scene": {"scene_id": "highway"},
                    "telemetry": {"speed_kmh": 90, "gear": 5, "obstacle_summary": "前方拥堵"},
                    "level_id": "L9",
                },
            }
        )
        self.assertTrue(ack[0]["data"]["ok"])
        state = self.rpc({"type": "state_query"})[0]["data"]
        self.assertEqual(state["trust"], 33)
        self.assertEqual(state["scene"]["scene_id"], "highway")
        self.assertEqual(state["telemetry"]["speed_kmh"], 90)
        self.assertEqual(state["level_id"], "L9")

    def test_local_event_updates_trust(self):
        before = self.rpc({"type": "state_query"})[0]["data"]["trust"]
        messages = self.rpc({"type": "local_event", "event": "collision"})
        delta = next(m for m in messages if m["kind"] == "state_delta")
        self.assertEqual(delta["data"]["trust_delta"], -12)
        after = self.rpc({"type": "state_query"})[0]["data"]["trust"]
        self.assertAlmostEqual(after, before - 12)

    def test_unknown_local_event(self):
        messages = self.rpc({"type": "local_event", "event": "explode"})
        self.assertEqual(messages[0]["kind"], "error")

    def test_audio_turn_degrades_to_text_hint(self):
        import base64

        with mock.patch.object(self.pipeline, "_asr", None):
            messages = self.rpc({"type": "audio_turn", "chunks": [base64.b64encode(b"\x00" * 320).decode()]})
        self.assertEqual(messages[0]["kind"], "error")
        self.assertEqual(messages[0]["data"]["code"], "asr_unavailable")

    def test_audio_turn_with_active_asr(self):
        import base64

        if self.pipeline._asr is None:
            self.skipTest("no asr backend installed")
        messages = self.rpc(
            {"type": "audio_turn", "chunks": [base64.b64encode(b"\x00" * 3200).decode()]}
        )
        kinds = [m["kind"] for m in messages]
        self.assertIn("asr_final", kinds)

    def test_unknown_type(self):
        messages = self.rpc({"type": "warp_drive"})
        self.assertEqual(messages[0]["kind"], "error")
        self.assertEqual(messages[0]["data"]["code"], "unknown_type")

    def test_malformed_json(self):
        with socket.create_connection(("127.0.0.1", self.port), timeout=5) as sock:
            sock.sendall(b"not-json\n")
            data = sock.recv(4096)
        payload = json.loads(data.decode("utf-8").splitlines()[0])
        self.assertEqual(payload["kind"], "error")
        self.assertEqual(payload["data"]["code"], "bad_json")

    def test_two_clients_sequentially(self):
        first = self.rpc({"type": "text_turn", "text": "刹车"})
        second = self.rpc({"type": "ping"})
        self.assertTrue(any(m["kind"] == "state_delta" for m in first))
        self.assertEqual(second[0]["kind"], "health")


class ServeEntryTest(unittest.TestCase):
    def _cfg(self, transport=None):
        cfg = load_config()
        cfg["llm"]["backend"] = "rule"
        cfg["memory"]["db_path"] = os.path.join(tempfile.mkdtemp(), "serve_test.db")
        cfg["server"]["host"] = "127.0.0.1"
        cfg["server"]["port"] = 0
        if transport:
            cfg["server"]["transport"] = transport
        return cfg

    def _ping(self, port: int) -> dict:
        with socket.create_connection(("127.0.0.1", port), timeout=5) as sock:
            sock.sendall(b'{"type": "ping"}\n')
            line = sock.recv(4096).split(b"\n")[0]
        return json.loads(line.decode("utf-8"))

    def test_serve_json_non_blocking(self):
        cfg = self._cfg("json")
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            server = serve(cfg, block=False)
        try:
            self.assertIn("JSON-TCP", buffer.getvalue())
            port = server.server_address[1]
            payload = self._ping(port)
            self.assertEqual(payload["kind"], "health")
            self.assertTrue(payload["data"]["ok"])
        finally:
            server.shutdown()
            server.server_close()

    def test_serve_auto_falls_back_when_grpc_missing(self):
        cfg = self._cfg()
        with contextlib.redirect_stdout(io.StringIO()):
            server = serve(cfg, block=False)
        try:
            payload = self._ping(server.server_address[1])
            self.assertEqual(payload["kind"], "health")
        finally:
            server.shutdown()
            server.server_close()

    def test_forced_grpc_without_dependency_exits(self):
        cfg = self._cfg()
        with self.assertRaises(SystemExit):
            with contextlib.redirect_stdout(io.StringIO()):
                serve(cfg, "grpc", block=False)

    def test_main_parses_args_and_blocks(self):
        import server as server_module

        cfg = self._cfg("json")
        with mock.patch.object(server_module, "serve", return_value=None) as fake:
            with contextlib.redirect_stdout(io.StringIO()):
                server_module.main(["--transport", "json", "--port", "12345"])
            args, kwargs = fake.call_args
            self.assertEqual(args[1], "json")
            self.assertEqual(args[0]["server"]["port"], 12345)


if __name__ == "__main__":
    unittest.main()
