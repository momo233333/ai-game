from __future__ import annotations

from dataclasses import dataclass

from .types import Emotion, GameState, StateDelta


@dataclass
class LocalEvent:
    kind: str
    trust_delta: float
    description: str


LOCAL_EVENTS = {
    "collision": LocalEvent("collision", -12.0, "撞车了"),
    "red_light": LocalEvent("red_light", -8.0, "闯红灯"),
    "lane_cut": LocalEvent("lane_cut", -4.0, "压实线"),
    "safe_arrival": LocalEvent("safe_arrival", 10.0, "安全到达"),
    "smooth_drive": LocalEvent("smooth_drive", 3.0, "开得稳"),
    "honk": LocalEvent("honk", -2.0, "乱按喇叭"),
}


def next_emotion(state: GameState, last_delta: float = 0.0) -> Emotion:
    trust = state.trust
    if trust < 20:
        return Emotion.NERVOUS
    if last_delta <= -5 or trust < 40:
        return Emotion.ANNOYED
    if trust >= 80 and last_delta >= 0:
        return Emotion.HAPPY
    if state.telemetry.at_intersection and "红灯" in state.telemetry.obstacle_summary:
        return Emotion.NERVOUS
    return Emotion.CALM


class StateManager:
    def __init__(self, state: GameState | None = None):
        self.state = state or GameState()

    def sync(self, data: dict) -> GameState:
        from .types import state_from_dict

        self.state = state_from_dict(data) if data else self.state
        return self.state

    def apply_delta(self, trust_delta: float, last_delta: float | None = None) -> StateDelta:
        before = self.state.trust
        self.state.trust = max(0.0, min(100.0, before + trust_delta))
        delta = self.state.trust - before
        self.state.emotion = next_emotion(self.state, delta if last_delta is None else last_delta)
        return StateDelta(trust=self.state.trust, emotion=self.state.emotion_name(), trust_delta=delta)

    def apply_local_event(self, event_key: str) -> StateDelta | None:
        event = LOCAL_EVENTS.get(event_key)
        if event is None:
            return None
        return self.apply_delta(event.trust_delta)

    @property
    def emotion(self) -> Emotion:
        return self.state.emotion
