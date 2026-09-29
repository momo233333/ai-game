import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core.corpus import Corpus, Line

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REAL_CSV = os.path.join(SERVICE_DIR, "..", "voice-corpus", "lines.csv")

CSV = """id,text,emotion,intent_tags,priority
A1,左转了您嘞,CALM,TURN_LEFT,70
A2,又是左转,ANNOYED,TURN_LEFT,40
A3,右转收到,CALM,TURN_RIGHT,60
A4,随便开开,HAPPY,IGNORE,50
A5,这句没人匹配,CALM,,10
"""


class LineTest(unittest.TestCase):
    def test_voicelib_uri(self):
        self.assertEqual(Line(id="X1", text="hi", emotion="CALM").voicelib_uri, "voicelib://X1")


class CorpusTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.csv = os.path.join(self.tmp, "lines.csv")
        with open(self.csv, "w", encoding="utf-8") as fh:
            fh.write(CSV)
        self.corpus = Corpus(self.csv)

    def test_load(self):
        self.assertEqual(len(self.corpus.lines), 5)
        self.assertEqual(self.corpus.get("A1").text, "左转了您嘞")
        self.assertIsNone(self.corpus.get("nope"))

    def test_missing_file_is_empty(self):
        empty = Corpus(os.path.join(self.tmp, "missing.csv"))
        self.assertEqual(empty.lines, [])
        self.assertIsNone(empty.pick(intent="TURN_LEFT"))

    def test_candidates_ranking(self):
        ranked = self.corpus.candidates(intent="TURN_LEFT", emotion="ANNOYED")
        self.assertEqual({line.id for line in ranked}, {"A1", "A2"})
        self.assertEqual(ranked[0].id, "A1")
        without_a1 = self.corpus.pick(intent="TURN_LEFT", emotion="ANNOYED", exclude_ids={"A1"})
        self.assertEqual(without_a1.id, "A2")

    def test_pick_respects_exclude(self):
        picked = self.corpus.pick(intent="TURN_LEFT", emotion="CALM")
        self.assertEqual(picked.id, "A1")
        again = self.corpus.pick(intent="TURN_LEFT", emotion="CALM", exclude_ids={"A1"})
        self.assertEqual(again.id, "A2")
        none = self.corpus.pick(intent="TURN_LEFT", exclude_ids={"A1", "A2"})
        self.assertIsNone(none)

    def test_keyword_match(self):
        picked = self.corpus.pick(keyword="随便")
        self.assertEqual(picked.id, "A4")

    def test_prompt_lines_sorted_by_priority(self):
        lines = self.corpus.prompt_lines(limit=2)
        self.assertTrue(lines[0].startswith("voicelib://A1"))
        self.assertEqual(len(lines), 2)

    def test_intent_tag_parsing(self):
        line = self.corpus.get("A5")
        self.assertEqual(line.intent_tags, [])


class RealCorpusTest(unittest.TestCase):
    def test_real_corpus_loads(self):
        corpus = Corpus(REAL_CSV)
        self.assertGreater(len(corpus.lines), 50)
        tags = set()
        for line in corpus.lines:
            tags.update(line.intent_tags)
        self.assertLessEqual(
            {"TURN_LEFT", "TURN_RIGHT", "ACCELERATE", "BRAKE", "LANE_CHANGE", "PARK", "IGNORE", "QUESTION_BACK"},
            tags,
        )

    def test_no_duplicate_ids(self):
        corpus = Corpus(REAL_CSV)
        ids = [line.id for line in corpus.lines]
        self.assertEqual(len(ids), len(set(ids)))


if __name__ == "__main__":
    unittest.main()
