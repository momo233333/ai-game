import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from core.ptt import PttListener, ptt_label
from test_audio_and_cli import FakeASR, FakeVAD, make_cfg

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class CtrlStateTest(unittest.TestCase):
    def test_ctrl_down_and_up(self):
        ptt = PttListener()
        self.assertFalse(ptt.held())
        ptt._handle(0x11, True)
        self.assertTrue(ptt.held())
        ptt._handle(0x11, False)
        self.assertFalse(ptt.held())

    def test_left_and_right_ctrl(self):
        ptt = PttListener()
        ptt._handle(0xA2, True)
        self.assertTrue(ptt.held())
        ptt._handle(0xA3, False)
        self.assertFalse(ptt.held())

    def test_ignores_other_keys(self):
        ptt = PttListener()
        ptt._handle(0x41, True)
        ptt._handle(0x0D, True)
        self.assertFalse(ptt.held())

    @unittest.skipUnless(sys.platform == "win32", "windows hook")
    def test_real_hook_start_stop(self):
        with PttListener() as ptt:
            self.assertFalse(ptt.held())
            ptt._handle(0x11, True)
            self.assertTrue(ptt.held())
        self.assertFalse(ptt.held())

    def test_space_key_mode(self):
        ptt = PttListener(key="space")
        ptt._handle(0x11, True)
        self.assertFalse(ptt.held())
        ptt._handle(0x20, True)
        self.assertTrue(ptt.held())
        ptt._handle(0x20, False)
        self.assertFalse(ptt.held())

    def test_unknown_key_falls_back_to_ctrl(self):
        ptt = PttListener(key="nope")
        self.assertEqual(ptt.key, "ctrl")
        self.assertEqual(ptt_label(ptt.key), "Ctrl")


class StopOnSilenceTest(unittest.TestCase):
    def test_default_stops_at_vad_silence(self):
        from pipeline import DialoguePipeline

        vad = FakeVAD()
        pipeline = DialoguePipeline(
            make_cfg(), SERVICE_DIR, asr=FakeASR("往左打方向", 0.9), vad=vad
        )
        chunks = [bytes([i]) * 320 for i in range(1, 6)]
        list(pipeline.handle_audio(chunks))
        self.assertEqual(vad.frames, 2)

    def test_ptt_mode_consumes_all_chunks(self):
        from pipeline import DialoguePipeline

        vad = FakeVAD()
        pipeline = DialoguePipeline(
            make_cfg(), SERVICE_DIR, asr=FakeASR("往左打方向", 0.9), vad=vad
        )
        chunks = [bytes([i]) * 320 for i in range(1, 6)]
        events = list(pipeline.handle_audio(chunks, stop_on_silence=False))
        self.assertEqual(vad.frames, 5)
        self.assertIn("drive_action", [e.kind for e in events])


class MicDrainTest(unittest.TestCase):
    def test_drain_clears_queue(self):
        from core.mic import MicCapture

        mic = MicCapture()
        mic._queue.put((b"\x01" * 10, 16000))
        mic._queue.put((b"\x02" * 10, 16000))
        self.assertEqual(mic._queue.qsize(), 2)
        mic.drain()
        self.assertEqual(mic._queue.qsize(), 0)


if __name__ == "__main__":
    unittest.main()
