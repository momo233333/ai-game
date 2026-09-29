import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core.state import StateManager, next_emotion
from core.types import Emotion, GameState, state_from_dict, to_dict


class StateManagerTest(unittest.TestCase):
    def test_delta_clamped_to_bounds(self):
        mgr = StateManager()
        mgr.state.trust = 98.0
        delta = mgr.apply_delta(10.0)
        self.assertEqual(mgr.state.trust, 100.0)
        self.assertEqual(delta.trust_delta, 2.0)
        mgr.state.trust = 3.0
        delta = mgr.apply_delta(-50.0)
        self.assertEqual(mgr.state.trust, 0.0)
        self.assertEqual(delta.trust_delta, -3.0)

    def test_emotion_transitions(self):
        state = GameState()
        state.trust = 15.0
        self.assertEqual(next_emotion(state), Emotion.NERVOUS)
        state.trust = 30.0
        self.assertEqual(next_emotion(state, 0.0), Emotion.ANNOYED)
        state.trust = 85.0
        self.assertEqual(next_emotion(state, 2.0), Emotion.HAPPY)
        state.trust = 60.0
        self.assertEqual(next_emotion(state, 1.0), Emotion.CALM)

    def test_big_negative_delta_forces_annoyed(self):
        state = GameState()
        state.trust = 85.0
        self.assertEqual(next_emotion(state, -6.0), Emotion.ANNOYED)

    def test_local_events(self):
        mgr = StateManager()
        mgr.state.trust = 50.0
        delta = mgr.apply_local_event("collision")
        self.assertEqual(delta.trust_delta, -12.0)
        self.assertIsNone(mgr.apply_local_event("nope"))

    def test_sync_from_dict_roundtrip(self):
        mgr = StateManager()
        data = {
            "trust": 77,
            "emotion": "HAPPY",
            "scene": {"scene_id": "highway", "weather": "雨", "time_of_day": "夜"},
            "telemetry": {"speed_kmh": 88, "gear": 4, "at_intersection": False, "obstacle_summary": ""},
            "level_id": "level_09",
        }
        state = mgr.sync(data)
        self.assertEqual(state.trust, 77.0)
        self.assertEqual(state.emotion, Emotion.HAPPY)
        self.assertEqual(state.scene.scene_id, "highway")
        self.assertEqual(state.telemetry.speed_kmh, 88.0)
        out = to_dict(state)
        again = state_from_dict(out)
        self.assertEqual(again.level_id, "level_09")
        self.assertEqual(again.emotion, Emotion.HAPPY)


if __name__ == "__main__":
    unittest.main()
