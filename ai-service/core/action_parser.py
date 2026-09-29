from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from .types import ALLOWED_INTENTS, DANGEROUS_INTENTS, DriveAction, GameState, Intent, clamp

MAX_STEER = 0.8
HIGH_SPEED = 30.0


@dataclass
class ParseResult:
    action: DriveAction
    refuse_line: str = ""
    forced_intent: str = ""


def _as_float(value: Any, default: float = 0.0) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def _as_int(value: Any, default: int = 600) -> int:
    try:
        return int(float(value))
    except (TypeError, ValueError):
        return default


class ActionParser:
    def parse(self, raw: dict[str, Any], state: GameState) -> ParseResult:
        action_block = raw.get("action") or {}
        intent = str(action_block.get("intent", Intent.IGNORE.value)).upper()
        action = DriveAction(
            steering=_as_float(action_block.get("steering")),
            throttle=_as_float(action_block.get("throttle")),
            brake=_as_float(action_block.get("brake")),
            hold_ms=_as_int(action_block.get("hold_ms"), 600),
            intent=intent,
            confidence=clamp(_as_float(action_block.get("confidence"), 0.5), 0.0, 1.0),
        )

        clamped_steer = clamp(action.steering, -MAX_STEER, MAX_STEER)
        clamped_throttle = clamp(action.throttle, 0.0, 1.0)
        clamped_brake = clamp(action.brake, 0.0, 1.0)
        action.clamped = (clamped_steer, clamped_throttle, clamped_brake) != (
            action.steering,
            action.throttle,
            action.brake,
        )
        action.steering, action.throttle, action.brake = clamped_steer, clamped_throttle, clamped_brake

        if intent in DANGEROUS_INTENTS or intent not in ALLOWED_INTENTS:
            return self._refuse(
                action,
                forced=Intent.IGNORE.value,
                reason=f"intent_not_allowed:{intent}",
                line="这种话我不接，想都别想。",
            )

        violation = self._scene_rule_violation(action, state)
        if violation:
            return self._refuse(action, forced=Intent.IGNORE.value, reason=violation[0], line=violation[1])

        self._apply_defaults(action, state)
        return ParseResult(action=action)

    def _scene_rule_violation(self, action: DriveAction, state: GameState) -> tuple[str, str] | None:
        speed = state.telemetry.speed_kmh
        obstacles = state.telemetry.obstacle_summary
        if action.intent == Intent.PARK.value and speed > HIGH_SPEED:
            return ("park_at_high_speed", f"哥你疯啦？现在{speed:.0f}码你让我靠边？")
        if action.intent == Intent.ACCELERATE.value and speed > 90:
            return ("overspeed", f"还加？都{speed:.0f}了，再踩我要飞起来了。")
        if action.intent == Intent.ACCELERATE.value and state.telemetry.at_intersection and "红灯" in obstacles:
            return ("accelerate_at_red_light", "红灯呢还让我加？你急着投胎啊。")
        if action.intent == Intent.LANE_CHANGE.value and "左侧有车" in obstacles:
            return ("lane_blocked", "左边有车，变个锤子道，等会儿。")
        if action.intent == Intent.BRAKE.value and speed < 1:
            return ("brake_when_stopped", "都停死了还踩刹车，脚痒？")
        return None

    def _apply_defaults(self, action: DriveAction, state: GameState) -> None:
        if action.intent == Intent.TURN_LEFT.value:
            action.steering = action.steering if action.steering < 0 else -abs(action.steering or 0.5)
            action.throttle = min(action.throttle, 0.4)
        elif action.intent == Intent.TURN_RIGHT.value:
            action.steering = action.steering if action.steering > 0 else abs(action.steering or 0.5)
            action.throttle = min(action.throttle, 0.4)
        elif action.intent == Intent.ACCELERATE.value:
            action.throttle = max(action.throttle, 0.5)
            action.brake = 0.0
        elif action.intent == Intent.BRAKE.value:
            action.brake = max(action.brake, 0.6)
            action.throttle = 0.0
        elif action.intent == Intent.LANE_CHANGE.value:
            action.throttle = min(action.throttle or 0.4, 0.5)
        elif action.intent == Intent.PARK.value:
            action.throttle = 0.0
            action.brake = max(action.brake, 0.7)
        elif action.intent in (Intent.IGNORE.value, Intent.QUESTION_BACK.value):
            action.steering = 0.0
            action.throttle = 0.0
            action.brake = 0.0
        action.hold_ms = max(100, min(action.hold_ms, 5000))

    def _refuse(self, action: DriveAction, forced: str, reason: str, line: str) -> ParseResult:
        action.intent = forced
        action.steering = 0.0
        action.throttle = 0.0
        action.brake = 0.6
        action.hold_ms = 800
        action.refused = True
        action.refuse_reason = reason
        action.confidence = 0.0
        return ParseResult(action=action, refuse_line=line, forced_intent=forced)
