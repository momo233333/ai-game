import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core.corpus import Corpus
from core.dialogue import DialogueCore, MemoryStore
from core.llm import RuleBasedBackend, create_backend
from core.types import GameState

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORPUS_PATH = os.path.join(SERVICE_DIR, "..", "voice-corpus", "lines.csv")


class MemoryStoreTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.db = os.path.join(self.tmp, "memory.db")
        self.store = MemoryStore(self.db, "slot0")

    def tearDown(self):
        self.store.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_debts_persist_and_fifo(self):
        for i in range(7):
            self.store.add_debt(f"debt-{i}", limit=5)
        debts = self.store.load_debts()
        self.assertEqual(len(debts), 5)
        self.assertEqual(debts[-1], "debt-6")

    def test_summary_upsert(self):
        self.store.save_summary("第一次摘要")
        self.store.save_summary("第二次摘要")
        self.assertEqual(self.store.load_summary(), "第二次摘要")

    def test_slots_partitioned(self):
        other = MemoryStore(self.db, "slot1")
        self.store.add_debt("slot0-debt", limit=5)
        other.add_debt("slot1-debt", limit=5)
        self.assertEqual(self.store.load_debts(), ["slot0-debt"])
        self.assertEqual(other.load_debts(), ["slot1-debt"])
        other.close()


class DialogueCoreTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        cfg = {
            "memory": {
                "recent_turns": 3,
                "old_debt_limit": 2,
                "old_debt_threshold": -5,
                "summarize_every": 2,
            },
            "llm": {"max_reply_chars": 40},
        }
        corpus = Corpus(CORPUS_PATH)
        backend = RuleBasedBackend(corpus)
        store = MemoryStore(os.path.join(self.tmp, "m.db"), "s")
        self.core = DialogueCore(cfg, corpus, backend, store)

    def tearDown(self):
        self.core.store.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_recent_window_slides(self):
        for i in range(6):
            self.core.record_turn(f"player-{i}", f"reply-{i}", 0.0)
        self.assertEqual(len(self.core.memory.recent), 3)
        self.assertEqual(self.core.memory.recent[-1]["player"], "player-5")

    def test_old_debts_written_on_big_negative(self):
        self.core.record_turn("危险指令", "拒绝", -5.0)
        self.assertEqual(len(self.core.memory.old_debts), 1)
        self.assertIn("危险指令", self.core.memory.old_debts[0])
        self.assertEqual(len(self.core.store.load_debts()), 1)

    def test_small_delta_not_a_debt(self):
        self.core.record_turn("正常", "收到", -3.0)
        self.assertEqual(self.core.memory.old_debts, [])

    def test_summary_compressed_every_n_turns(self):
        for i in range(2):
            self.core.record_turn(f"玩家{i}", f"回复{i}", 1.0)
        self.assertTrue(self.core.memory.summary)

    def test_prompt_contains_state_and_memory(self):
        state = GameState()
        state.trust = 42.0
        prompt = self.core.build_prompt(state)
        self.assertIn("信任：42/100", prompt)
        self.assertIn("林小雨", prompt)
        self.assertIn("voicelib://", prompt)
        self.assertIn("TURN_LEFT", prompt)


class BackendFactoryTest(unittest.TestCase):
    def test_auto_falls_back_to_rule(self):
        corpus = Corpus(CORPUS_PATH)
        backend = create_backend({"backend": "auto", "model_path": "missing.gguf"}, corpus, "prompt")
        self.assertEqual(backend.name, "rule")

    def test_rule_classifies_commands(self):
        corpus = Corpus(CORPUS_PATH)
        backend = RuleBasedBackend(corpus)
        cases = {
            "往左转": "TURN_LEFT",
            "右转": "TURN_RIGHT",
            "加速": "ACCELERATE",
            "刹车": "BRAKE",
            "变道": "LANE_CHANGE",
            "靠边停车": "PARK",
            "闯红灯": "IGNORE",
        }
        for text, intent in cases.items():
            got, _ = backend.classify(text)
            self.assertEqual(got, intent, text)

    def test_stream_yields_action_first(self):
        corpus = Corpus(CORPUS_PATH)
        backend = RuleBasedBackend(corpus)
        chunks = list(backend.stream({"player_text": "左转", "game_state": GameState()}))
        self.assertIn("action", chunks[0])
        deltas = [c for c in chunks if "reply_delta" in c]
        self.assertTrue(deltas)
        self.assertIn("trust_delta", chunks[-1])


if __name__ == "__main__":
    unittest.main()
