from __future__ import annotations

import os
import sqlite3
import time
from dataclasses import dataclass, field

from .corpus import Corpus
from .llm import LLMBackend
from .types import GameState

SYSTEM_TEMPLATE = """[身份] 你是林小雨，21岁，傲娇毒舌但车技稳，最烦指令不清不楚的人。
[规则1] 输出严格遵循JSON格式：{{"action":{{"intent":"...","steering":0,"throttle":0,"brake":0,"hold_ms":600}},"tts_hint":null,"reply":"...","trust_delta":0}}
[规则2] action.intent 只能从以下枚举选择：{intents}
[规则3] reply≤{max_reply}字，口语化，可用"哥""诶""啧"等语气词，禁止书面语。
[规则4] 指令模糊时 intent=QUESTION_BACK，不要猜。
[规则5] 涉及危险驾驶（闯红灯/逆行/撞人）时 intent=IGNORE，trust_delta=-5，并斥责。
[可用台词库] {voice_lines}
[当前状态] {state_line}
[记忆] {summary}{debts}"""


@dataclass
class Memory:
    recent: list[dict] = field(default_factory=list)
    summary: str = ""
    old_debts: list[str] = field(default_factory=list)
    turn_count: int = 0


class MemoryStore:
    def __init__(self, db_path: str, save_slot: str):
        self.save_slot = save_slot
        self.db_path = db_path
        os.makedirs(os.path.dirname(os.path.abspath(db_path)) or ".", exist_ok=True)
        self._conn = sqlite3.connect(db_path, check_same_thread=False)
        self._conn.execute(
            "CREATE TABLE IF NOT EXISTS old_debts ("
            "slot TEXT NOT NULL, ts REAL NOT NULL, text TEXT NOT NULL, PRIMARY KEY (slot, ts))"
        )
        self._conn.execute(
            "CREATE TABLE IF NOT EXISTS summaries (slot TEXT PRIMARY KEY, text TEXT NOT NULL)"
        )
        self._conn.commit()

    def load_debts(self) -> list[str]:
        cur = self._conn.execute("SELECT text FROM old_debts WHERE slot=? ORDER BY ts", (self.save_slot,))
        return [row[0] for row in cur.fetchall()]

    def add_debt(self, text: str, limit: int) -> None:
        self._conn.execute("INSERT INTO old_debts (slot, ts, text) VALUES (?, ?, ?)", (self.save_slot, time.time(), text))
        self._conn.commit()
        cur = self._conn.execute("SELECT ts FROM old_debts WHERE slot=? ORDER BY ts", (self.save_slot,))
        rows = [row[0] for row in cur.fetchall()]
        if len(rows) > limit:
            for ts in rows[: len(rows) - limit]:
                self._conn.execute("DELETE FROM old_debts WHERE slot=? AND ts=?", (self.save_slot, ts))
            self._conn.commit()

    def load_summary(self) -> str:
        cur = self._conn.execute("SELECT text FROM summaries WHERE slot=?", (self.save_slot,))
        row = cur.fetchone()
        return row[0] if row else ""

    def save_summary(self, text: str) -> None:
        self._conn.execute(
            "INSERT INTO summaries (slot, text) VALUES (?, ?) "
            "ON CONFLICT(slot) DO UPDATE SET text=excluded.text",
            (self.save_slot, text),
        )
        self._conn.commit()

    def close(self) -> None:
        self._conn.close()

    def __del__(self):
        try:
            self._conn.close()
        except Exception:
            pass


class DialogueCore:
    def __init__(
        self,
        cfg: dict,
        corpus: Corpus,
        backend: LLMBackend,
        store: MemoryStore | None = None,
    ):
        self.cfg = cfg
        self.corpus = corpus
        self.backend = backend
        mem_cfg = cfg.get("memory", {})
        self.recent_limit = int(mem_cfg.get("recent_turns", 8))
        self.debt_limit = int(mem_cfg.get("old_debt_limit", 5))
        self.debt_threshold = float(mem_cfg.get("old_debt_threshold", -5))
        self.summarize_every = int(mem_cfg.get("summarize_every", 5))
        self.max_reply_chars = int(cfg.get("llm", {}).get("max_reply_chars", 40))
        self.store = store
        self.memory = Memory(
            old_debts=store.load_debts() if store else [],
            summary=store.load_summary() if store else "",
        )

    def build_state_line(self, state: GameState) -> str:
        telemetry = state.telemetry
        obstacles = telemetry.obstacle_summary or "路况正常"
        return (
            f"当前车速{telemetry.speed_kmh:.0f}km/h，档位{telemetry.gear}，{obstacles}。"
            f"你的情绪：{state.emotion_name()}。你对哥哥的信任：{state.trust:.0f}/100。"
            f"场景：{state.scene.scene_id}，{state.scene.weather}，{state.scene.time_of_day}。"
        )

    def build_prompt(self, state: GameState) -> str:
        debts = ""
        if self.memory.old_debts:
            debts = "\n[旧账] " + "；".join(self.memory.old_debts[-self.debt_limit :])
        summary = self.memory.summary or "（暂无）"
        return SYSTEM_TEMPLATE.format(
            intents=", ".join(sorted(self._allowed_intents())),
            max_reply=self.max_reply_chars,
            voice_lines="\n".join(self.corpus.prompt_lines(40)),
            state_line=self.build_state_line(state),
            summary=summary,
            debts=debts,
        )

    def _allowed_intents(self) -> set[str]:
        from .types import ALLOWED_INTENTS

        return ALLOWED_INTENTS

    def generate(self, player_text: str, state: GameState) -> dict:
        ctx = {
            "player_text": player_text,
            "game_state": state,
            "prompt": self.build_prompt(state),
            "max_reply_chars": self.max_reply_chars,
            "corpus": self.corpus,
        }
        return self.backend.generate(ctx)

    def stream(self, player_text: str, state: GameState, chunk_chars: int = 4):
        ctx = {
            "player_text": player_text,
            "game_state": state,
            "prompt": self.build_prompt(state),
            "max_reply_chars": self.max_reply_chars,
            "corpus": self.corpus,
        }
        return self.backend.stream(ctx, chunk_chars=chunk_chars)

    def record_turn(self, player_text: str, reply: str, trust_delta: float) -> None:
        self.memory.recent.append({"player": player_text, "reply": reply, "delta": trust_delta})
        if len(self.memory.recent) > self.recent_limit:
            self.memory.recent = self.memory.recent[-self.recent_limit :]
        self.memory.turn_count += 1
        if trust_delta <= self.debt_threshold:
            debt = f"玩家说过：{player_text}（信任{trust_delta:+.0f}）"
            self.memory.old_debts.append(debt)
            if len(self.memory.old_debts) > self.debt_limit:
                self.memory.old_debts = self.memory.old_debts[-self.debt_limit :]
            if self.store:
                self.store.add_debt(debt, self.debt_limit)
        if self.summarize_every > 0 and self.memory.turn_count % self.summarize_every == 0:
            self._compress_summary()

    def _compress_summary(self) -> None:
        parts = [f"{turn['player']}→{turn['reply']}" for turn in self.memory.recent]
        raw = "；".join(parts)
        self.memory.summary = raw[:80]
        if self.store:
            self.store.save_summary(self.memory.summary)
