#requires -Version 5.1
<#
.SYNOPSIS
  Download the model weights required by Copilot.AIService into .\models (see IT方案1.md V2.0 §2.4).

.DESCRIPTION
  Weights are gitignored; this script makes them reproducible on a new machine.
  ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less files as ANSI and would mangle non-ASCII text.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\fetch_models.ps1
  powershell -ExecutionPolicy Bypass -File tools\fetch_models.ps1 -Only asr
  powershell -ExecutionPolicy Bypass -File tools\fetch_models.ps1 -Force
#>
param(
    [ValidateSet('all', 'llm', 'asr', 'vad')]
    [string]$Only = 'all',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# repo root = parent of this script's folder
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$modelsDir = Join-Path $repoRoot 'models'
New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Get-File($url, $dest, [int64]$minBytes) {
    $name = Split-Path -Leaf $dest
    if ((Test-Path $dest) -and -not $Force) {
        $len = (Get-Item $dest).Length
        if ($len -ge $minBytes) {
            Write-Host ("    [skip] {0} already present ({1:N1} MB)" -f $name, ($len / 1MB)) -ForegroundColor DarkGray
            return
        }
    }

    Write-Step "downloading $name"
    $part = "$dest.part"
    if (Test-Path $part) { Remove-Item $part -Force }

    # curl.exe ships with Windows 10+ and handles multi-GB files far better than Invoke-WebRequest
    & curl.exe -L --fail --retry 3 --retry-delay 5 -o $part $url
    if ($LASTEXITCODE -ne 0) { throw "curl failed for $url (exit $LASTEXITCODE)" }

    $len = (Get-Item $part).Length
    if ($len -lt $minBytes) { throw "$name is only $len bytes, expected >= $minBytes" }

    Move-Item -Force $part $dest
    Write-Host ("    [ok]   {0} ({1:N1} MB)" -f $name, ($len / 1MB)) -ForegroundColor Green
}

# ---------------------------------------------------------------- VAD
function Fetch-Vad {
    Write-Step 'VAD: silero_vad.onnx'
    Get-File 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx' `
        (Join-Path $modelsDir 'silero_vad.onnx') 400KB
}

# ---------------------------------------------------------------- ASR
function Fetch-Asr {
    $dirName = 'sherpa-onnx-streaming-paraformer-bilingual-zh-en'
    $target = Join-Path $modelsDir $dirName
    $tokens = Join-Path $target 'tokens.txt'

    if ((Test-Path $tokens) -and -not $Force) {
        Write-Host "    [skip] $dirName already present" -ForegroundColor DarkGray
        return
    }

    # ModelScope mirror (~226 MB, int8) is used instead of the 1 GB GitHub release tarball:
    # github.com release downloads are throttled to ~0.01 MB/s from here, ModelScope runs ~11 MB/s.
    Write-Step "ASR: $dirName (int8, ~226 MB)"
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    $repo = 'https://modelscope.cn/models/pengzhendong/sherpa-onnx-streaming-paraformer-bilingual-zh-en/resolve/master'
    Get-File "$repo/encoder.int8.onnx" (Join-Path $target 'encoder.int8.onnx') 100MB
    Get-File "$repo/decoder.int8.onnx" (Join-Path $target 'decoder.int8.onnx') 40MB
    Get-File "$repo/tokens.txt" (Join-Path $target 'tokens.txt') 10KB

    Write-Host "    [ok]   extracted to models\$dirName" -ForegroundColor Green
}

# ---------------------------------------------------------------- LLM
function Fetch-Llm {
    $repo = 'https://modelscope.cn/models/Qwen/Qwen2.5-7B-Instruct-GGUF/resolve/master'
    $parts = @(
        'qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf',
        'qwen2.5-7b-instruct-q4_k_m-00002-of-00002.gguf'
    )

    Write-Step 'LLM: Qwen2.5-7B-Instruct Q4_K_M (split GGUF, ~4.4 GB)'
    foreach ($p in $parts) {
        Get-File "$repo/$p" (Join-Path $modelsDir $p) 100MB
    }

    Write-Host '    note: point llm.modelPath at part 1; llama.cpp loads the sibling shard automatically.' -ForegroundColor DarkGray
}

switch ($Only) {
    'all' { Fetch-Vad; Fetch-Asr; Fetch-Llm }
    'vad' { Fetch-Vad }
    'asr' { Fetch-Vad; Fetch-Asr }
    'llm' { Fetch-Llm }
}

Write-Host ''
Write-Host 'done. contents of .\models:' -ForegroundColor Cyan
Get-ChildItem $modelsDir | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize

Write-Host 'next: dotnet run --project src\Copilot.AIService -- --health' -ForegroundColor Yellow