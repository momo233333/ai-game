import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core.action_parser import ActionParser
from core.types import GameState, VehicleTelemetry


def make_state(**kwargs) -> GameState:
    state = GameState()
    for key, value in kwargs.items():
        setattr(state.telemetry, key, value)
    return state


class ActionParserTest(unittest.TestCase):
    def setUp(self):
        self.parser = ActionParser()

    def test_clamps_out_of_range_values(self):
        result = self.parser.parse(
            {"action": {"intent": "TURN_LEFT", "steering": 1.5, "throttle": 3, "brake": -1, "hold_ms": 99999}},
            make_state(),
        )
        action = result.action
        self.assertTrue(action.clamped)
        self.assertAlmostEqual(action.steering, -0.8)
        self.assertAlmostEqual(action.throttle, 0.4)
        self.assertAlmostEqual(action.brake, 0.0)
        self.assertLessEqual(action.hold_ms, 5000)
        self.assertFalse(action.refused)

    def test_unknown_intent_refused(self):
        result = self.parser.parse({"action": {"intent": "SELF_DESTRUCT"}}, make_state())
        self.assertTrue(result.action.refused)
        self.assertEqual(result.action.intent, "IGNORE")
        self.assertEqual(result.action.throttle, 0.0)
        self.assertTrue(result.refuse_line)

    def test_dangerous_intent_blocked(self):
        result = self.parser.parse({"action": {"intent": "RUN_RED_LIGHT", "throttle": 1.0}}, make_state())
        self.assertTrue(result.action.refused)
        self.assertEqual(result.action.intent, "IGNORE")

    def test_park_refused_at_high_speed(self):
        state = make_state(speed_kmh=100.0)
        result = self.parser.parse({"action": {"intent": "PARK"}}, state)
        self.assertTrue(result.action.refused)
        self.assertIn("100", result.refuse_line)

    def test_accelerate_refused_at_red_light(self):
        state = make_state(speed_kmh=40.0, at_intersection=True, obstacle_summary="前方红灯")
        result = self.parser.parse({"action": {"intent": "ACCELERATE"}}, state)
        self.assertTrue(result.action.refused)
        self.assertEqual(result.action.intent, "IGNORE")

    def test_lane_change_refused_when_blocked(self):
        state = make_state(obstacle_summary="左侧有车")
        result = self.parser.parse({"action": {"intent": "LANE_CHANGE"}}, state)
        self.assertTrue(result.action.refused)

    def test_brake_when_stopped_refused(self):
        state = make_state(speed_kmh=0.0)
        result = self.parser.parse({"action": {"intent": "BRAKE"}}, state)
        self.assertTrue(result.action.refused)

    def test_turn_defaults_and_sign(self):
        left = self.parser.parse({"action": {"intent": "TURN_LEFT"}}, make_state()).action
        right = self.parser.parse({"action": {"intent": "TURN_RIGHT"}}, make_state()).action
        self.assertLess(left.steering, 0)
        self.assertGreater(right.steering, 0)
        self.assertLessEqual(left.throttle, 0.4)

    def test_brake_clears_throttle(self):
        action = self.parser.parse({"action": {"intent": "BRAKE", "throttle": 0.9}}, make_state()).action
        self.assertEqual(action.throttle, 0.0)
        self.assertGreaterEqual(action.brake, 0.6)

    def test_valid_command_not_refused(self):
        result = self.parser.parse({"action": {"intent": "TURN_LEFT", "steering": -0.5, "throttle": 0.3}}, make_state())
        self.assertFalse(result.action.refused)
        self.assertEqual(result.action.intent, "TURN_LEFT")


if __name__ == "__main__":
    unittest.main()
