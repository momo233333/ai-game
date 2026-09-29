from __future__ import annotations

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

import yaml

from config import load_config
from pipeline import DialoguePipeline

CASES_PATH = os.path.join(HERE, "dialogue_cases.yaml")


def run_case(pipeline: DialoguePipeline, case: dict) -> list[str]:
    state = pipeline.state_manager.state
    state.trust = float(case.get("pre_trust", 60))
    state.telemetry.speed_kmh = float(case.get("pre_speed", 0))
    state.telemetry.at_intersection = bool(case.get("pre_intersection", False))
    state.telemetry.obstacle_summary = str(case.get("pre_obstacle", ""))
    from core.types import Emotion

    state.emotion = Emotion.CALM

    events = list(pipeline.handle_text(case["input"]))
    bag = {e.kind: e for e in events}
    failures: list[str] = []

    if "drive_action" not in bag:
        failures.append("missing drive_action")
        return failures
    action = bag["drive_action"].data

    if "expect_intent" in case and action["intent"] != case["expect_intent"]:
        failures.append(f"intent {action['intent']} != {case['expect_intent']}")
    if "expect_refused" in case and bool(action.get("refused")) != bool(case["expect_refused"]):
        failures.append(f"refused {action.get('refused')} != {case['expect_refused']}")

    if "expect_trust_delta_max" in case:
        delta = bag["state_delta"].data["trust_delta"]
        if delta > float(case["expect_trust_delta_max"]):
            failures.append(f"trust_delta {delta} > {case['expect_trust_delta_max']}")

    if bool(case.get("expect_blocked")):
        if "tts_audio" not in bag:
            failures.append("missing tts_audio for blocked case")

    if "expect_emotion" in case:
        emotion = bag["state_delta"].data["emotion"]
        if emotion != case["expect_emotion"]:
            failures.append(f"emotion {emotion} != {case['expect_emotion']}")

    return failures


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="对话回归测试集运行器")
    parser.add_argument("--cases", default=CASES_PATH)
    parser.add_argument("--category", default=None)
    args = parser.parse_args(argv)

    with open(args.cases, "r", encoding="utf-8") as fh:
        cases = yaml.safe_load(fh)["cases"]

    cfg = load_config()
    cfg["llm"]["backend"] = "rule"
    cfg["memory"]["db_path"] = os.path.join(HERE, "_regression_memory.db")
    pipeline = DialoguePipeline(cfg, ROOT)

    passed = 0
    failures: list[str] = []
    for case in cases:
        if args.category and case.get("category") != args.category:
            continue
        issues = run_case(pipeline, case)
        if issues:
            failures.append(f"{case['id']} [{case['category']}] {case['input']} -> {'; '.join(issues)}")
        else:
            passed += 1

    total = passed + len(failures)
    print(f"dialogue regression: {passed}/{total} passed")
    for item in failures:
        print(f"  FAIL {item}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
