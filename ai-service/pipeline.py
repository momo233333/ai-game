from __future__ import annotations

import time
from typing import Iterable, Iterator

from config import resolve_path
from core.action_parser import ActionParser, ParseResult
from core.asr import ASRBackend
from core.corpus import Corpus
from core.dialogue import DialogueCore, MemoryStore
from core.llm import LLMBackend, create_backend
from core.safety import SafetyFilter, SafetyVerdict
from core.state import StateManager
from core.tts import TTSRouter, VoiceLib
from core.types import DriveAction, GameState, TurnEvent
from core.vad import VADBase


class DialoguePipeline:
    def __init__(
        self,
        cfg: dict,
        service_dir: str,
        state_manager: StateManager | None = None,
        asr: ASRBackend | None = None,
        vad: VADBase | None = None,
    ):
        self.cfg = cfg
        self.service_dir = service_dir
        corpus_path = resolve_path(service_dir, cfg.get("dialogue", {}).get("corpus_path", "../voice-corpus/lines.csv"))
        voicelib_path = resolve_path(service_dir, cfg.get("tts", {}).get("voicelib_path", "../voice-corpus/manifest.json"))
        wordlist_path = resolve_path(service_dir, cfg.get("safety", {}).get("wordlist_path", "data/sensitive_words.txt"))
        db_path = resolve_path(service_dir, cfg.get("memory", {}).get("db_path", "memory.db"))

        self.corpus = Corpus(corpus_path)
        self.voicelib = VoiceLib(voicelib_path, int(cfg.get("tts", {}).get("sample_rate", 24000)))
        self.safety = SafetyFilter(wordlist_path)
        self.state_manager = state_manager or StateManager()
        store = MemoryStore(db_path, cfg.get("memory", {}).get("save_slot", "slot0"))

        self.parser = ActionParser()
        self._asr = asr
        self._vad = vad

        self.dialogue = DialogueCore(cfg, self.corpus, backend=None, store=store)  # type: ignore[arg-type]
        self.backend: LLMBackend = create_backend(cfg.get("llm", {}), self.corpus, "")
        self.dialogue.backend = self.backend

        self.tts = TTSRouter(cfg, self.voicelib)
        self.turn_timings: list[dict] = []
        self._store = store

    def close(self) -> None:
        self._store.close()

    @property
    def health(self) -> dict:
        return {
            "ok": True,
            "llm_backend": self.backend.name,
            "asr_backend": self._asr.name if self._asr else "text",
            "vad_backend": self._vad.name if self._vad else "none",
            "tts_backend": self.tts.name,
            "voicelib_lines": len(self.voicelib.entries),
            "corpus_lines": len(self.corpus.lines),
            "version": self.cfg.get("server", {}).get("version", "0.0.0"),
        }

    def sync_state(self, data: dict) -> GameState:
        return self.state_manager.sync(data)

    @property
    def state(self) -> GameState:
        return self.state_manager.state

    def handle_text(
        self, text: str, state: GameState | None = None, emit_asr: bool = True
    ) -> Iterator[TurnEvent]:
        state = state or self.state
        started = time.perf_counter()
        timings: dict[str, float] = {}
        text = (text or "").strip()
        if not text:
            yield TurnEvent("error", {"code": "empty_input", "message": "空指令"})
            return

        verdict = self.safety.check(text)
        if verdict.blocked:
            if emit_asr:
                yield TurnEvent("asr_final", {"text": text, "confidence": 1.0, "low_confidence": False})
            line = self.safety.redacted(verdict, state.emotion_name())
            action = DriveAction(intent="IGNORE", brake=0.6, hold_ms=800, confidence=1.0)
            yield TurnEvent("drive_action", _action_payload(action))
            for i in range(0, len(line), 4):
                yield TurnEvent("llm_delta", {"text": line[i : i + 4]})
            yield from self._speak(line, tts_hint=None, state=state)
            delta = self.state_manager.apply_delta(-8.0)
            yield TurnEvent("state_delta", _delta_payload(delta))
            self.dialogue.record_turn(text, line, -8.0)
            timings["total_ms"] = (time.perf_counter() - started) * 1000
            self.turn_timings = timings
            return

        if emit_asr:
            yield TurnEvent("asr_final", {"text": text, "confidence": 1.0, "low_confidence": False})

        tts_hint: str | None = None
        trust_delta = 0
        refused_line = ""
        first_action_ms = None

        acc = ""
        blocked_stream = False
        chunk_chars = int(self.cfg.get("llm", {}).get("stream_chunk_chars", 4))
        for chunk in self.dialogue.stream(text, state, chunk_chars=chunk_chars):
            if "action" in chunk:
                parse: ParseResult = self.parser.parse({"action": chunk["action"]}, state)
                refused_line = parse.refuse_line
                if first_action_ms is None:
                    first_action_ms = (time.perf_counter() - started) * 1000
                yield TurnEvent("drive_action", _action_payload(parse.action))
                if refused_line:
                    tts_hint = None
                    for i in range(0, len(refused_line), chunk_chars):
                        yield TurnEvent("llm_delta", {"text": refused_line[i : i + chunk_chars]})
            elif "tts_hint" in chunk:
                if not refused_line:
                    tts_hint = chunk["tts_hint"]
            elif "reply_delta" in chunk or "reply" in chunk:
                if refused_line:
                    continue
                piece = chunk.get("reply_delta") or str(chunk.get("reply") or "")
                candidate = acc + piece
                if self.safety.check_streaming(candidate).blocked:
                    blocked_stream = True
                    break
                acc = candidate
                yield TurnEvent("llm_delta", {"text": piece})
            elif "trust_delta" in chunk:
                trust_delta = int(chunk["trust_delta"])
                if refused_line:
                    trust_delta = min(trust_delta, 0)

        if blocked_stream:
            redacted = self.safety.redacted(
                SafetyVerdict(blocked=True, hits=[], cleaned_text=acc), state.emotion_name()
            )
            for i in range(0, len(redacted), chunk_chars):
                yield TurnEvent("llm_delta", {"text": redacted[i : i + chunk_chars]})
            acc = redacted
            trust_delta = -3

        reply = acc
        if refused_line:
            reply = refused_line

        out_verdict = self.safety.check(reply)
        if out_verdict.blocked:
            reply = self.safety.redacted(out_verdict, state.emotion_name())
            trust_delta = min(trust_delta, -3)

        yield from self._speak(reply, tts_hint=tts_hint, state=state)

        delta = self.state_manager.apply_delta(float(trust_delta))
        yield TurnEvent("state_delta", _delta_payload(delta))

        self.dialogue.record_turn(text, reply, float(trust_delta))
        timings["total_ms"] = (time.perf_counter() - started) * 1000
        if first_action_ms is not None:
            timings["action_ms"] = first_action_ms
        self.turn_timings = timings

    def handle_audio(self, chunks: Iterable[bytes], stop_on_silence: bool = True) -> Iterator[TurnEvent]:
        if self._asr is None or self._vad is None:
            yield TurnEvent(
                "error",
                {"code": "asr_unavailable", "message": "ASR 未安装，当前降级为文本指令 (text_turn)"},
            )
            return

        buffer = b""
        for chunk in chunks:
            if not chunk:
                break
            buffer += chunk
            self._vad.is_speech(chunk)
            for partial in self._asr.feed(chunk):
                yield TurnEvent("asr_partial", {"text": partial.text, "confidence": partial.confidence})
            if stop_on_silence and getattr(self._vad, "silence_exceeded", lambda: False)() and getattr(
                self._vad, "has_enough_speech", lambda: True
            )():
                break
        if not buffer:
            yield TurnEvent("error", {"code": "empty_audio", "message": "无音频数据"})
            return

        result = self._asr.finalize()
        self._vad.reset()
        yield TurnEvent(
            "asr_final",
            {"text": result.text, "confidence": result.confidence, "low_confidence": result.low_confidence},
        )
        if result.low_confidence:
            line = "啊？你说啥？风太大没听清，再说一遍。"
            yield TurnEvent("drive_action", _action_payload(DriveAction(intent="QUESTION_BACK", confidence=0.1)))
            yield from self._speak(line, tts_hint=None, state=self.state)
            return
        if not result.text.strip():
            yield TurnEvent("error", {"code": "asr_empty", "message": "未识别到语音"})
            return
        yield from self.handle_text(result.text, emit_asr=False)

    def _speak(self, text: str, tts_hint: str | None, state: GameState) -> Iterator[TurnEvent]:
        route = self.tts.route(text, tts_hint=tts_hint, emotion=state.emotion_name())
        yield TurnEvent(
            "tts_audio",
            {
                "pcm16": route.pcm16,
                "from_voicelib": route.from_voicelib,
                "text": route.text or text,
                "route": route.kind,
                "reason": route.reason,
                "sample_rate": self._tts_sample_rate(),
            },
        )

    def _tts_sample_rate(self) -> int:
        return int(self.cfg.get("tts", {}).get("sample_rate", 24000))


def _action_payload(action: DriveAction) -> dict:
    return {
        "steering": action.steering,
        "throttle": action.throttle,
        "brake": action.brake,
        "hold_ms": action.hold_ms,
        "intent": action.intent,
        "confidence": action.confidence,
        "refused": action.refused,
        "refuse_reason": action.refuse_reason,
    }


def _delta_payload(delta) -> dict:
    return {"trust": delta.trust, "emotion": delta.emotion, "trust_delta": delta.trust_delta}
