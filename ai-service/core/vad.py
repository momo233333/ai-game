from __future__ import annotations

import struct
from abc import ABC, abstractmethod


class VADBase(ABC):
    name: str = "base"

    @abstractmethod
    def is_speech(self, pcm16: bytes) -> bool:
        raise NotImplementedError

    @abstractmethod
    def reset(self) -> None:
        raise NotImplementedError


class EnergyVAD(VADBase):
    name = "energy"

    def __init__(self, threshold: float = 0.02, min_silence_ms: int = 600, sample_rate: int = 16000):
        self.threshold = threshold
        self.min_silence_ms = min_silence_ms
        self.sample_rate = sample_rate
        self.speech_started = False
        self._silence_ms = 0.0
        self._speech_ms = 0.0

    def is_speech(self, pcm16: bytes) -> bool:
        frame_ms = (len(pcm16) / 2) / self.sample_rate * 1000.0
        rms = _rms(pcm16)
        speaking = rms >= self.threshold
        if speaking:
            self.speech_started = True
            self._silence_ms = 0.0
            self._speech_ms += frame_ms
        elif self.speech_started:
            self._silence_ms += frame_ms
        return speaking

    def silence_exceeded(self) -> bool:
        return self.speech_started and self._silence_ms >= self.min_silence_ms

    def has_enough_speech(self, min_ms: float = 200.0) -> bool:
        return self._speech_ms >= min_ms

    def reset(self) -> None:
        self.speech_started = False
        self._silence_ms = 0.0
        self._speech_ms = 0.0


class SileroVAD(EnergyVAD):
    name = "silero"

    def __init__(self, threshold: float = 0.5, min_silence_ms: int = 600, sample_rate: int = 16000):
        super().__init__(threshold=0.3, min_silence_ms=min_silence_ms, sample_rate=sample_rate)
        import numpy as np
        import onnxruntime as ort

        self._np = np
        self.session = ort.InferenceSession("models/silero_vad.onnx", providers=["CPUExecutionProvider"])
        self.onnx_threshold = threshold
        self._state: list = []

    def is_speech(self, pcm16: bytes) -> bool:
        samples = self._np.frombuffer(pcm16, dtype=self._np.int16).astype(self._np.float32) / 32768.0
        if samples.size < 512:
            return False
        prob = float(self.session.run(None, {"input": samples.reshape(1, -1), "sr": self._np.int64(16000)})[0][0][0])
        speaking = prob >= self.onnx_threshold
        frame_ms = samples.size / self.sample_rate * 1000.0
        if speaking:
            self.speech_started = True
            self._silence_ms = 0.0
            self._speech_ms += frame_ms
        elif self.speech_started:
            self._silence_ms += frame_ms
        return speaking


def _rms(pcm16: bytes) -> float:
    count = len(pcm16) // 2
    if count == 0:
        return 0.0
    samples = struct.unpack(f"<{count}h", pcm16[: count * 2])
    total = sum(s * s for s in samples)
    return (total / count) ** 0.5 / 32768.0


def create_vad(cfg: dict) -> VADBase:
    vad_cfg = cfg.get("vad", {})
    choice = str(vad_cfg.get("backend", "auto")).lower()
    min_silence = int(vad_cfg.get("min_silence_ms", 600))
    if choice in ("auto", "silero"):
        try:
            return SileroVAD(
                threshold=float(vad_cfg.get("threshold", 0.5)),
                min_silence_ms=min_silence,
            )
        except Exception:
            if choice == "silero":
                raise
    return EnergyVAD(
        threshold=float(vad_cfg.get("energy_threshold", 0.02)),
        min_silence_ms=min_silence,
    )
