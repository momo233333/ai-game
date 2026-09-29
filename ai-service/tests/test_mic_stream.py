import sys
import types
import unittest
from unittest import mock

import numpy as np

import core.mic as mic_mod


class FakeStream:
    def __init__(self, **kwargs):
        self.kwargs = kwargs
        self.callback = kwargs.get("callback")
        self.started = False
        self.stopped = False
        self.closed = False

    def start(self):
        self.started = True

    def stop(self):
        self.stopped = True

    def close(self):
        self.closed = True


def fake_sounddevice(stream_holder: dict):
    def InputStream(**kwargs):
        stream_holder["stream"] = FakeStream(**kwargs)
        return stream_holder["stream"]

    return types.SimpleNamespace(
        default=types.SimpleNamespace(device=[1]),
        query_devices=lambda index: {"max_input_channels": 1, "default_samplerate": 44100},
        InputStream=InputStream,
    )


class MicStreamTest(unittest.TestCase):
    def test_open_callback_and_frames(self):
        holder: dict = {}
        fake = fake_sounddevice(holder)
        mic = mic_mod.MicCapture(block_ms=100, target_rate=16000)
        with mock.patch.dict(sys.modules, {"sounddevice": fake}):
            with mic:
                stream = holder["stream"]
                self.assertTrue(stream.started)
                self.assertEqual(stream.kwargs["samplerate"], 44100)
                blocksize = stream.kwargs["blocksize"]
                self.assertEqual(blocksize, 4410)
                block = np.full(blocksize, 3000, dtype=np.int16)
                stream.callback(block.reshape(-1, 1), blocksize, None, None)
                frames = []
                for frame in mic.frames():
                    frames.append(frame)
                    if len(frames) >= 1:
                        break
        self.assertEqual(len(frames), 1)
        self.assertAlmostEqual(len(frames[0]) // 2, 1600, delta=4)
        self.assertTrue(stream.stopped)
        self.assertTrue(stream.closed)

    def test_queue_overflow_drops_oldest(self):
        holder: dict = {}
        fake = fake_sounddevice(holder)
        mic = mic_mod.MicCapture(block_ms=100, max_queue=2)
        with mock.patch.dict(sys.modules, {"sounddevice": fake}):
            with mic:
                stream = holder["stream"]
                block = np.zeros(10, dtype=np.int16)
                for value in (100, 200, 300):
                    block[:] = value
                    stream.callback(block.reshape(-1, 1), 10, None, None)
                self.assertEqual(mic._queue.qsize(), 2)
                first, _ = mic._queue.get_nowait()
                self.assertEqual(int(np.frombuffer(first, dtype="<i2")[0]), 200)

    def test_close_puts_sentinel(self):
        mic = mic_mod.MicCapture()
        mic.close()
        item = mic._queue.get_nowait()
        self.assertIsNone(item)


if __name__ == "__main__":
    unittest.main()
