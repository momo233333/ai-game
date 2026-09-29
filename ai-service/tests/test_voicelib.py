import json
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core.tts.router import TTSRouter
from core.tts.voicelib import VoiceLib

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(SERVICE_DIR, "..", "voice-corpus", "manifest.json")


class VoiceLibTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not os.path.exists(MANIFEST):
            sys.path.insert(0, os.path.join(SERVICE_DIR, "..", "tools"))
            import voicelib_gen

            voicelib_gen.main([])
        cls.lib = VoiceLib(MANIFEST)

    def test_manifest_loaded(self):
        self.assertTrue(self.lib.loaded)
        self.assertGreater(len(self.lib.entries), 20)

    def test_exact_lookup(self):
        entry = self.lib.entries[0]
        found = self.lib.lookup(entry.text)
        self.assertIsNotNone(found)
        self.assertEqual(found.id, entry.id)

    def test_partial_lookup(self):
        found = self.lib.lookup("行，左转交给我吧")
        self.assertIsNotNone(found)

    def test_uri_resolve(self):
        entry = self.lib.entries[0]
        self.assertEqual(self.lib.resolve_uri(entry.voicelib_uri).id, entry.id)
        self.assertIsNone(self.lib.resolve_uri("voicelib://nope"))
        self.assertIsNone(self.lib.resolve_uri(None))


class TTSRouterTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.lib = VoiceLib(MANIFEST)

    def test_voicelib_route(self):
        router = TTSRouter({"tts": {"voicelib_enabled": True, "realtime_enabled": False}}, self.lib)
        line = self.lib.entries[3]
        result = router.route(line.text, emotion="CALM")
        self.assertEqual(result.kind, "voicelib")
        self.assertTrue(result.from_voicelib)

    def test_hint_uri_wins(self):
        router = TTSRouter({"tts": {"voicelib_enabled": True, "realtime_enabled": False}}, self.lib)
        line = self.lib.entries[5]
        other = self.lib.entries[0]
        result = router.route(other.text, tts_hint=line.voicelib_uri)
        self.assertEqual(result.entry.id, line.id)

    def test_disabled_falls_back_to_silent(self):
        router = TTSRouter({"tts": {"voicelib_enabled": False, "realtime_enabled": False}}, self.lib)
        result = router.route("随便说点什么")
        self.assertEqual(result.kind, "silent")
        self.assertFalse(result.from_voicelib)
        self.assertEqual(result.reason, "tts_disabled")

    def test_empty_text(self):
        router = TTSRouter({"tts": {}}, self.lib)
        self.assertEqual(router.route("   ").kind, "silent")


class ManifestShapeTest(unittest.TestCase):
    def test_manifest_fields(self):
        with open(MANIFEST, "r", encoding="utf-8") as fh:
            data = json.load(fh)
        self.assertIn("items", data)
        item = data["items"][0]
        for key in ("id", "text", "emotion", "intent_tags", "wav_path"):
            self.assertIn(key, item)


if __name__ == "__main__":
    unittest.main()
