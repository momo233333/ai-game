from __future__ import annotations

import json
import os
from abc import ABC, abstractmethod
from typing import Iterator


class ASRResult:
    def __init__(self, text: str, confidence: float):
        self.text = text
        self.confidence = confidence

    @property
    def low_confidence(self) -> bool:
        return self.confidence < 0.6


class ASRBackend(ABC):
    name: str = "base"

    @abstractmethod
    def feed(self, pcm16: bytes) -> Iterator[ASRResult]:
        raise NotImplementedError

    @abstractmethod
    def finalize(self) -> ASRResult:
        raise NotImplementedError

    def reset(self) -> None:
        pass


class FunASRBackend(ASRBackend):
    name = "funasr"

    def __init__(self, model: str = "paraformer-zh-streaming"):
        from funasr import AutoModel

        self.model = AutoModel(model=model, vad_model="fsmn-vad", disable_update=True)
        self._buffer: list[bytes] = []

    def feed(self, pcm16: bytes) -> Iterator[ASRResult]:
        self._buffer.append(pcm16)
        res = self.model.generate(input=pcm16, is_final=False, chunk_size=[0, 10, 5])
        for item in res or []:
            text = str(item.get("text", "")).strip()
            if text:
                yield ASRResult(text, float(item.get("confidence", 0.7) or 0.7))

    def finalize(self) -> ASRResult:
        joined = b"".join(self._buffer)
        self._buffer.clear()
        res = self.model.generate(input=joined, is_final=True, chunk_size=[0, 10, 5])
        text = ""
        confidence = 0.7
        for item in res or []:
            text += str(item.get("text", ""))
            confidence = float(item.get("confidence", confidence) or confidence)
        return ASRResult(text.strip(), confidence)


class VoskASRBackend(ASRBackend):
    name = "vosk"

    def __init__(self, model_path: str, sample_rate: int = 16000):
        import vosk

        if not os.path.isdir(model_path):
            raise FileNotFoundError(f"vosk model not found: {model_path}")
        vosk.SetLogLevel(-1)
        self._vosk = vosk
        self._model = vosk.Model(model_path)
        self.sample_rate = sample_rate
        self._segments: list[str] = []
        self._new_recognizer()

    def _new_recognizer(self) -> None:
        self._rec = self._vosk.KaldiRecognizer(self._model, self.sample_rate)

    @staticmethod
    def _normalize(text: str) -> str:
        return "".join(text.split())

    def feed(self, pcm16: bytes) -> Iterator[ASRResult]:
        if self._rec.AcceptWaveform(pcm16):
            text = self._normalize(json.loads(self._rec.Result()).get("text", ""))
            if text:
                self._segments.append(text)
                yield ASRResult(text, 0.75)
        else:
            partial = self._normalize(json.loads(self._rec.PartialResult()).get("partial", ""))
            if partial:
                yield ASRResult(partial, 0.7)

    def finalize(self) -> ASRResult:
        tail = self._normalize(json.loads(self._rec.FinalResult()).get("text", ""))
        parts = self._segments + ([tail] if tail else [])
        self._segments.clear()
        self._new_recognizer()
        full = "".join(parts).strip()
        return ASRResult(full, 0.8 if full else 0.65)

    def reset(self) -> None:
        self._segments.clear()
        self._new_recognizer()


class TextStubASR(ASRBackend):
    name = "text"

    def __init__(self, text: str = "", confidence: float = 1.0):
        self.text = text
        self.confidence = confidence

    def feed(self, pcm16: bytes) -> Iterator[ASRResult]:
        return iter(())

    def finalize(self) -> ASRResult:
        return ASRResult(self.text, self.confidence)


def resolve_vosk_model(asr_cfg: dict, service_dir: str | None = None) -> str | None:
    raw = str(asr_cfg.get("vosk_model_path", "")).strip()
    if not raw:
        return None
    if not os.path.isabs(raw) and service_dir:
        raw = os.path.join(service_dir, raw)
    return raw if os.path.isdir(raw) else None


def create_asr(cfg: dict, service_dir: str | None = None) -> ASRBackend | None:
    asr_cfg = cfg.get("asr", {})
    choice = str(asr_cfg.get("backend", "auto")).lower()
    if choice == "none":
        return None
    if choice in ("auto", "funasr"):
        try:
            return FunASRBackend(str(asr_cfg.get("model", "paraformer-zh-streaming")))
        except Exception:
            if choice == "funasr":
                raise
    if choice in ("auto", "vosk"):
        model_path = resolve_vosk_model(asr_cfg, service_dir)
        if model_path:
            try:
                return VoskASRBackend(model_path, int(asr_cfg.get("sample_rate", 16000)))
            except Exception:
                if choice == "vosk":
                    raise
        elif choice == "vosk":
            raise FileNotFoundError(
                f"vosk model missing: {asr_cfg.get('vosk_model_path', '')} "
                "(download vosk-model-small-cn-0.22 and unzip into ai-service/models/)"
            )
    return None
