$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dist = Join-Path $root "dist"
$build = Join-Path $root "build"
$portableName = "ios-perf-monitor-portable"
$portableDir = Join-Path $dist $portableName
$zipPath = Join-Path $dist ($portableName + ".zip")

function Invoke-Native {
  param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
  )

  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "$FilePath failed with exit code $LASTEXITCODE"
  }
}

Set-Location $root

Invoke-Native python -m unittest discover -s tests
Invoke-Native python -m compileall backend tests scripts
Invoke-Native node --check frontend\app.js

Get-Process -Name "ios-perf-monitor" -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -like (Join-Path $portableDir "*") } |
  Stop-Process -Force

if (Test-Path $portableDir) {
  Remove-Item -LiteralPath $portableDir -Recurse -Force
}
if (Test-Path $zipPath) {
  Remove-Item -LiteralPath $zipPath -Force
}

Invoke-Native python -m PyInstaller --clean --noconfirm packaging\ios_perf_monitor.spec

$readme = @"
iOS Performance Monitor Portable
================================

How to use:
1. Double-click ios-perf-monitor.exe.
2. The browser opens http://127.0.0.1:<port>.
3. Connect and trust the iPhone, refresh devices, choose the app and process, then start.

Notes:
- This package includes the Python runtime, web UI, backend server, tidevice, and py-ios-device dependencies.
- Screenshots and samples are written to the data directory next to the EXE.
- The target PC still needs Apple Mobile Device Support, and the iPhone must trust this computer.
- If port 8770 is busy, the app tries 8771-8789 automatically.
- Close the startup console window to exit the tool.
"@

Set-Content -LiteralPath (Join-Path $portableDir "README.txt") -Value $readme -Encoding ASCII

Compress-Archive -LiteralPath $portableDir -DestinationPath $zipPath -Force

Write-Host "Portable build complete:"
Write-Host $portableDir
Write-Host $zipPath
