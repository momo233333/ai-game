from __future__ import annotations

import os
import sys

SERVICE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SAMPLE_DIR = os.path.join(SERVICE_DIR, "tests", "samples")

CASES = {
    "left_turn.wav": "向左转弯",
    "obstacle_stop.wav": "前面有障碍物，赶紧停车",
    "speed_100.wav": "现在一百码了，靠边停车",
    "overtake.wav": "找个机会超车",
    "greet.wav": "你好呀，妹妹",
    "swear.wav": "你真是个白痴",
}

POWERSHELL = r"""
Add-Type -AssemblyName System.Speech
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voice = $s.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture -like "zh*" } | Select-Object -First 1
if (-not $voice) { Write-Error "no zh-CN voice"; exit 1 }
$s.SelectVoice($voice.VoiceInfo.Name)
$s.Rate = -1
foreach ($kv in $cases.GetEnumerator()) {
    $s.SetOutputToWaveFile((Join-Path $dir $kv.Key))
    $s.Speak($kv.Value)
}
$s.Dispose()
"""


def main() -> int:
    import subprocess

    os.makedirs(SAMPLE_DIR, exist_ok=True)
    case_lines = "\n".join(f"    '{k}' = '{v}';" for k, v in CASES.items())
    script = "$cases = @{\n" + case_lines + "\n}\n" + f'$dir = "{SAMPLE_DIR}"\n' + POWERSHELL
    result = subprocess.run(
        ["powershell", "-NoProfile", "-Command", script],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if result.returncode != 0:
        print(result.stderr, file=sys.stderr)
        return result.returncode
    for name in CASES:
        path = os.path.join(SAMPLE_DIR, name)
        size = os.path.getsize(path) if os.path.exists(path) else 0
        print(f"{name}: {size // 1024} KB")
    print(f"generated -> {SAMPLE_DIR}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
