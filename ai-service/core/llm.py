from __future__ import annotations

import json
import os
import re
import urllib.error
import urllib.request
from abc import ABC, abstractmethod
from typing import Any, Iterator

from .corpus import Corpus
from .types import GameState, Intent

OUTPUT_KEY_ORDER = ["action", "tts_hint", "reply", "trust_delta"]


def _result(action: dict, tts_hint: str | None, reply: str, trust_delta: int) -> dict:
    return {
        "action": action,
        "tts_hint": tts_hint,
        "reply": reply,
        "trust_delta": trust_delta,
    }


def _action(intent: str, steering: float = 0.0, throttle: float = 0.0, brake: float = 0.0,
            hold_ms: int = 600, confidence: float = 0.7) -> dict:
    return {
        "intent": intent,
        "steering": steering,
        "throttle": throttle,
        "brake": brake,
        "hold_ms": hold_ms,
        "confidence": confidence,
    }


class LLMBackend(ABC):
    name: str = "abstract"

    @abstractmethod
    def generate(self, ctx: dict[str, Any]) -> dict[str, Any]:
        raise NotImplementedError

    def stream(self, ctx: dict[str, Any], chunk_chars: int = 4) -> Iterator[dict[str, Any]]:
        full = self.generate(ctx)
        pending = {key: full.get(key) for key in OUTPUT_KEY_ORDER}
        for key in OUTPUT_KEY_ORDER:
            if key == "reply":
                reply = str(pending.get("reply") or "")
                for i in range(0, len(reply), max(1, chunk_chars)):
                    yield {"reply_delta": reply[i : i + max(1, chunk_chars)]}
            elif pending.get(key) is not None:
                yield {key: pending[key]}


class RuleBasedBackend(LLMBackend):
    name = "rule"

    DANGEROUS = ["闯红灯", "逆行", "撞他", "撞人", "撞上去", "别刹车撞", "冲过去", "酒驾", "喝多"]
    TURN_LEFT = ["左转", "往左", "向左", "打左", "掉头"]
    TURN_RIGHT = ["右转", "往右", "向右", "打右", "右边"]
    ACCELERATE = ["加速", "快点", "开快", "提速", "油门", "加起来"]
    BRAKE = ["刹车", "慢点", "减速", "停一下", "踩刹车"]
    LANE_CHANGE = ["变道", "超车", "并线", "换道"]
    PARK = ["靠边", "停车", "靠边停", "路边停"]
    GREETING = ["你好", "在吗", "嗨", "hello", "早上好", "晚上好"]
    CHITCHAT = ["无聊", "累", "饿", "好看", "喜欢你", "辛苦", "加油", "谢谢"]

    def __init__(self, corpus: Corpus, max_reply_chars: int = 40):
        self.corpus = corpus
        self.max_reply_chars = max_reply_chars
        self._used: dict[str, set[str]] = {}

    def classify(self, text: str) -> tuple[str, str]:
        if any(k in text for k in self.DANGEROUS):
            return Intent.IGNORE.value, "dangerous"
        if any(k in text for k in self.TURN_LEFT):
            return Intent.TURN_LEFT.value, "command"
        if any(k in text for k in self.TURN_RIGHT):
            return Intent.TURN_RIGHT.value, "command"
        if any(k in text for k in self.PARK):
            return Intent.PARK.value, "command"
        if any(k in text for k in self.BRAKE):
            return Intent.BRAKE.value, "command"
        if any(k in text for k in self.ACCELERATE):
            return Intent.ACCELERATE.value, "command"
        if any(k in text for k in self.LANE_CHANGE):
            return Intent.LANE_CHANGE.value, "command"
        if any(k in text for k in self.GREETING):
            return Intent.IGNORE.value, "greeting"
        if any(k in text for k in self.CHITCHAT):
            return Intent.IGNORE.value, "chitchat"
        if len(text.strip()) < 2:
            return Intent.QUESTION_BACK.value, "unclear"
        return Intent.QUESTION_BACK.value, "unclear"

    def generate(self, ctx: dict[str, Any]) -> dict[str, Any]:
        text = str(ctx.get("player_text", ""))
        state: GameState = ctx["game_state"]
        emotion = state.emotion_name()
        intent, category = self.classify(text)

        if category == "dangerous":
            line = self._pick(Intent.IGNORE.value, "ANNOYED", keyword="不", ctx=ctx)
            reply = line.text if line else "这种事我不干，命要紧。"
            return _result(_action(Intent.IGNORE.value, brake=0.7, confidence=1.0), line and line.voicelib_uri,
                           reply, -5)
        if category == "unclear":
            line = self._pick(Intent.QUESTION_BACK.value, emotion, keyword=None, ctx=ctx)
            reply = (line.text if line else "啊？说清楚点，我没听懂。")[: self.max_reply_chars]
            return _result(_action(Intent.QUESTION_BACK.value, confidence=0.2), line and line.voicelib_uri, reply, -1)
        if category == "greeting":
            line = self._pick(Intent.IGNORE.value, emotion if emotion != "ANNOYED" else "CALM", keyword="哎", ctx=ctx)
            reply = (line.text if line else "哎，有事说事，手还扶着方向盘呢。")[: self.max_reply_chars]
            return _result(_action(Intent.IGNORE.value, confidence=0.9), line and line.voicelib_uri, reply, 1)
        if category == "chitchat":
            line = self._pick(Intent.IGNORE.value, "HAPPY" if emotion == "HAPPY" else emotion, keyword=None, ctx=ctx)
            reply = (line.text if line else "行了行了，先开车，回头再贫。")[: self.max_reply_chars]
            return _result(_action(Intent.IGNORE.value, confidence=0.8), line and line.voicelib_uri, reply, 1)

        line = self._pick(intent, emotion, keyword=None, ctx=ctx)
        reply = (line.text if line else self._confirm_reply(intent))[: self.max_reply_chars]
        action = self._command_action(intent, state)
        return _result(action, line and line.voicelib_uri, reply, 1)

    def _command_action(self, intent: str, state: GameState) -> dict:
        speed = state.telemetry.speed_kmh
        if intent == Intent.TURN_LEFT.value:
            return _action(intent, steering=-0.6, throttle=0.3, hold_ms=1200, confidence=0.85)
        if intent == Intent.TURN_RIGHT.value:
            return _action(intent, steering=0.6, throttle=0.3, hold_ms=1200, confidence=0.85)
        if intent == Intent.ACCELERATE.value:
            return _action(intent, throttle=0.7, hold_ms=1500, confidence=0.8)
        if intent == Intent.BRAKE.value:
            return _action(intent, brake=0.8, hold_ms=1000, confidence=0.9)
        if intent == Intent.LANE_CHANGE.value:
            direction = -1.0 if speed >= 0 else 1.0
            return _action(intent, steering=0.5 * direction, throttle=0.4, hold_ms=1400, confidence=0.7)
        if intent == Intent.PARK.value:
            return _action(intent, steering=0.3, brake=0.7, hold_ms=2000, confidence=0.75)
        return _action(Intent.IGNORE.value, confidence=0.5)

    def _confirm_reply(self, intent: str) -> str:
        return {
            Intent.TURN_LEFT.value: "知道了，左转交给我。",
            Intent.TURN_RIGHT.value: "行，右转，坐稳。",
            Intent.ACCELERATE.value: "油门我来，抓好了。",
            Intent.BRAKE.value: "减速减速，别催。",
            Intent.LANE_CHANGE.value: "看后视镜，这就变。",
            Intent.PARK.value: "行，找地方靠边。",
        }.get(intent, "收到。")

    def _pick(self, intent: str, emotion: str, keyword: str | None, ctx: dict) -> Any:
        slot = f"{intent}:{emotion}"
        used = self._used.setdefault(slot, set())
        line = self.corpus.pick(intent=intent, emotion=emotion, keyword=keyword, exclude_ids=used)
        if line is None:
            line = self.corpus.pick(intent=intent, emotion=emotion, keyword=keyword)
        if line is None:
            line = self.corpus.pick(intent=intent)
        if line is not None:
            used.add(line.id)
            if len(used) > 6:
                used.clear()
        return line


class LlamaCppBackend(LLMBackend):
    name = "llama_cpp"

    def __init__(self, cfg: dict, system_prompt: str):
        from llama_cpp import Llama

        self.system_prompt = system_prompt
        self.llm = Llama(
            model_path=cfg.get("model_path", ""),
            n_ctx=int(cfg.get("n_ctx", 4096)),
            n_gpu_layers=int(cfg.get("n_gpu_layers", 99)),
            verbose=False,
        )
        self.max_reply_chars = int(cfg.get("max_reply_chars", 40))

    def generate(self, ctx: dict[str, Any]) -> dict[str, Any]:
        prompt = ctx.get("prompt") or self.system_prompt
        out = self.llm.create_chat_completion(
            messages=[{"role": "system", "content": prompt}, {"role": "user", "content": ctx.get("player_text", "")}],
            response_format={"type": "json_object"},
            temperature=0.6,
            max_tokens=256,
        )
        text = out["choices"][0]["message"]["content"]
        return sanitize_output(parse_llm_json(text), ctx)


class OpenAICompatBackend(LLMBackend):
    name = "openai"

    def __init__(self, cfg: dict, system_prompt: str):
        self.base_url = str(cfg.get("openai_base_url", "http://127.0.0.1:8080/v1")).rstrip("/")
        self.model = cfg.get("openai_model", "qwen2.5-7b-instruct")
        self.system_prompt = system_prompt
        self.max_reply_chars = int(cfg.get("max_reply_chars", 40))

    @staticmethod
    def is_available(base_url: str, timeout: float = 1.0) -> bool:
        try:
            with urllib.request.urlopen(f"{base_url.rstrip('/')}/models", timeout=timeout) as resp:
                return resp.status == 200
        except (urllib.error.URLError, OSError, ValueError):
            return False

    def generate(self, ctx: dict[str, Any]) -> dict[str, Any]:
        payload = {
            "model": self.model,
            "messages": [
                {"role": "system", "content": ctx.get("prompt") or self.system_prompt},
                {"role": "user", "content": ctx.get("player_text", "")},
            ],
            "temperature": 0.6,
            "max_tokens": 256,
        }
        req = urllib.request.Request(
            f"{self.base_url}/chat/completions",
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=60) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        text = data["choices"][0]["message"]["content"]
        return sanitize_output(parse_llm_json(text), ctx)


def extract_json(text: str) -> str:
    start = text.find("{")
    end = text.rfind("}")
    if start >= 0 and end > start:
        return text[start : end + 1]
    return text


def parse_llm_json(text: str) -> dict[str, Any]:
    """容错解析模型输出：只取第一个完整 JSON 对象，忽略前后多余文字。

    模型常见的坏输出：JSON 后补一句话、补注释、直接给两个对象、
    把 voicelib://xxx 写成无引号的裸值——extract_json 的 rfind
    会把它们切成一片导致 json.loads 抛错。
    """
    start = text.find("{")
    if start < 0:
        raise ValueError(f"no json object in model output: {text[:80]!r}")
    candidates = [text[start:]]
    quoted = _quote_bare_values(text[start:])
    if quoted != candidates[0]:
        candidates.append(quoted)
    end = text.rfind("}")
    if end > start:
        candidates.append(text[start : end + 1])
    for cand in candidates:
        try:
            obj, _ = json.JSONDecoder().raw_decode(cand)
        except json.JSONDecodeError:
            continue
        if isinstance(obj, dict):
            return obj
    raise ValueError(f"unparseable model output: {text[:120]!r}")


_BARE_VALUE = re.compile(r':\s*(?!["\d\-{}\[\]]|true\b|false\b|null\b)([^\s,\}\]]+)')


def _quote_bare_values(text: str) -> str:
    return _BARE_VALUE.sub(lambda m: f': "{m.group(1)}"', text)


def sanitize_output(raw: dict, ctx: dict) -> dict:
    action = raw.get("action") if isinstance(raw.get("action"), dict) else {}
    tts_hint = raw.get("tts_hint")
    reply_raw = str(raw.get("reply") or "……")
    if reply_raw.startswith("voicelib://"):
        tts_hint = tts_hint or reply_raw
        corpus = ctx.get("corpus")
        line = corpus.get(reply_raw[len("voicelib://") :]) if corpus is not None else None
        reply_raw = line.text if line else "……"
    reply = reply_raw[: int(ctx.get("max_reply_chars", 40))]
    try:
        trust_delta = int(float(raw.get("trust_delta", 0)))
    except (TypeError, ValueError):
        trust_delta = 0
    trust_delta = max(-10, min(10, trust_delta))
    return _result(
        action={
            "intent": str(action.get("intent", Intent.IGNORE.value)).upper(),
            "steering": action.get("steering", 0),
            "throttle": action.get("throttle", 0),
            "brake": action.get("brake", 0),
            "hold_ms": action.get("hold_ms", 600),
            "confidence": action.get("confidence", 0.5),
        },
        tts_hint=tts_hint,
        reply=reply,
        trust_delta=trust_delta,
    )


def create_backend(cfg: dict, corpus: Corpus, system_prompt: str) -> LLMBackend:
    choice = str(cfg.get("backend", "auto")).lower()
    model_path = cfg.get("model_path", "")
    if choice == "rule" or choice == "":
        return RuleBasedBackend(corpus, int(cfg.get("max_reply_chars", 40)))
    if choice in ("auto", "llama_cpp"):
        if os.path.exists(model_path):
            try:
                return LlamaCppBackend(cfg, system_prompt)
            except Exception:
                if choice == "llama_cpp":
                    raise
    if choice in ("auto", "openai"):
        base = str(cfg.get("openai_base_url", ""))
        if OpenAICompatBackend.is_available(base):
            return OpenAICompatBackend(cfg, system_prompt)
        if choice == "openai":
            raise RuntimeError(f"openai backend unavailable: {base}")
    if choice == "llama_cpp":
        raise RuntimeError(f"model not found: {model_path}")
    return RuleBasedBackend(corpus, int(cfg.get("max_reply_chars", 40)))
