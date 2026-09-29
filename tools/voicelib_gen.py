from __future__ import annotations

import argparse
import csv
import json
import os
import sys
from datetime import datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def build_manifest(csv_path: str, wav_dir: str | None, output: str) -> dict:
    items = []
    with open(csv_path, "r", encoding="utf-8-sig", newline="") as fh:
        for row in csv.DictReader(fh):
            line_id = (row.get("id") or "").strip()
            text = (row.get("text") or "").strip()
            if not line_id or not text:
                continue
            wav_path = ""
            if wav_dir:
                candidate = os.path.join(wav_dir, f"{line_id}.wav")
                if os.path.exists(candidate):
                    wav_path = os.path.relpath(candidate, os.path.dirname(os.path.abspath(output)))
            items.append(
                {
                    "id": line_id,
                    "text": text,
                    "emotion": (row.get("emotion") or "CALM").strip().upper(),
                    "intent_tags": [t.strip() for t in (row.get("intent_tags") or "").split("|") if t.strip()],
                    "priority": int(row.get("priority") or 50),
                    "wav_path": wav_path,
                }
            )
    manifest = {
        "version": 1,
        "generated_at": datetime.now().isoformat(timespec="seconds"),
        "source_csv": os.path.relpath(csv_path, ROOT),
        "sample_rate": 24000,
        "items": items,
    }
    os.makedirs(os.path.dirname(os.path.abspath(output)) or ".", exist_ok=True)
    with open(output, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, ensure_ascii=False, indent=2)
    return manifest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="从台词语料生成 TTS 语音库 manifest")
    parser.add_argument("--csv", default=os.path.join(ROOT, "voice-corpus", "lines.csv"))
    parser.add_argument("--wav-dir", default=None, help="预生成 wav 所在目录（可选）")
    parser.add_argument("--out", default=os.path.join(ROOT, "voice-corpus", "manifest.json"))
    args = parser.parse_args(argv)
    if not os.path.exists(args.csv):
        print(f"csv not found: {args.csv}", file=sys.stderr)
        return 1
    manifest = build_manifest(args.csv, args.wav_dir, args.out)
    hit = sum(1 for item in manifest["items"] if item["wav_path"])
    print(f"[voicelib] {len(manifest['items'])} lines -> {args.out} (wav ready: {hit})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
