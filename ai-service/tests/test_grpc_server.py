import importlib.util
import os
import socket
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

GRPC_READY = (
    importlib.util.find_spec("grpc") is not None
    and importlib.util.find_spec("proto.copilot_pb2_grpc") is not None
)


def _free_port() -> int:
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    return port


@unittest.skipUnless(GRPC_READY, "grpcio / copilot_pb2_grpc 不可用")
class GrpcServicerTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import grpc
        from config import load_config
        from proto import copilot_pb2, copilot_pb2_grpc
        from server import _try_start_grpc, build_pipeline

        cfg = load_config()
        cfg["llm"]["backend"] = "rule"
        cfg["memory"]["db_path"] = os.path.join(tempfile.mkdtemp(), "grpc_test.db")
        cls.copilot_pb2 = copilot_pb2
        cls.pipeline = build_pipeline(cfg)
        cls.port = _free_port()
        cls.server = _try_start_grpc(cls.pipeline, "127.0.0.1", cls.port)
        if cls.server is None:
            raise unittest.SkipTest("gRPC 服务未能启动")
        cls.channel = grpc.insecure_channel(f"127.0.0.1:{cls.port}")
        cls.stub = copilot_pb2_grpc.AIServiceStub(cls.channel)

    @classmethod
    def tearDownClass(cls):
        channel = getattr(cls, "channel", None)
        if channel is not None:
            channel.close()
        server = getattr(cls, "server", None)
        if server is not None:
            server.stop(0)

    def test_ping_reports_backends(self):
        health = self.stub.Ping(self.copilot_pb2.Empty(), timeout=5)
        self.assertTrue(health.ok)
        self.assertEqual(health.llm_backend, "rule")
        self.assertTrue(health.version)

    def test_text_turn_event_order_matches_contract(self):
        stream = self.stub.TextTurn(self.copilot_pb2.TextRequest(text="前面路口左转"), timeout=10)
        events = list(stream)
        kinds = [e.WhichOneof("payload") for e in events]
        self.assertEqual(kinds[0], "asr_final")
        self.assertIn("drive_action", kinds)
        self.assertLess(kinds.index("drive_action"), kinds.index("llm_delta"))
        self.assertLess(kinds.index("llm_delta"), kinds.index("tts_audio"))
        self.assertEqual(kinds[-1], "state_delta")

    def test_text_turn_drive_action_and_state_delta(self):
        stream = self.stub.TextTurn(self.copilot_pb2.TextRequest(text="前面路口左转"), timeout=10)
        events = list(stream)
        action = next(e.drive_action for e in events if e.WhichOneof("payload") == "drive_action")
        self.assertEqual(action.intent, "TURN_LEFT")
        self.assertAlmostEqual(action.steering, -0.6, places=2)
        delta = next(e.state_delta for e in events if e.WhichOneof("payload") == "state_delta")
        self.assertGreater(delta.trust, 0)
        self.assertEqual(delta.emotion, self.copilot_pb2.CALM)

    def test_sync_game_state_applies_to_pipeline(self):
        req = self.copilot_pb2.GameState(
            trust=42,
            emotion=self.copilot_pb2.NERVOUS,
            scene=self.copilot_pb2.SceneContext(scene_id="highway", weather="rain", time_of_day="night"),
            telemetry=self.copilot_pb2.VehicleTelemetry(
                speed_kmh=90, gear=5, at_intersection=True, obstacle_summary="前方拥堵"
            ),
            level_id="L9",
        )
        ack = self.stub.SyncGameState(req, timeout=5)
        self.assertTrue(ack.ok)
        self.assertAlmostEqual(self.pipeline.state.trust, 42)
        self.assertEqual(self.pipeline.state.emotion_name(), "NERVOUS")
        self.assertEqual(self.pipeline.state.scene.scene_id, "highway")
        self.assertEqual(self.pipeline.state.telemetry.speed_kmh, 90)
        self.assertEqual(self.pipeline.state.level_id, "L9")

    def test_voice_turn_streams_audio_turn(self):
        if self.pipeline._asr is None:
            self.skipTest("无 ASR 后端")
        from core.audio import wav_chunks

        sample = os.path.join(os.path.dirname(__file__), "samples", "left_turn.wav")
        chunks = list(wav_chunks(sample, target_rate=16000))

        def requests():
            for i, chunk in enumerate(chunks):
                yield self.copilot_pb2.AudioChunk(pcm16=chunk, seq=i, end_of_stream=(i == len(chunks) - 1))

        events = list(self.stub.VoiceTurn(requests(), timeout=30))
        kinds = [e.WhichOneof("payload") for e in events]
        self.assertIn("asr_final", kinds)
        self.assertIn("drive_action", kinds)
        final = next(e.asr_final for e in events if e.WhichOneof("payload") == "asr_final")
        self.assertIn("左", final.text)

    def test_event_proto_covers_every_event_kind(self):
        from core.types import TurnEvent
        from server import _event_to_proto

        pb2 = self.copilot_pb2
        cases = [
            ("asr_partial", {"text": "转", "confidence": 0.5}, "asr_partial"),
            ("asr_final", {"text": "左转", "confidence": 0.9, "low_confidence": False}, "asr_final"),
            ("llm_delta", {"text": "好"}, "llm_delta"),
            ("drive_action", {"steering": -0.5, "throttle": 0.2, "brake": 0.0, "hold_ms": 900, "intent": "TURN_LEFT", "confidence": 0.8}, "drive_action"),
            ("tts_audio", {"pcm16": b"\x01\x02", "from_voicelib": True, "text": "行"}, "tts_audio"),
            ("state_delta", {"trust": 60, "trust_delta": 1, "emotion": "HAPPY"}, "state_delta"),
            ("error", {"code": "bad", "message": "boom"}, "error"),
        ]
        for kind, data, expected in cases:
            with self.subTest(kind=kind):
                proto = _event_to_proto(pb2, TurnEvent(kind=kind, data=data))
                self.assertEqual(proto.WhichOneof("payload"), expected)
        self.assertEqual(_event_to_proto(pb2, TurnEvent(kind="mystery", data={})).WhichOneof("payload"), None)

    def test_tts_audio_proto_carries_sample_rate(self):
        from core.types import TurnEvent
        from server import _event_to_proto

        pb2 = self.copilot_pb2
        proto = _event_to_proto(
            pb2, TurnEvent(kind="tts_audio", data={"pcm16": b"\x01\x02", "sample_rate": 16000})
        )
        self.assertEqual(proto.tts_audio.sample_rate, 16000)
        self.assertEqual(proto.tts_audio.pcm16, b"\x01\x02")
        default = _event_to_proto(pb2, TurnEvent(kind="tts_audio", data={"pcm16": b""}))
        self.assertEqual(default.tts_audio.sample_rate, 24000)

    def test_state_dict_conversion_helpers(self):
        from server import copilot_emotion_name, copilot_emotion_reverse

        self.assertEqual([copilot_emotion_name(i) for i in range(5)], ["CALM", "ANNOYED", "NERVOUS", "HAPPY", "CALM"])
        self.assertEqual(copilot_emotion_reverse("HAPPY"), 3)
        self.assertEqual(copilot_emotion_reverse("unknown"), 0)


if __name__ == "__main__":
    unittest.main()
