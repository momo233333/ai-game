from __future__ import annotations

from dataclasses import dataclass

from .voicelib import VoiceEntry, VoiceLib


@dataclass
class RouteResult:
    kind: str
    text: str
    from_voicelib: bool
    entry: VoiceEntry | None = None
    pcm16: bytes = b""
    reason: str = ""


class TTSRouter:
    def __init__(self, cfg: dict, voicelib: VoiceLib | None = None, realtime=None):
        tts_cfg = cfg.get("tts", {})
        self.voicelib_enabled = bool(tts_cfg.get("voicelib_enabled", True))
        self.realtime_enabled = bool(tts_cfg.get("realtime_enabled", True))
        self.voicelib = voicelib
        if realtime is None:
            from .gpt_sovits import GPTSoVITSEngine

            realtime = GPTSoVITSEngine(tts_cfg)
        self.realtime = realtime

    @property
    def name(self) -> str:
        parts = []
        if self.voicelib_enabled and self.voicelib and self.voicelib.loaded:
            parts.append("voicelib")
        if self.realtime_enabled and getattr(self.realtime, "available", False):
            parts.append(getattr(self.realtime, "name", "realtime"))
        return "+".join(parts) if parts else "silent"

    def route(self, text: str, tts_hint: str | None = None, emotion: str = "CALM") -> RouteResult:
        text = (text or "").strip()
        if not text:
            return RouteResult(kind="silent", text="", from_voicelib=False, reason="empty")

        if self.voicelib_enabled and self.voicelib and self.voicelib.loaded:
            entry = None
            if tts_hint:
                entry = self.voicelib.resolve_uri(tts_hint)
            if entry is None:
                entry = self.voicelib.lookup(text, emotion)
            if entry is not None:
                pcm = self.voicelib.pcm16(entry) or b""
                if pcm:
                    return RouteResult(
                        kind="voicelib",
                        text=entry.text,
                        from_voicelib=True,
                        entry=entry,
                        pcm16=pcm,
                    )
                # 有匹配词条但 wav 缺失/损坏 → 不谎报 voicelib，继续走实时 TTS

        if self.realtime_enabled and getattr(self.realtime, "available", False):
            result = self.realtime.synth(text, emotion=emotion)
            if result.ok:
                return RouteResult(kind="realtime", text=text, from_voicelib=False, pcm16=result.pcm16)
            return RouteResult(kind="realtime_failed", text=text, from_voicelib=False, reason=result.reason)

        return RouteResult(kind="silent", text=text, from_voicelib=False, reason="tts_disabled")
