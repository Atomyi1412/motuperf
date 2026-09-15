param(
  [Parameter(Mandatory = $true)][string]$Version,
  [Parameter(Mandatory = $true)][string]$AssetsDirectory,
  [Parameter(Mandatory = $true)][string]$OutputPath,
  [string]$Repository = "Atomyi1412/motuperf"
)

$ErrorActionPreference = "Stop"
$normalized = $Version.TrimStart('v')
if ($normalized -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "Invalid release version: $Version" }
$windows = Get-ChildItem -LiteralPath $AssetsDirectory -File -Filter "MoTuPerf-Setup-v$normalized.exe" | Select-Object -First 1
$mac = Get-ChildItem -LiteralPath $AssetsDirectory -File -Filter "MoTuPerf-v$normalized-osx-arm64.dmg" | Select-Object -First 1
if (!$windows -or !$mac) { throw "Both Windows and macOS release assets are required." }

function Get-Asset([System.IO.FileInfo]$file, [string]$target) {
  $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  return [ordered]@{
    target = $target
    fileName = $file.Name
    url = "https://github.com/$Repository/releases/download/v$normalized/$($file.Name)"
    sha256 = $hash
  }
}

$manifest = [ordered]@{
  schema = 1
  version = $normalized
  mandatory = $false
  releaseNotesUrl = "https://github.com/$Repository/releases/tag/v$normalized"
  publishedAtUtc = [DateTime]::UtcNow.ToString("o")
  assets = [ordered]@{
    "win-x64" = Get-Asset $windows "win-x64"
    "osx-arm64" = Get-Asset $mac "osx-arm64"
  }
}
$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Update manifest: $OutputPath"
