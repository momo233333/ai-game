from __future__ import annotations

import json
import os
from dataclasses import dataclass

from ..audio import load_wav_pcm


@dataclass
class VoiceEntry:
    id: str
    text: str
    emotion: str
    intent_tags: list[str]
    wav_path: str = ""

    @property
    def voicelib_uri(self) -> str:
        return f"voicelib://{self.id}"


class VoiceLib:
    def __init__(self, manifest_path: str, sample_rate: int = 24000):
        self.manifest_path = manifest_path
        self.sample_rate = sample_rate
        self.base_dir = os.path.dirname(os.path.abspath(manifest_path))
        self.entries: list[VoiceEntry] = []
        self._by_text: dict[str, VoiceEntry] = {}
        self.loaded = False
        self.load()

    def load(self) -> None:
        self.entries.clear()
        self._by_text.clear()
        self.loaded = False
        if not os.path.exists(self.manifest_path):
            return
        with open(self.manifest_path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
        for item in data.get("items", []):
            entry = VoiceEntry(
                id=item["id"],
                text=item["text"],
                emotion=str(item.get("emotion", "CALM")).upper(),
                intent_tags=list(item.get("intent_tags", [])),
                wav_path=item.get("wav_path", ""),
            )
            self.entries.append(entry)
            self._by_text.setdefault(entry.text, entry)
        self.loaded = bool(self.entries)

    def resolve_uri(self, uri: str | None) -> VoiceEntry | None:
        if not uri or not uri.startswith("voicelib://"):
            return None
        line_id = uri[len("voicelib://") :]
        for entry in self.entries:
            if entry.id == line_id:
                return entry
        return None

    def lookup(self, text: str, emotion: str | None = None) -> VoiceEntry | None:
        exact = self._by_text.get(text)
        if exact and (emotion is None or exact.emotion == emotion.upper()):
            return exact
        if exact:
            return exact
        for entry in self.entries:
            if text and (text in entry.text or entry.text in text):
                return entry
        if emotion:
            for entry in self.entries:
                if entry.emotion == emotion.upper():
                    return entry
        return None

    def wav_bytes(self, entry: VoiceEntry) -> bytes | None:
        """原始 wav 文件字节（含 RIFF 头），仅调试用。"""
        if not entry.wav_path:
            return None
        path = entry.wav_path if os.path.isabs(entry.wav_path) else os.path.join(self.base_dir, entry.wav_path)
        if not os.path.exists(path):
            return None
        with open(path, "rb") as fh:
            return fh.read()

    def pcm16(self, entry: VoiceEntry) -> bytes | None:
        """传输用原始 PCM16（无 RIFF 头），单声道，重采样到 manifest sample_rate。"""
        if not entry.wav_path:
            return None
        path = entry.wav_path if os.path.isabs(entry.wav_path) else os.path.join(self.base_dir, entry.wav_path)
        if not os.path.exists(path):
            return None
        try:
            return load_wav_pcm(path, self.sample_rate)
        except (OSError, ValueError, EOFError):
            return None
