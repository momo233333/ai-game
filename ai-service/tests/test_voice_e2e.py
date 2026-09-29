import os
import unittest

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VOSK_MODEL = os.path.join(SERVICE_DIR, "models", "vosk-model-small-cn-0.22")
SAMPLES = os.path.join(SERVICE_DIR, "tests", "samples")

HAS_MODEL = os.path.isdir(VOSK_MODEL)
HAS_SAMPLES = all(
    os.path.exists(os.path.join(SAMPLES, n))
    for n in ("left_turn.wav", "obstacle_stop.wav", "swear.wav", "greet.wav")
)


@unittest.skipUnless(HAS_MODEL and HAS_SAMPLES, "vosk model or samples missing")
class VoiceE2ETest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import tempfile

        from config import load_config
        from core.asr import create_asr
        from core.vad import create_vad
        from pipeline import DialoguePipeline

        cfg = load_config()
        cfg["memory"]["db_path"] = os.path.join(tempfile.mkdtemp(), "m.db")
        cls.pipeline = DialoguePipeline(
            cfg, SERVICE_DIR, asr=create_asr(cfg, service_dir=SERVICE_DIR), vad=create_vad(cfg)
        )

    def _run(self, name: str):
        from core.audio import wav_chunks

        path = os.path.join(SAMPLES, name)
        events = list(self.pipeline.handle_audio(wav_chunks(path, target_rate=16000)))
        return events

    @staticmethod
    def _first(events, kind):
        return next((e.data for e in events if e.kind == kind), None)

    def test_left_turn_recognized_and_executed(self):
        events = self._run("left_turn.wav")
        final = self._first(events, "asr_final")
        action = self._first(events, "drive_action")
        self.assertIsNotNone(final)
        self.assertEqual(final["text"], "向左转弯")
        self.assertEqual(action["intent"], "TURN_LEFT")
        self.assertLess(action["steering"], 0)

    def test_obstacle_stop_parks(self):
        events = self._run("obstacle_stop.wav")
        final = self._first(events, "asr_final")
        action = self._first(events, "drive_action")
        self.assertIn("障碍物", final["text"])
        self.assertEqual(action["intent"], "PARK")
        self.assertGreater(action["brake"], 0.5)

    def test_swear_blocked_input_layer(self):
        events = self._run("swear.wav")
        action = self._first(events, "drive_action")
        delta = self._first(events, "state_delta")
        self.assertEqual(action["intent"], "IGNORE")
        self.assertGreaterEqual(action["brake"], 0.6)
        self.assertEqual(delta["trust_delta"], -8.0)

    def test_greet_smalltalk(self):
        events = self._run("greet.wav")
        final = self._first(events, "asr_final")
        self.assertIn("你好", final["text"].replace(" ", ""))
        self.assertIsNotNone(self._first(events, "drive_action"))

    def test_no_duplicate_asr_final(self):
        events = self._run("left_turn.wav")
        count = sum(1 for e in events if e.kind == "asr_final")
        self.assertEqual(count, 1)

    def test_event_order_contract(self):
        events = self._run("left_turn.wav")
        kinds = [e.kind for e in events]
        self.assertEqual(kinds[0], "asr_partial")
        self.assertIn("asr_final", kinds)
        self.assertLess(kinds.index("asr_final"), kinds.index("drive_action"))
        self.assertLess(kinds.index("drive_action"), kinds.index("state_delta"))


if __name__ == "__main__":
    unittest.main()
