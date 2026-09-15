$ErrorActionPreference = "Stop"

$nativeRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$root = Split-Path -Parent $nativeRoot
$dist = Join-Path $root "dist"
$portableName = "native-ios-perf-monitor-portable"
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

Invoke-Native python -m compileall backend native_perf_monitor
Invoke-Native python native_perf_monitor\launcher.py --check

Get-Process -Name "native-ios-perf-monitor" -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -like (Join-Path $portableDir "*") } |
  Stop-Process -Force

if (Test-Path $portableDir) {
  Remove-Item -LiteralPath $portableDir -Recurse -Force
}
if (Test-Path $zipPath) {
  Remove-Item -LiteralPath $zipPath -Force
}

Invoke-Native python -m PyInstaller --clean --noconfirm native_perf_monitor\packaging\native_perf_monitor.spec

$readme = @"
Native iOS Performance Monitor Portable
======================================

How to use:
1. Double-click native-ios-perf-monitor.exe.
2. Connect and trust the iPhone.
3. Refresh devices, choose the app and process, then start capture.

Notes:
- This package uses a native PyQt UI instead of the browser UI.
- Screenshots and exports are written next to the EXE under data.
- Apple Mobile Device Support is still required on the target PC.
- Default screenshot interval is 3 seconds. Raising it reduces extra load.
"@

Set-Content -LiteralPath (Join-Path $portableDir "README.txt") -Value $readme -Encoding ASCII
Compress-Archive -LiteralPath $portableDir -DestinationPath $zipPath -Force

Write-Host "Native portable build complete:"
Write-Host $portableDir
Write-Host $zipPath
