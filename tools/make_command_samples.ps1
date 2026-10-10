#requires -Version 5.1
<#
.SYNOPSIS
  Synthesize player-command speech samples (16 kHz mono WAV) for manual ASR testing.

.DESCRIPTION
  Reads tools\command_samples.txt (UTF-8) and writes tests\samples\<name>.wav via Windows SAPI.
  ASCII-only source on purpose: Windows PowerShell 5.1 reads BOM-less files as ANSI and would mangle
  any non-ASCII literal in this script. All Chinese text lives in command_samples.txt.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\make_command_samples.ps1
  powershell -ExecutionPolicy Bypass -File tools\make_command_samples.ps1 -Force
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$listPath = Join-Path $repoRoot 'tools\command_samples.txt'
$outDir = Join-Path $repoRoot 'tests\samples'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if (-not (Test-Path $listPath)) { throw "missing $listPath" }

Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer

# prefer an installed zh-CN voice
$zh = $synth.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -eq 'zh-CN' } | Select-Object -First 1
if ($zh) {
    $synth.SelectVoice($zh.VoiceInfo.Name)
    Write-Host ("voice: {0}" -f $zh.VoiceInfo.Name) -ForegroundColor Cyan
} else {
    Write-Warning 'no zh-CN voice installed; output may be unusable for Chinese commands'
}

$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
    16000,
    [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
    [System.Speech.AudioFormat.AudioChannel]::Mono)

$made = 0
foreach ($line in (Get-Content $listPath -Encoding UTF8)) {
    $t = $line.Trim()
    if ($t.Length -eq 0 -or $t.StartsWith('#')) { continue }

    $parts = $t.Split('|', 2)
    if ($parts.Count -lt 2) { Write-Warning "bad line: $t"; continue }

    $name = $parts[0].Trim()
    $text = $parts[1].Trim()
    $path = Join-Path $outDir "$name.wav"

    if ((Test-Path $path) -and -not $Force) {
        Write-Host "skip $name.wav (exists)" -ForegroundColor DarkGray
        continue
    }

    $synth.SetOutputToWaveFile($path, $format)
    $synth.Speak($text)
    $synth.SetOutputToNull()
    $made++
    Write-Host ("ok   $name.wav  <- {0}" -f $text) -ForegroundColor Green
}

$synth.Dispose()
Write-Host ''
Write-Host "wrote $made file(s) to tests\samples" -ForegroundColor Cyan
Write-Host 'try: dotnet run --project src\Copilot.AIService -- --wav tests\samples\left_turn.wav' -ForegroundColor Yellow