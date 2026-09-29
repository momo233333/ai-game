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


if __name__ == "__main__":
    unittest.main()
