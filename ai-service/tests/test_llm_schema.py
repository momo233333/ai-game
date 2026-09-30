import contextlib
import io
import json
import os
import sys
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from config import DEFAULTS, load_config
from core.llm import (
    LLM_JSON_SCHEMA,
    OUTPUT_KEY_ORDER,
    OpenAICompatBackend,
    build_response_format,
)
from core.types import ALLOWED_INTENTS

STATE = {"mode": "accept"}
PAYLOADS: list[dict] = []

REPLY = {
    "action": {"intent": "TURN_LEFT", "steering": -0.6, "throttle": 0.3, "brake": 0, "hold_ms": 1200, "confidence": 0.85},
    "tts_hint": None,
    "reply": "知道了，左转交给我。",
    "trust_delta": 1,
}


class _SchemaHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        body = json.dumps({"data": [{"id": "fake-model"}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        payload = json.loads(self.rfile.read(length))
        PAYLOADS.append(payload)
        mode = STATE["mode"]
        if mode == "reject_all" or (mode == "reject" and "response_format" in payload):
            self.send_response(400)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"error":"unsupported response_format"}')
            return
        body = json.dumps(
            {"choices": [{"message": {"content": json.dumps(REPLY, ensure_ascii=False)}}], "model": payload.get("model")}
        ).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


class SchemaShapeTest(unittest.TestCase):
    def test_top_level_contract_matches_output_keys(self):
        self.assertEqual(set(LLM_JSON_SCHEMA["required"]), set(OUTPUT_KEY_ORDER))
        self.assertFalse(LLM_JSON_SCHEMA["additionalProperties"])
        self.assertEqual(LLM_JSON_SCHEMA["type"], "object")
        json.dumps(LLM_JSON_SCHEMA)

    def test_intent_enum_matches_allowed_intents(self):
        action = LLM_JSON_SCHEMA["properties"]["action"]
        enum = action["properties"]["intent"]["enum"]
        self.assertEqual(set(enum), ALLOWED_INTENTS)
        self.assertFalse(action["additionalProperties"])

    def test_confidence_optional_but_core_action_fields_required(self):
        required = set(LLM_JSON_SCHEMA["properties"]["action"]["required"])
        self.assertLessEqual({"intent", "steering", "throttle", "brake", "hold_ms"}, required)
        self.assertNotIn("confidence", required)

    def test_tts_hint_allows_null(self):
        hint = LLM_JSON_SCHEMA["properties"]["tts_hint"]
        self.assertIn({"type": "null"}, hint["anyOf"])
        self.assertIn({"type": "string"}, hint["anyOf"])

    def test_build_response_format_modes(self):
        self.assertIsNone(build_response_format({"json_schema": "off"}))
        self.assertIsNone(build_response_format({"json_schema": "false"}))
        self.assertIsNone(build_response_format({"json_schema": "0"}))
        for mode in ("auto", "on", "anything"):
            rf = build_response_format({"json_schema": mode})
            self.assertEqual(rf["type"], "json_schema")
            self.assertEqual(rf["json_schema"]["name"], "copilot_reply")
            self.assertIs(rf["json_schema"]["schema"], LLM_JSON_SCHEMA)

    def test_config_defaults_expose_switch(self):
        self.assertEqual(DEFAULTS["llm"]["json_schema"], "auto")
        self.assertEqual(load_config().get("llm", {}).get("json_schema"), "auto")
        self.assertEqual(build_response_format(DEFAULTS["llm"])["type"], "json_schema")


class SchemaTransportTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), _SchemaHandler)
        cls.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.cfg = {
            "openai_base_url": f"http://127.0.0.1:{cls.port}/v1",
            "openai_model": "fake-model",
        }

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def setUp(self):
        STATE["mode"] = "accept"
        PAYLOADS.clear()

    def _generate(self, extra_cfg):
        backend = OpenAICompatBackend({**self.cfg, **extra_cfg}, "sys")
        out = backend.generate({"player_text": "往左转", "max_reply_chars": 40})
        return backend, out

    def test_auto_sends_json_schema_and_parses(self):
        STATE["mode"] = "accept"
        backend, out = self._generate({"json_schema": "auto"})
        rf = PAYLOADS[-1].get("response_format")
        self.assertIsNotNone(rf)
        self.assertEqual(rf["type"], "json_schema")
        self.assertEqual(rf["json_schema"]["schema"], LLM_JSON_SCHEMA)
        self.assertFalse(backend.schema_degraded)
        self.assertEqual(out["action"]["intent"], "TURN_LEFT")
        self.assertEqual(out["reply"], "知道了，左转交给我。")

    def test_off_never_sends_response_format(self):
        backend, out = self._generate({"json_schema": "off"})
        self.assertNotIn("response_format", PAYLOADS[-1])
        self.assertFalse(backend.schema_degraded)
        self.assertEqual(out["action"]["intent"], "TURN_LEFT")

    def test_degrades_when_server_rejects_json_schema(self):
        STATE["mode"] = "reject"
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            backend, out = self._generate({"json_schema": "auto"})
        self.assertTrue(backend.schema_degraded)
        self.assertIn("json_schema", stderr.getvalue())
        self.assertIn("降级", stderr.getvalue())
        self.assertIn("response_format", PAYLOADS[0])
        self.assertNotIn("response_format", PAYLOADS[-1])
        self.assertEqual(len(PAYLOADS), 2)
        self.assertEqual(out["action"]["intent"], "TURN_LEFT")

    def test_error_propagates_when_retry_also_fails(self):
        STATE["mode"] = "reject_all"
        backend = OpenAICompatBackend({**self.cfg, "json_schema": "auto"}, "sys")
        with self.assertRaises(Exception):
            backend.generate({"player_text": "刹车", "max_reply_chars": 40})
        self.assertTrue(backend.schema_degraded)
        self.assertEqual(len(PAYLOADS), 2)


if __name__ == "__main__":
    unittest.main()
