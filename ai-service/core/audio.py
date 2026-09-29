from __future__ import annotations

import os
import wave
from array import array
from typing import Iterator

try:
    import numpy as np
except ImportError:
    np = None

TARGET_RATE = 16000
CHUNK_MS = 100


def to_mono(samples, channels: int):
    if channels == 1:
        return samples
    if np is not None and isinstance(samples, np.ndarray):
        return samples.reshape(-1, channels).mean(axis=1)
    mono = array("h")
    for i in range(0, len(samples) - channels + 1, channels):
        mono.append(sum(samples[i : i + channels]) // channels)
    return mono


def resample_pcm16(data: bytes, src_rate: int, dst_rate: int) -> bytes:
    if src_rate == dst_rate or not data:
        return data
    if np is not None:
        src = np.frombuffer(data, dtype="<i2").astype(np.float32)
        out_count = max(1, int(round(len(src) * dst_rate / src_rate)))
        x_old = np.linspace(0.0, 1.0, num=len(src), endpoint=False)
        x_new = np.linspace(0.0, 1.0, num=out_count, endpoint=False)
        out = np.interp(x_new, x_old, src).astype(np.int16)
        return out.tobytes()

    src = array("h")
    src.frombytes(data)
    out_count = max(1, int(round(len(src) * dst_rate / src_rate)))
    out = array("h")
    step = (len(src) - 1) / max(1, out_count - 1) if len(src) > 1 else 0.0
    for i in range(out_count):
        pos = i * step
        idx = int(pos)
        frac = pos - idx
        s0 = src[idx]
        s1 = src[idx + 1] if idx + 1 < len(src) else s0
        out.append(int(round(s0 + (s1 - s0) * frac)))
    return out.tobytes()


def wav_chunks(path: str, target_rate: int = TARGET_RATE, chunk_ms: int = CHUNK_MS) -> Iterator[bytes]:
    if not os.path.exists(path):
        raise FileNotFoundError(path)
    with wave.open(path, "rb") as wf:
        channels = wf.getnchannels()
        width = wf.getsampwidth()
        src_rate = wf.getframerate()
        if width != 2:
            raise ValueError(f"only 16-bit pcm supported, got width={width}")
        frames_per_chunk = max(1, int(src_rate * chunk_ms / 1000))
        while True:
            raw = wf.readframes(frames_per_chunk)
            if not raw:
                return
            samples = array("h")
            samples.frombytes(raw)
            mono = to_mono(samples, channels)
            pcm = mono.tobytes()
            yield resample_pcm16(pcm, src_rate, target_rate)


def write_wav(path: str, pcm16: bytes, rate: int = TARGET_RATE, channels: int = 1) -> None:
    with wave.open(path, "wb") as wf:
        wf.setnchannels(channels)
        wf.setsampwidth(2)
        wf.setframerate(rate)
        wf.writeframes(pcm16)
