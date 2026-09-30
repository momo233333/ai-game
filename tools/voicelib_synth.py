"""批量 TTS 合成 voice-corpus 台词 wav。

用 Windows 自带 System.Speech(中文语音)合成 voice-corpus/lines.csv 的每条台词，
再归一化为 24kHz 单声道 16-bit PCM 写入 voice-corpus/wavs/<id>.wav，
最后刷新 manifest.json（wav_path 自动填好）。

用法：
    python tools/voicelib_synth.py                 # 只补缺失的
    python tools/voicelib_synth.py --force         # 全部重合成
    python tools/voicelib_synth.py --only L001,L007
    python tools/voicelib_synth.py --no-manifest   # 不刷新 manifest.json
"""
from __future__ import annotations

import argparse
import csv
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
for path in (os.path.join(ROOT, "ai-service"), os.path.join(ROOT, "tools")):
    if path not in sys.path:
        sys.path.insert(0, path)

from core.audio import load_wav_pcm, write_wav  # noqa: E402

CORPUS_CSV = os.path.join(ROOT, "voice-corpus", "lines.csv")
WAV_DIR = os.path.join(ROOT, "voice-corpus", "wavs")
SAMPLE_RATE = 24000

# 纯 ASCII 脚本（PS5.1 无 BOM 安全），台词经 UTF-8 TSV 传入。
POWERSHELL = r"""
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Speech
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voice = $s.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture -like "zh*" } |
    Select-Object -First 1
if (-not $voice) { Write-Output "NO_ZH_VOICE"; exit 1 }
$s.SelectVoice($voice.VoiceInfo.Name)
$s.Rate = RATE_PLACEHOLDER
$items = @(Get-Content -LiteralPath 'TSV_PLACEHOLDER' -Encoding UTF8)
$fail = 0
foreach ($item in $items) {
    if (-not $item) { continue }
    $parts = $item -split "`t", 2
    if ($parts.Count -lt 2) { continue }
    $id = $parts[0].Trim()
    $text = $parts[1]
    try {
        $s.SetOutputToWaveFile((Join-Path 'RAW_PLACEHOLDER' ($id + '.wav')))
        $s.Speak($text)
    } catch {
        $fail = $fail + 1
        Write-Output ("FAIL " + $id + " :: " + $_.Exception.Message)
    }
}
$s.SetOutputToNull()
$s.Dispose()
Write-Output ("DONE total=" + $items.Count + " fail=" + $fail)
exit $fail
"""


def load_lines(csv_path: str) -> list[dict]:
    if not os.path.exists(csv_path):
        raise FileNotFoundError(csv_path)
    rows = []
    with open(csv_path, "r", encoding="utf-8-sig", newline="") as fh:
        for row in csv.DictReader(fh):
            line_id = (row.get("id") or "").strip()
            text = (row.get("text") or "").strip()
            if line_id and text:
                rows.append({"id": line_id, "text": text})
    if not rows:
        raise ValueError(f"空语料: {csv_path}")
    return rows


def synthesize(items: list[dict], raw_dir: str, rate: int) -> None:
    tsv_path = os.path.join(raw_dir, "items.tsv")
    with open(tsv_path, "w", encoding="utf-8", newline="") as fh:
        for item in items:
            fh.write(item["id"] + "\t" + item["text"] + "\n")
    script_path = os.path.join(raw_dir, "synth.ps1")
    script = (
        POWERSHELL.replace("TSV_PLACEHOLDER", tsv_path)
        .replace("RAW_PLACEHOLDER", raw_dir)
        .replace("RATE_PLACEHOLDER", str(rate))
    )
    with open(script_path, "w", encoding="ascii", newline="\r\n") as fh:
        fh.write(script)
    result = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script_path],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    sys.stdout.write(result.stdout)
    if result.stderr.strip():
        sys.stderr.write(result.stderr)
    if "NO_ZH_VOICE" in result.stdout:
        raise RuntimeError("系统没有 zh* 中文语音，请在 Windows 语音设置里安装中文 TTS 声音")
    if result.returncode != 0:
        raise RuntimeError("powershell synth failed (code=%s)" % result.returncode)


def normalize(rows: list[dict], raw_dir: str, out_dir: str, force: bool) -> tuple[int, int]:
    """把 raw_dir/<id>.wav 归一化为 24k 单声道写到 out_dir；返回 (新写入, 跳过)。"""
    written = skipped = 0
    missing = []
    for item in rows:
        line_id = item["id"]
        dst = os.path.join(out_dir, line_id + ".wav")
        if os.path.exists(dst) and not force:
            skipped += 1
            continue
        src = os.path.join(raw_dir, line_id + ".wav")
        if not os.path.exists(src):
            missing.append(line_id)
            continue
        pcm = load_wav_pcm(src, SAMPLE_RATE)
        if not pcm:
            missing.append(line_id)
            continue
        write_wav(dst, pcm, rate=SAMPLE_RATE)
        written += 1
    if missing:
        raise RuntimeError("合成失败/空音频: " + ",".join(missing))
    return written, skipped


def dir_stats(out_dir: str) -> tuple[int, int]:
    count = total = 0
    for name in os.listdir(out_dir):
        if name.endswith(".wav"):
            count += 1
            total += os.path.getsize(os.path.join(out_dir, name))
    return count, total


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="批量合成 voice-corpus 台词 wav（24k 单声道）")
    parser.add_argument("--csv", default=CORPUS_CSV)
    parser.add_argument("--out-dir", default=WAV_DIR)
    parser.add_argument("--only", default=None, help="只合成指定 id，逗号分隔，如 L001,L007")
    parser.add_argument("--force", action="store_true", help="已存在的也重新合成")
    parser.add_argument("--rate", type=int, default=-1, help="System.Speech 语速 -2..2（默认 -1）")
    parser.add_argument("--no-manifest", action="store_true", help="不刷新 manifest.json")
    args = parser.parse_args(argv)

    rows = load_lines(args.csv)
    if args.only:
        wanted = {s.strip().upper() for s in args.only.split(",") if s.strip()}
        unknown = wanted - {r["id"] for r in rows}
        if unknown:
            print("未知 id: " + ",".join(sorted(unknown)), file=sys.stderr)
            return 1
        rows = [r for r in rows if r["id"] in wanted]

    todo = [r for r in rows if args.force or not os.path.exists(os.path.join(args.out_dir, r["id"] + ".wav"))]
    os.makedirs(args.out_dir, exist_ok=True)
    written = skipped = 0
    if todo:
        raw_dir = tempfile.mkdtemp(prefix="voicelib_synth_")
        try:
            synthesize(todo, raw_dir, args.rate)
            written, skipped = normalize(rows, raw_dir, args.out_dir, args.force)
        finally:
            shutil.rmtree(raw_dir, ignore_errors=True)
    else:
        skipped = len(rows)

    if not args.no_manifest:
        import voicelib_gen

        voicelib_gen.main(["--csv", args.csv, "--wav-dir", args.out_dir])

    count, total = dir_stats(args.out_dir)
    print(
        "[synth] todo=%d new=%d skip=%d -> %s (existing %d wav, %.1f MB)"
        % (len(todo), written, skipped, args.out_dir, count, total / (1024 * 1024))
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
