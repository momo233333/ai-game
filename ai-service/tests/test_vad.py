import os
import struct
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core.vad import EnergyVAD, _rms, create_vad


def silence(ms: int, sample_rate: int = 16000) -> bytes:
    return b"\x00\x00" * int(sample_rate * ms / 1000)


def tone(ms: int, amplitude: int = 12000, sample_rate: int = 16000) -> bytes:
    count = int(sample_rate * ms / 1000)
    return struct.pack(f"<{count}h", *([amplitude, -amplitude] * (count // 2 + 1))[:count])


class RmsTest(unittest.TestCase):
    def test_silence_is_zero(self):
        self.assertEqual(_rms(silence(50)), 0.0)

    def test_full_scale_square_wave(self):
        self.assertAlmostEqual(_rms(tone(50, amplitude=32767)), 1.0, places=2)

    def test_empty_buffer(self):
        self.assertEqual(_rms(b""), 0.0)


class EnergyVADTest(unittest.TestCase):
    def test_speech_start_and_silence_timeout(self):
        vad = EnergyVAD(threshold=0.02, min_silence_ms=600)
        self.assertFalse(vad.speech_started)
        for _ in range(3):
            self.assertTrue(vad.is_speech(tone(40)))
        self.assertTrue(vad.speech_started)
        for _ in range(14):
            vad.is_speech(silence(40))
        self.assertFalse(vad.silence_exceeded())
        vad.is_speech(silence(300))
        self.assertTrue(vad.silence_exceeded())

    def test_silence_before_speech_never_ends_turn(self):
        vad = EnergyVAD(threshold=0.02, min_silence_ms=200)
        for _ in range(20):
            vad.is_speech(silence(50))
        self.assertFalse(vad.silence_exceeded())

    def test_not_enough_speech(self):
        vad = EnergyVAD(threshold=0.02, min_silence_ms=100)
        vad.is_speech(tone(50))
        self.assertFalse(vad.has_enough_speech(200))
        vad.is_speech(tone(200))
        self.assertTrue(vad.has_enough_speech(200))

    def test_reset_clears_state(self):
        vad = EnergyVAD(threshold=0.02, min_silence_ms=100)
        vad.is_speech(tone(100))
        vad.is_speech(silence(500))
        vad.reset()
        self.assertFalse(vad.speech_started)
        self.assertFalse(vad.silence_exceeded())


class CreateVadTest(unittest.TestCase):
    def test_energy_backend_explicit(self):
        vad = create_vad({"vad": {"backend": "energy", "energy_threshold": 0.05, "min_silence_ms": 300}})
        self.assertEqual(vad.name, "energy")
        self.assertEqual(vad.threshold, 0.05)
        self.assertEqual(vad.min_silence_ms, 300)

    def test_auto_falls_back_without_onnxruntime(self):
        vad = create_vad({"vad": {"backend": "auto"}})
        self.assertEqual(vad.name, "energy")

    def test_silero_missing_raises_when_forced(self):
        with self.assertRaises(Exception):
            create_vad({"vad": {"backend": "silero"}})

    def test_auto_uses_silero_when_available(self):
        from unittest import mock

        from core.vad import SileroVAD

        sentinel = object()
        with mock.patch("core.vad.SileroVAD", return_value=sentinel) as ctor:
            got = create_vad({"vad": {"backend": "auto", "threshold": 0.6, "min_silence_ms": 700}})
        self.assertIs(got, sentinel)
        ctor.assert_called_once_with(threshold=0.6, min_silence_ms=700)


class AbstractVadTest(unittest.TestCase):
    def test_base_methods_are_abstract(self):
        from core.vad import VADBase

        bare = object()
        with self.assertRaises(NotImplementedError):
            VADBase.is_speech(bare, b"")
        with self.assertRaises(NotImplementedError):
            VADBase.reset(bare)


try:
    import numpy as np
except ImportError:  # pragma: no cover
    np = None


class _FakeSileroSession:
    def __init__(self, prob: float):
        self.prob = prob

    def run(self, outputs, inputs):
        return [[[self.prob]]]


def make_silero(prob: float = 0.9):
    """绕过 __init__（要 onnxruntime）直接组装可测实例，专测 is_speech 分支。"""
    from core.vad import EnergyVAD, SileroVAD

    vad = SileroVAD.__new__(SileroVAD)
    EnergyVAD.__init__(vad, threshold=0.3, min_silence_ms=600, sample_rate=16000)
    vad._np = np
    vad.session = _FakeSileroSession(prob)
    vad.onnx_threshold = 0.5
    vad._state = []
    return vad


@unittest.skipUnless(np is not None, "numpy 未安装")
class SileroIsSpeechTest(unittest.TestCase):
    def test_init_builds_session_when_onnxruntime_present(self):
        import types
        from unittest import mock

        from core.vad import SileroVAD

        fake_ort = types.SimpleNamespace(InferenceSession=lambda *a, **k: _FakeSileroSession(0.9))
        with mock.patch.dict(sys.modules, {"onnxruntime": fake_ort}):
            vad = SileroVAD(threshold=0.7, min_silence_ms=500)
        self.assertEqual(vad.name, "silero")
        self.assertEqual(vad.onnx_threshold, 0.7)
        self.assertEqual(vad.min_silence_ms, 500)
        self.assertTrue(vad.is_speech(tone(64)))

    def test_too_short_frame_is_not_speech(self):
        vad = make_silero(0.9)
        self.assertFalse(vad.is_speech(silence(10, sample_rate=16000)))  # 160 samples < 512

    def test_high_prob_starts_speech(self):
        vad = make_silero(0.9)
        self.assertTrue(vad.is_speech(tone(64)))  # 1024 samples
        self.assertTrue(vad.speech_started)

    def test_low_prob_accumulates_silence_after_start(self):
        vad = make_silero(0.9)
        vad.is_speech(tone(64))
        vad2 = make_silero(0.1)
        vad2.speech_started = True
        vad2.is_speech(tone(64))
        self.assertGreater(vad2._silence_ms, 0)
        vad2.reset()
        self.assertFalse(vad2.speech_started)


if __name__ == "__main__":
    unittest.main()
