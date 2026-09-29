from __future__ import annotations

import csv
import os
from dataclasses import dataclass, field



@dataclass
class Line:
    id: str
    text: str
    emotion: str
    intent_tags: list[str] = field(default_factory=list)
    priority: int = 50

    @property
    def voicelib_uri(self) -> str:
        return f"voicelib://{self.id}"


class Corpus:
    def __init__(self, path: str):
        self.path = path
        self.lines: list[Line] = []
        self._by_id: dict[str, Line] = {}
        self.load()

    def load(self) -> None:
        self.lines.clear()
        self._by_id.clear()
        if not os.path.exists(self.path):
            return
        with open(self.path, "r", encoding="utf-8-sig", newline="") as fh:
            reader = csv.DictReader(fh)
            for row in reader:
                if not row.get("id") or not row.get("text"):
                    continue
                line = Line(
                    id=row["id"].strip(),
                    text=row["text"].strip(),
                    emotion=(row.get("emotion") or "CALM").strip().upper(),
                    intent_tags=[t.strip() for t in (row.get("intent_tags") or "").split("|") if t.strip()],
                    priority=int(row.get("priority") or 50),
                )
                self.lines.append(line)
                self._by_id[line.id] = line

    def get(self, line_id: str) -> Line | None:
        return self._by_id.get(line_id)

    def candidates(
        self,
        intent: str | None = None,
        emotion: str | None = None,
        keyword: str | None = None,
    ) -> list[Line]:
        scored: list[tuple[int, Line]] = []
        for line in self.lines:
            score = 0
            if intent and intent in line.intent_tags:
                score += 50
            if emotion and line.emotion == emotion:
                score += 20
            if keyword and keyword in line.text:
                score += 30
            if score > 0:
                scored.append((score + line.priority, line))
        scored.sort(key=lambda pair: pair[0], reverse=True)
        return [line for _, line in scored]

    def pick(
        self,
        intent: str | None = None,
        emotion: str | None = None,
        keyword: str | None = None,
        exclude_ids: set[str] | None = None,
    ) -> Line | None:
        exclude_ids = exclude_ids or set()
        for line in self.candidates(intent, emotion, keyword):
            if line.id not in exclude_ids:
                return line
        return None

    def prompt_lines(self, limit: int = 40) -> list[str]:
        ordered = sorted(self.lines, key=lambda l: l.priority, reverse=True)
        return [f"{line.voicelib_uri} | {line.text}" for line in ordered[:limit]]

    def keywords(self) -> list[str]:
        return [line.text for line in self.lines]
