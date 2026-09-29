from __future__ import annotations

import queue
import threading
from typing import Iterator

from .audio import TARGET_RATE, resample_pcm16

try:
    import sounddevice as sd
except ImportError:
    sd = None


class MicCapture:
    def __init__(self, device=None, target_rate: int = TARGET_RATE, block_ms: int = 100, max_queue: int = 200):
        if sd is None:
            raise RuntimeError("sounddevice not installed: pip install sounddevice")
        self.device = device
        self.target_rate = target_rate
        self.block_ms = block_ms
        self.max_queue = max_queue
        self._queue: queue.Queue[bytes | None] = queue.Queue(maxsize=max_queue)
        self._stream = None
        self._stopped = threading.Event()

    def _open(self):
        import sounddevice as sd_mod

        dev_index = self.device
        if dev_index is None:
            dev_index = sd_mod.default.device[0]
        info = sd_mod.query_devices(dev_index)
        in_channels = min(int(info["max_input_channels"]), 2)
        dev_rate = int(info["default_samplerate"])
        blocksize = max(1, int(dev_rate * self.block_ms / 1000))

        def callback(indata, frames, time_info, status):
            if self._stopped.is_set():
                return
            data = bytes(indata)
            if in_channels == 2:
                data = _stereo_to_mono_bytes(data)
            if self._queue.full():
                try:
                    self._queue.get_nowait()
                except queue.Empty:
                    pass
            try:
                self._queue.put_nowait((data, dev_rate))
            except queue.Full:
                pass

        stream = sd_mod.InputStream(
            device=dev_index,
            channels=in_channels,
            samplerate=dev_rate,
            blocksize=blocksize,
            dtype="int16",
            callback=callback,
        )
        stream.start()
        return stream, dev_rate

    def __enter__(self):
        self._stream, self._dev_rate = self._open()
        return self

    def __exit__(self, exc_type, exc, tb):
        self.close()
        return False

    def close(self):
        self._stopped.set()
        if self._stream is not None:
            try:
                self._stream.stop()
                self._stream.close()
            finally:
                self._stream = None
        try:
            self._queue.put_nowait(None)
        except queue.Full:
            pass

    def drain(self) -> None:
        while True:
            try:
                self._queue.get_nowait()
            except queue.Empty:
                return

    def frames(self) -> Iterator[bytes]:
        while not self._stopped.is_set():
            try:
                item = self._queue.get(timeout=0.5)
            except queue.Empty:
                if self._stopped.is_set():
                    return
                continue
            if item is None:
                return
            data, dev_rate = item
            yield resample_pcm16(data, dev_rate, self.target_rate)


def _stereo_to_mono_bytes(data: bytes) -> bytes:
    from array import array

    samples = array("h")
    samples.frombytes(data)
    mono = array("h")
    for i in range(0, len(samples) - 1, 2):
        mono.append((samples[i] + samples[i + 1]) // 2)
    return mono.tobytes()
