# Run Unity in batch mode with -executeMethod, robust against the crash:
#   "Fatal Error! It looks like another Unity instance is running with this project open."
#
# Why this script exists: Start-Process -Wait does NOT wait for the real editor
# process -- Unity re-launches itself, so the launcher exits early. A loop over
# several methods therefore starts a second editor while the first still holds
# unity/Temp/UnityLockfile, and the second one self-crashes. This script kills
# leftovers, waits until NO Unity process remains, and only then runs the next.
#
# Also: if you changed anything under src/ (Copilot.Core / Contracts / Client),
# run tools\build_unity_plugins.ps1 FIRST -- Unity uses the prebuilt DLLs in
# unity/Assets/Plugins/Copilot, and a stale DLL shows up as "error CS0234:
# namespace does not exist".
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\unity_batch.ps1 -Method Copilot.EditorTools.SceneBuilder.BuildAndExit
#   powershell -ExecutionPolicy Bypass -File tools\unity_batch.ps1 -All
#
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 reads BOM-less files
# as ANSI and non-ASCII bytes in comments can swallow the following line).
param(
    [string]$Method,
    [switch]$All,
    [string]$LogDir = "logs",
    [int]$TimeoutMinutes = 8
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repo "unity"

# The Unity editor is NOT under Program Files on this machine
$unity = "dotnet"   # placeholder, replaced below if found
$unity = $null
if ($env:LOCALAPPDATA) {
    $candidate = Get-ChildItem (Join-Path $env:LOCALAPPDATA "Unity\Hub\Editor") -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($candidate) {
        $exe = Join-Path $candidate.FullName "Editor\Unity.exe"
        if (Test-Path $exe) { $unity = $exe }
    }
}
if (-not $unity) { throw "Unity.exe not found under %LOCALAPPDATA%\Unity\Hub\Editor" }

$defaultMethods = @(
    "Copilot.EditorTools.SceneBuilder.BuildAndExit",
    "Copilot.EditorTools.SceneBuilder.ValidateAndExit",
    "Copilot.EditorTools.SceneBuilder.CaptureAndExit",
    "Copilot.EditorTools.SmokeTest.Run"
)

$methods = if ($All) { $defaultMethods }
           elseif ($Method) { @($Method) }
           else { throw "pass -Method <type.method> or -All" }

function Wait-NoUnity {
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline -and (Get-Process Unity -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 4
    }
    if (Get-Process Unity -ErrorAction SilentlyContinue) {
        Write-Host "  !! still running after $TimeoutMinutes min" -ForegroundColor Yellow
    }
}

Write-Host "Unity  : $unity"
Write-Host "Project: $projectPath"
Write-Host ""

foreach ($m in $methods) {
    Stop-Process -Name Unity -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    $name = ($m -split '\.')[-1]
    $log = Join-Path $repo (Join-Path $LogDir "unity_$name.log")
    if (Test-Path $log) { Remove-Item $log -Force }

    Start-Process -FilePath $unity `
        -ArgumentList "-batchmode", "-projectPath", $projectPath, "-executeMethod", $m, "-logFile", $log | Out-Null

    Wait-NoUnity

    Write-Host "=== $m ==="
    $hits = Select-String -Path $log -Pattern "error CS|Fatal Error|Unhandled Exception" -ErrorAction SilentlyContinue
    if ($hits) {
        $hits | Select-Object -First 10 | ForEach-Object { Write-Host "  $($_.Line)" -ForegroundColor Red }
    } else {
        Write-Host "  ok (no compile/crash errors)" -ForegroundColor Green
    }
    Write-Host "  log: $log"
}