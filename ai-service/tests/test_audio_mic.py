import math
import os
import struct
import tempfile
import unittest
from array import array

from core.audio import load_wav_pcm, resample_pcm16, to_mono, wav_chunks, write_wav
from core.mic import MicCapture, _stereo_to_mono_bytes

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VOSK_MODEL = os.path.join(SERVICE_DIR, "models", "vosk-model-small-cn-0.22")


def sine_pcm(rate: int, seconds: float, freq: float = 440.0, amp: int = 8000) -> bytes:
    n = int(rate * seconds)
    out = array("h")
    for i in range(n):
        out.append(int(amp * math.sin(2 * math.pi * freq * i / rate)))
    return out.tobytes()


class ResampleTest(unittest.TestCase):
    def test_same_rate_passthrough(self):
        data = sine_pcm(16000, 0.1)
        self.assertEqual(resample_pcm16(data, 16000, 16000), data)

    def test_empty_passthrough(self):
        self.assertEqual(resample_pcm16(b"", 44100, 16000), b"")

    def test_48k_to_16k_duration(self):
        data = sine_pcm(48000, 1.0)
        out = resample_pcm16(data, 48000, 16000)
        self.assertEqual(len(out) // 2, 16000)

    def test_44100_to_16000_duration(self):
        data = sine_pcm(44100, 0.5)
        out = resample_pcm16(data, 44100, 16000)
        self.assertAlmostEqual(len(out) // 2, 8000, delta=4)

    def test_pure_python_fallback(self):
        import core.audio as audio_mod

        saved, audio_mod.np = audio_mod.np, None
        try:
            data = sine_pcm(44100, 0.2)
            out = resample_pcm16(data, 44100, 16000)
            self.assertAlmostEqual(len(out) // 2, 3200, delta=4)
        finally:
            audio_mod.np = saved

    def test_to_mono_average(self):
        stereo = array("h", [100, 200, -100, 0, 50, 50])
        mono = to_mono(stereo, 2)
        self.assertEqual(list(mono), [150, -50, 50])

    def test_to_mono_single_channel(self):
        mono = array("h", [1, 2, 3])
        self.assertIs(to_mono(mono, 1), mono)


class WavIoTest(unittest.TestCase):
    def test_write_and_chunks_roundtrip(self):
        pcm = sine_pcm(16000, 0.35)
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "t.wav")
            write_wav(path, pcm, rate=16000)
            chunks = list(wav_chunks(path, target_rate=16000, chunk_ms=100))
            self.assertEqual(len(chunks), 4)
            total = sum(len(c) for c in chunks) // 2
            self.assertAlmostEqual(total, 5600, delta=10)

    def test_chunks_resample_and_stereo(self):
        src_rate = 44100
        stereo = array("h")
        for i in range(src_rate // 2):
            v = int(6000 * math.sin(2 * math.pi * 440 * i / src_rate))
            stereo.extend([v, v])
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "s.wav")
            write_wav(path, stereo.tobytes(), rate=src_rate, channels=2)
            chunks = list(wav_chunks(path, target_rate=16000, chunk_ms=100))
            total = sum(len(c) for c in chunks) // 2
            self.assertAlmostEqual(total, 8000, delta=20)

    def test_missing_file(self):
        with self.assertRaises(FileNotFoundError):
            list(wav_chunks("no/such/file.wav"))

    def test_rejects_8bit(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "b.wav")
            import wave

            with wave.open(path, "wb") as wf:
                wf.setnchannels(1)
                wf.setsampwidth(1)
                wf.setframerate(8000)
                wf.writeframes(b"\x80" * 8000)
            with self.assertRaises(ValueError):
                list(wav_chunks(path))


class LoadWavPcmTest(unittest.TestCase):
    def test_roundtrip_same_rate(self):
        pcm = sine_pcm(24000, 0.2)
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "t.wav")
            write_wav(path, pcm, rate=24000)
            self.assertEqual(load_wav_pcm(path, 24000), pcm)

    def test_resample_44100_to_24000(self):
        pcm = sine_pcm(44100, 1.0)
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "t.wav")
            write_wav(path, pcm, rate=44100)
            out = load_wav_pcm(path, 24000)
        self.assertAlmostEqual(len(out) // 2, 24000, delta=10)

    def test_stereo_to_mono(self):
        stereo = array("h")
        for i in range(100):
            stereo.extend([100 + i, 300 + i])
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "s.wav")
            write_wav(path, stereo.tobytes(), rate=16000, channels=2)
            out = load_wav_pcm(path, 16000)
        self.assertEqual(struct.unpack(f"<{len(out) // 2}h", out)[:3], (200, 201, 202))

    def test_empty_wav(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "e.wav")
            write_wav(path, b"", rate=24000)
            self.assertEqual(load_wav_pcm(path, 24000), b"")

    def test_rejects_8bit(self):
        import wave

        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "b.wav")
            with wave.open(path, "wb") as wf:
                wf.setnchannels(1)
                wf.setsampwidth(1)
                wf.setframerate(8000)
                wf.writeframes(b"\x80" * 8000)
            with self.assertRaises(ValueError):
                load_wav_pcm(path, 16000)


class MicCaptureTest(unittest.TestCase):
    def test_frames_resample_and_stop(self):
        mic = MicCapture(device=0, target_rate=16000)
        mic._queue.put((sine_pcm(44100, 0.1), 44100))
        mic._queue.put(None)
        got = list(mic.frames())
        self.assertEqual(len(got), 1)
        self.assertAlmostEqual(len(got[0]) // 2, 1600, delta=4)

    def test_stereo_to_mono_bytes(self):
        data = struct.pack("<4h", 100, 200, -100, 0)
        mono = _stereo_to_mono_bytes(data)
        self.assertEqual(struct.unpack("<2h", mono), (150, -50))

    def test_requires_sounddevice(self):
        import core.mic as mic_mod

        saved, mic_mod.sd = mic_mod.sd, None
        try:
            with self.assertRaises(RuntimeError):
                MicCapture()
        finally:
            mic_mod.sd = saved


class VoskFactoryTest(unittest.TestCase):
    def test_resolve_missing_path_returns_none(self):
        from core.asr import resolve_vosk_model

        self.assertIsNone(resolve_vosk_model({"vosk_model_path": "models/nope"}, SERVICE_DIR))
        self.assertIsNone(resolve_vosk_model({}, SERVICE_DIR))

    def test_vosk_explicit_missing_model_raises(self):
        from core.asr import create_asr

        cfg = {"asr": {"backend": "vosk", "vosk_model_path": "models/definitely-missing"}}
        with self.assertRaises(FileNotFoundError):
            create_asr(cfg, service_dir=SERVICE_DIR)

    def test_auto_falls_back_to_none_without_deps(self):
        import unittest.mock as mock

        from core.asr import create_asr

        cfg = {"asr": {"backend": "auto", "vosk_model_path": "models/definitely-missing"}}
        with mock.patch.dict("sys.modules", {"funasr": None}):
            self.assertIsNone(create_asr(cfg, service_dir=SERVICE_DIR))


@unittest.skipUnless(os.path.isdir(VOSK_MODEL), "vosk model not downloaded yet")
class VoskBackendTest(unittest.TestCase):
    def test_silence_finalize_empty(self):
        from core.asr import VoskASRBackend

        asr = VoskASRBackend(VOSK_MODEL)
        silence = b"\x00\x00" * 16000
        for _ in range(4):
            list(asr.feed(silence))
        result = asr.finalize()
        self.assertEqual(result.text, "")
        self.assertGreaterEqual(result.confidence, 0.6)
        self.assertFalse(result.low_confidence)


if __name__ == "__main__":
    unittest.main()
