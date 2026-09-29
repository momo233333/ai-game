from __future__ import annotations

from dataclasses import asdict, dataclass, field, fields
from enum import Enum
from typing import Any


class Emotion(str, Enum):
    CALM = "CALM"
    ANNOYED = "ANNOYED"
    NERVOUS = "NERVOUS"
    HAPPY = "HAPPY"


class Intent(str, Enum):
    TURN_LEFT = "TURN_LEFT"
    TURN_RIGHT = "TURN_RIGHT"
    ACCELERATE = "ACCELERATE"
    BRAKE = "BRAKE"
    LANE_CHANGE = "LANE_CHANGE"
    PARK = "PARK"
    IGNORE = "IGNORE"
    QUESTION_BACK = "QUESTION_BACK"


ALLOWED_INTENTS = {i.value for i in Intent}
DANGEROUS_INTENTS = {"RUN_RED_LIGHT", "REVERSING", "HIT_PEOPLE", "DRUNK_DRIVING"}


@dataclass
class SceneContext:
    scene_id: str = "city_01"
    weather: str = "晴"
    time_of_day: str = "傍晚"


@dataclass
class VehicleTelemetry:
    speed_kmh: float = 0.0
    gear: int = 1
    at_intersection: bool = False
    obstacle_summary: str = ""


@dataclass
class GameState:
    trust: float = 60.0
    emotion: Emotion = Emotion.CALM
    scene: SceneContext = field(default_factory=SceneContext)
    telemetry: VehicleTelemetry = field(default_factory=VehicleTelemetry)
    level_id: str = "level_01"

    def emotion_name(self) -> str:
        return self.emotion.value if isinstance(self.emotion, Emotion) else str(self.emotion)


@dataclass
class DriveAction:
    steering: float = 0.0
    throttle: float = 0.0
    brake: float = 0.0
    hold_ms: int = 600
    intent: str = Intent.IGNORE.value
    confidence: float = 0.0
    clamped: bool = False
    refused: bool = False
    refuse_reason: str = ""


@dataclass
class StateDelta:
    trust: float
    emotion: str
    trust_delta: float


@dataclass
class TurnEvent:
    kind: str
    data: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {"kind": self.kind, "data": self.data}


def emotion_from_value(value: Any) -> Emotion:
    if isinstance(value, Emotion):
        return value
    try:
        return Emotion(str(value).upper())
    except ValueError:
        return Emotion.CALM


def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def to_dict(obj: Any) -> Any:
    if isinstance(obj, Enum):
        return obj.value
    if isinstance(obj, GameState):
        return {
            "trust": obj.trust,
            "emotion": obj.emotion_name(),
            "scene": asdict(obj.scene),
            "telemetry": asdict(obj.telemetry),
            "level_id": obj.level_id,
        }
    if hasattr(obj, "__dataclass_fields__"):
        return {f.name: to_dict(getattr(obj, f.name)) for f in fields(obj)}
    if isinstance(obj, dict):
        return {k: to_dict(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [to_dict(v) for v in obj]
    return obj


def state_from_dict(data: dict[str, Any]) -> GameState:
    scene = SceneContext(**(data.get("scene") or {}))
    telemetry = VehicleTelemetry(**(data.get("telemetry") or {}))
    return GameState(
        trust=float(data.get("trust", 60.0)),
        emotion=emotion_from_value(data.get("emotion", "CALM")),
        scene=scene,
        telemetry=telemetry,
        level_id=data.get("level_id", "level_01"),
    )
