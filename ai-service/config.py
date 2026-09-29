from __future__ import annotations

import copy
import os
from typing import Any

try:
    import yaml
except ImportError:
    yaml = None

DEFAULTS: dict[str, Any] = {
    "server": {"host": "127.0.0.1", "port": 50051, "transport": "auto", "version": "0.1.0-skeleton"},
    "llm": {
        "backend": "auto",
        "model_path": "models/qwen2.5-7b-instruct-q4_k_m.gguf",
        "n_ctx": 4096,
        "n_gpu_layers": 99,
        "openai_base_url": "http://127.0.0.1:8080/v1",
        "openai_model": "qwen2.5-7b-instruct",
        "max_reply_chars": 40,
        "stream_chunk_chars": 4,
    },
    "vad": {"backend": "auto", "threshold": 0.5, "min_silence_ms": 600, "energy_threshold": 0.02},
    "asr": {
        "backend": "auto",
        "model": "paraformer-zh-streaming",
        "vosk_model_path": "models/vosk-model-small-cn-0.22",
        "sample_rate": 16000,
        "low_confidence": 0.6,
    },
    "mic": {"device": None, "block_ms": 100},
    "tts": {
        "voicelib_enabled": True,
        "realtime_enabled": True,
        "voicelib_path": "../voice-corpus/manifest.json",
        "sample_rate": 24000,
    },
    "memory": {
        "db_path": "memory.db",
        "save_slot": "slot0",
        "recent_turns": 8,
        "old_debt_limit": 5,
        "old_debt_threshold": -5,
        "summarize_every": 5,
    },
    "safety": {"wordlist_path": "data/sensitive_words.txt"},
    "dialogue": {"corpus_path": "../voice-corpus/lines.csv"},
}


def _deep_merge(base: dict, override: dict) -> dict:
    out = copy.deepcopy(base)
    for key, value in override.items():
        if isinstance(value, dict) and isinstance(out.get(key), dict):
            out[key] = _deep_merge(out[key], value)
        else:
            out[key] = value
    return out


def _mini_yaml(text: str) -> dict:
    root: dict[str, Any] = {}
    section: dict[str, Any] = root
    for raw in text.splitlines():
        line = raw.split("#", 1)[0].rstrip()
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip())
        if ":" not in line:
            continue
        key, _, value = line.strip().partition(":")
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if not value:
            node: dict[str, Any] = {}
            if indent == 0:
                root[key] = node
                section = node
            else:
                root[key] = node
                section = node
            continue
        if value.lower() in ("true", "false"):
            parsed: Any = value.lower() == "true"
        else:
            try:
                parsed = int(value)
            except ValueError:
                try:
                    parsed = float(value)
                except ValueError:
                    parsed = value
        if indent == 0:
            root[key] = parsed
        else:
            section[key] = parsed
    return root


def load_config(path: str | None = None) -> dict:
    if path is None:
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "config.yaml")
    if not os.path.exists(path):
        return copy.deepcopy(DEFAULTS)
    with open(path, "r", encoding="utf-8") as fh:
        text = fh.read()
    data = yaml.safe_load(text) if yaml is not None else _mini_yaml(text)
    if not isinstance(data, dict):
        raise ValueError(f"invalid config file: {path}")
    return _deep_merge(DEFAULTS, data)


def resolve_path(base: str, maybe_relative: str) -> str:
    if os.path.isabs(maybe_relative):
        return maybe_relative
    if os.path.isdir(base):
        anchor = os.path.abspath(base)
    else:
        anchor = os.path.dirname(os.path.abspath(base))
    return os.path.normpath(os.path.join(anchor, maybe_relative))
