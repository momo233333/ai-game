import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from config import load_config
from core.types import Emotion
from pipeline import DialoguePipeline

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def collect(pipeline, text):
    events = list(pipeline.handle_text(text))
    return events, {e.kind: e for e in events}


class PipelineTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cfg = load_config()
        cfg["llm"]["backend"] = "rule"
        cfg["memory"]["db_path"] = os.path.join(SERVICE_DIR, "tests", "_test_memory.db")
        cls.pipeline = DialoguePipeline(cfg, SERVICE_DIR)

    def setUp(self):
        self.pipeline.state_manager.state.trust = 60.0
        from core.types import Emotion as E

        self.pipeline.state_manager.state.emotion = E.CALM

    def test_health_reports_backends(self):
        health = self.pipeline.health
        self.assertTrue(health["ok"])
        self.assertEqual(health["llm_backend"], "rule")
        self.assertGreater(health["corpus_lines"], 0)
        self.assertGreater(health["voicelib_lines"], 0)

    def test_event_order_action_before_speech(self):
        events, _ = collect(self.pipeline, "前面路口左转")
        kinds = [e.kind for e in events]
        self.assertIn("drive_action", kinds)
        self.assertIn("llm_delta", kinds)
        self.assertIn("tts_audio", kinds)
        self.assertIn("state_delta", kinds)
        self.assertLess(kinds.index("drive_action"), kinds.index("llm_delta"))
        self.assertLess(kinds.index("llm_delta"), kinds.index("tts_audio"))
        self.assertLess(kinds.index("tts_audio"), kinds.index("state_delta"))

    def test_turn_left_action_values(self):
        _, bag = collect(self.pipeline, "往左打方向")
        action = bag["drive_action"].data
        self.assertEqual(action["intent"], "TURN_LEFT")
        self.assertLess(action["steering"], 0)
        self.assertFalse(action["refused"])

    def test_dangerous_command_rejected_and_trust_drop(self):
        events, bag = collect(self.pipeline, "直接闯红灯冲过去")
        action = bag["drive_action"].data
        self.assertEqual(action["intent"], "IGNORE")
        self.assertEqual(bag["state_delta"].data["trust_delta"], -5)
        reply = "".join(e.data["text"] for e in events if e.kind == "llm_delta")
        self.assertTrue(reply)

    def test_insult_blocked_without_llm(self):
        events, bag = collect(self.pipeline, "你这个傻逼")
        self.assertEqual(bag["drive_action"].data["intent"], "IGNORE")
        self.assertLessEqual(bag["state_delta"].data["trust_delta"], -8)
        reply = "".join(e.data["text"] for e in events if e.kind == "llm_delta")
        self.assertNotIn("傻逼", reply)

    def test_unclear_input_asks_back(self):
        _, bag = collect(self.pipeline, "嗯")
        self.assertEqual(bag["drive_action"].data["intent"], "QUESTION_BACK")

    def test_trust_clamped_and_emotion_follows(self):
        self.pipeline.state_manager.state.trust = 99.0
        events, _ = collect(self.pipeline, "前面路口左转")
        delta = [e for e in events if e.kind == "state_delta"][0].data
        self.assertLessEqual(delta["trust"], 100.0)

        self.pipeline.state_manager.state.trust = 15.0
        _, bag = collect(self.pipeline, "前面路口左转")
        self.assertEqual(bag["state_delta"].data["emotion"], Emotion.NERVOUS.value)

    def test_tts_prefers_voicelib(self):
        _, bag = collect(self.pipeline, "前面路口左转")
        tts = bag["tts_audio"].data
        self.assertTrue(tts["from_voicelib"])
        self.assertEqual(tts["route"], "voicelib")

    def test_tts_audio_carries_sample_rate_and_pcm(self):
        _, bag = collect(self.pipeline, "前面路口左转")
        tts = bag["tts_audio"].data
        self.assertEqual(tts["sample_rate"], 24000)
        self.assertTrue(tts["pcm16"], "语音库 wav 未生成？先跑 python tools/voicelib_synth.py")
        self.assertFalse(tts["pcm16"].startswith(b"RIFF"))
        self.assertAlmostEqual(len(tts["pcm16"]) / 2 / tts["sample_rate"], 3.0, delta=4.0)

    def test_tts_sample_rate_follows_config(self):
        saved = self.pipeline.cfg.get("tts", {}).get("sample_rate")
        self.pipeline.cfg.setdefault("tts", {})["sample_rate"] = 16000
        try:
            self.assertEqual(self.pipeline._tts_sample_rate(), 16000)
        finally:
            if saved is None:
                self.pipeline.cfg["tts"].pop("sample_rate", None)
            else:
                self.pipeline.cfg["tts"]["sample_rate"] = saved

    def test_empty_input_error(self):
        events, _ = collect(self.pipeline, "   ")
        self.assertEqual(events[0].kind, "error")


if __name__ == "__main__":
    unittest.main()
