from __future__ import annotations

from dataclasses import dataclass


@dataclass
class SynthesisResult:
    ok: bool
    pcm16: bytes = b""
    reason: str = ""


class GPTSoVITSEngine:
    name = "gpt_sovits"

    def __init__(self, cfg: dict):
        self.cfg = cfg
        self.available = False
        self.reason = "not_installed"
        try:
            import importlib.util

            self.available = importlib.util.find_spec("GPT_SoVITS") is not None
            if self.available:
                self.reason = ""
        except Exception as exc:
            self.reason = str(exc)

    def synth(self, text: str, ref_text: str = "", emotion: str = "CALM") -> SynthesisResult:
        if not self.available:
            return SynthesisResult(ok=False, reason=f"gpt_sovits_unavailable:{self.reason}")
        return SynthesisResult(ok=False, reason="gpt_sovits_pipeline_not_wired")
