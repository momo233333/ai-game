# Build Copilot.Client (Release) and copy the managed DLLs Unity needs into
# unity/Assets/Plugins/Copilot.
#
# Why this step: the Unity side consumes the shared libraries
# Copilot.Client / Copilot.Core / Copilot.Contracts (IT方案1.md V2.0 §4).
# Unity does not compile them; it only references the DLLs -- that is what makes
# "one implementation, shared by both ends" actually true.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\build_unity_plugins.ps1 [-Clean]
#
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less files as
# ANSI, and non-ASCII bytes in a comment can swallow the following line.
param([switch]$Clean)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "src\Copilot.Client\Copilot.Client.csproj"
$bin  = Join-Path $repo "src\Copilot.Client\bin\Release\netstandard2.1"
$dest = Join-Path $repo "unity\Assets\Plugins\Copilot"

# The dotnet on PATH (C:\Program Files\dotnet) has no SDK; prefer the user-level install
$dotnet = "dotnet"
if ($env:LOCALAPPDATA) {
    $candidate = Join-Path $env:LOCALAPPDATA "dotnet\dotnet.exe"
    if (Test-Path $candidate) { $dotnet = $candidate }
}

Write-Host "[1/3] Building Copilot.Client (Release)"
& $dotnet build $proj -c Release --nologo | Out-Null

if (-not (Test-Path $bin)) { throw "Build output not found: $bin" }

if ($Clean -and (Test-Path $dest)) {
    Write-Host "[2/3] Cleaning $dest"
    Remove-Item $dest -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Unity ships these framework assemblies itself; duplicating them triggers
# "Multiple precompiled assemblies with the same name".
$skip = @("netstandard.dll", "mscorlib.dll", "System.dll", "System.Core.dll", "System.Runtime.dll")

Write-Host "[2/3] Copying managed DLLs -> unity\Assets\Plugins\Copilot"
$copied = @()
foreach ($f in Get-ChildItem $bin -Filter *.dll) {
    if ($skip -contains $f.Name) { continue }
    Copy-Item $f.FullName -Destination $dest -Force
    $copied += $f.Name
}

Write-Host "[3/3] Done, $($copied.Count) files:"
$copied | Sort-Object | ForEach-Object { Write-Host "    $_" }
Write-Host ""
Write-Host "Unity picks these up automatically as managed plugins." -ForegroundColor Green