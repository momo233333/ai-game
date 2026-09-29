from __future__ import annotations

import os
import re
from collections import deque
from dataclasses import dataclass

DEFAULT_WORDS = ["去死", "滚开", "傻逼", "白痴"]

_REJECTED_PATTERN = re.compile(r"(?:傻|笨|蠢|滚)(?:货|东西|帽|样|蛋)")


class AhoCorasick:
    def __init__(self, words: list[str]):
        self.goto: dict[tuple[int, str], int] = {}
        self.fail: dict[int, int] = {0: 0}
        self.out: dict[int, list[str]] = {}
        self._build([w for w in words if w])

    def _build(self, words: list[str]) -> None:
        next_state = 1
        for word in words:
            state = 0
            for ch in word:
                key = (state, ch)
                if key not in self.goto:
                    self.goto[key] = next_state
                    self.fail[next_state] = 0
                    self.out[next_state] = []
                    next_state += 1
                state = self.goto[key]
            self.out.setdefault(state, []).append(word)

        queue: deque[int] = deque()
        for (src, ch), dst in self.goto.items():
            if src == 0:
                self.fail[dst] = 0
                queue.append(dst)

        bfs_order: list[int] = []
        while queue:
            r = queue.popleft()
            bfs_order.append(r)
            for (src, ch), t in list(self.goto.items()):
                if src != r:
                    continue
                queue.append(t)
                f = self.fail[r]
                while f != 0 and (f, ch) not in self.goto:
                    f = self.fail[f]
                nxt = self.goto.get((f, ch))
                self.fail[t] = 0 if (nxt is None or nxt == t) else nxt

        for s in bfs_order:
            parent = self.out.get(self.fail[s], [])
            merged = list(self.out.get(s, []))
            for word in parent:
                if word not in merged:
                    merged.append(word)
            self.out[s] = merged

    def find(self, text: str) -> list[str]:
        hits: list[str] = []
        state = 0
        for ch in text:
            while state != 0 and (state, ch) not in self.goto:
                state = self.fail[state]
            state = self.goto.get((state, ch), 0)
            for word in self.out.get(state, []):
                if word not in hits:
                    hits.append(word)
        return hits


def load_wordlist(path: str) -> list[str]:
    if not os.path.exists(path):
        return list(DEFAULT_WORDS)
    words: list[str] = []
    with open(path, "r", encoding="utf-8") as fh:
        for line in fh:
            word = line.strip()
            if word and not word.startswith("#"):
                words.append(word)
    return words or list(DEFAULT_WORDS)


@dataclass
class SafetyVerdict:
    blocked: bool
    hits: list[str]
    cleaned_text: str


class SafetyFilter:
    def __init__(self, wordlist_path: str | None = None):
        words = load_wordlist(wordlist_path) if wordlist_path else list(DEFAULT_WORDS)
        self._ac = AhoCorasick(words)
        self.words = words

    def check(self, text: str) -> SafetyVerdict:
        hits = self._ac.find(text)
        if not hits:
            hits = [m.group(0) for m in _REJECTED_PATTERN.finditer(text)]
        cleaned = text
        for word in hits:
            cleaned = cleaned.replace(word, "***")
        return SafetyVerdict(blocked=bool(hits), hits=hits, cleaned_text=cleaned)

    check_input = check
    check_output = check

    def check_streaming(self, text: str) -> SafetyVerdict:
        verdict = self.check(text)
        if verdict.blocked:
            return verdict
        for word in self.words:
            if len(word) < 3:
                continue
            for size in range(2, len(word)):
                if text.endswith(word[:size]):
                    return SafetyVerdict(blocked=True, hits=[word[:size]], cleaned_text=text)
        return verdict

    def redacted(self, verdict: SafetyVerdict, emotion: str) -> str:
        if emotion == "HAPPY":
            return "诶诶，说话放干净点，我还想好好开车呢。"
        if emotion == "NERVOUS":
            return "你、你别这么说啦，我害怕……好好开行不行。"
        if emotion == "ANNOYED":
            return "啧，嘴巴放干净点，不然我自己下车走。"
        return "说话注意点，我不接这种话。"
