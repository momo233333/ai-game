import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from config import DEFAULTS, _deep_merge, _mini_yaml, load_config, resolve_path


class MiniYamlTest(unittest.TestCase):
    def test_flat_and_nested(self):
        text = (
            "server:\n"
            "  port: 60051   # comment\n"
            "  transport: json\n"
            "llm:\n"
            "  backend: rule\n"
            "tts:\n"
            "  voicelib_enabled: true\n"
            "  realtime_enabled: false\n"
            "top: hello\n"
            "num: 3.5\n"
        )
        data = _mini_yaml(text)
        self.assertEqual(data["server"]["port"], 60051)
        self.assertEqual(data["server"]["transport"], "json")
        self.assertEqual(data["llm"]["backend"], "rule")
        self.assertTrue(data["tts"]["voicelib_enabled"])
        self.assertFalse(data["tts"]["realtime_enabled"])
        self.assertEqual(data["top"], "hello")
        self.assertEqual(data["num"], 3.5)

    def test_ignores_junk_lines(self):
        data = _mini_yaml("just_text\nserver:\n  host: 127.0.0.1\n")
        self.assertEqual(data["server"]["host"], "127.0.0.1")


class DeepMergeTest(unittest.TestCase):
    def test_nested_override_keeps_siblings(self):
        base = {"a": {"x": 1, "y": 2}, "b": 3}
        override = {"a": {"y": 9}}
        merged = _deep_merge(base, override)
        self.assertEqual(merged["a"], {"x": 1, "y": 9})
        self.assertEqual(merged["b"], 3)
        self.assertEqual(base["a"]["y"], 2)

    def test_non_dict_replaces(self):
        merged = _deep_merge({"a": {"x": 1}}, {"a": "flat"})
        self.assertEqual(merged["a"], "flat")


class LoadConfigTest(unittest.TestCase):
    def test_missing_file_returns_defaults_copy(self):
        cfg = load_config(os.path.join(tempfile.gettempdir(), "no_such_config.yaml"))
        self.assertEqual(cfg["server"]["port"], 50051)
        cfg["server"]["port"] = 1
        self.assertEqual(DEFAULTS["server"]["port"], 50051)

    def test_real_config_loads_and_merges(self):
        cfg = load_config()
        self.assertEqual(cfg["server"]["port"], 50051)
        self.assertIn("llm", cfg)
        self.assertIn("max_reply_chars", cfg["llm"])
        self.assertIn("old_debt_threshold", cfg["memory"])

    def test_partial_file_merges_defaults(self):
        path = os.path.join(tempfile.gettempdir(), "partial_cfg.yaml")
        with open(path, "w", encoding="utf-8") as fh:
            fh.write("llm:\n  backend: rule\n")
        try:
            cfg = load_config(path)
            self.assertEqual(cfg["llm"]["backend"], "rule")
            self.assertEqual(cfg["llm"]["n_ctx"], 4096)
            self.assertEqual(cfg["server"]["port"], 50051)
        finally:
            os.remove(path)


class ResolvePathTest(unittest.TestCase):
    def test_absolute_passthrough(self):
        target = os.path.abspath(os.sep)
        self.assertEqual(resolve_path("/any/base", target), target)

    def test_relative_against_directory(self):
        service_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        resolved = resolve_path(service_dir, "../voice-corpus/lines.csv")
        self.assertTrue(os.path.exists(resolved))

    def test_relative_against_file(self):
        service_file = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "config.py")
        resolved = resolve_path(service_file, "data/sensitive_words.txt")
        self.assertTrue(os.path.exists(resolved))


if __name__ == "__main__":
    unittest.main()
