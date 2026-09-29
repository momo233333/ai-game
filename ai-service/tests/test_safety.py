import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core.safety import AhoCorasick, SafetyFilter


class AhoCorasickTest(unittest.TestCase):
    def test_finds_single_and_overlapping(self):
        ac = AhoCorasick(["傻逼", "傻逼东西", "滚开"])
        self.assertEqual(ac.find("你这个傻逼"), ["傻逼"])
        self.assertEqual(sorted(ac.find("傻逼东西滚开")), ["傻逼", "傻逼东西", "滚开"])

    def test_no_match(self):
        ac = AhoCorasick(["傻逼"])
        self.assertEqual(ac.find("今天天气不错"), [])


class SafetyFilterTest(unittest.TestCase):
    def setUp(self):
        self.safety = SafetyFilter(None)

    def test_blocked_input(self):
        verdict = self.safety.check("你是不是傻逼啊")
        self.assertTrue(verdict.blocked)
        self.assertIn("傻逼", verdict.hits)
        self.assertNotIn("傻逼", verdict.cleaned_text)

    def test_clean_input_passes(self):
        verdict = self.safety.check("前面路口左转")
        self.assertFalse(verdict.blocked)
        self.assertEqual(verdict.cleaned_text, "前面路口左转")

    def test_redacted_lines_per_emotion(self):
        verdict = self.safety.check("傻逼")
        lines = {e: self.safety.redacted(verdict, e) for e in ("CALM", "ANNOYED", "NERVOUS", "HAPPY")}
        self.assertEqual(len(set(lines.values())), 4)
        for line in lines.values():
            self.assertTrue(0 < len(line) <= 30)

    def test_wordlist_file(self):
        here = os.path.dirname(os.path.abspath(__file__))
        path = os.path.join(os.path.dirname(here), "data", "sensitive_words.txt")
        safety = SafetyFilter(path)
        self.assertTrue(safety.check("你这个智障").blocked)


if __name__ == "__main__":
    unittest.main()
