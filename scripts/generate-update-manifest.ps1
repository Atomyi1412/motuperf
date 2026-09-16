param(
  [Parameter(Mandatory = $true)][string]$Version,
  [Parameter(Mandatory = $true)][string]$AssetsDirectory,
  [Parameter(Mandatory = $true)][string]$OutputPath,
  [string]$Repository = "Atomyi1412/motuperf",
  [string]$ChangelogPath,
  [string]$ReleaseNotesPath
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($ChangelogPath)) {
  $ChangelogPath = Join-Path $PSScriptRoot '../CHANGELOG.md'
}
$normalized = $Version.TrimStart('v')
if ($normalized -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "Invalid release version: $Version" }
$windows = Get-ChildItem -LiteralPath $AssetsDirectory -File -Filter "MoTuPerf-Setup-v$normalized.exe" | Select-Object -First 1
$mac = Get-ChildItem -LiteralPath $AssetsDirectory -File -Filter "MoTuPerf-v$normalized-osx-arm64.dmg" | Select-Object -First 1
if (!$windows -or !$mac) { throw "Both Windows and macOS release assets are required." }

$changelog = Get-Content -LiteralPath $ChangelogPath -Raw -Encoding UTF8
$sections = [regex]::Matches($changelog, '(?ms)^## v(?<version>\d+\.\d+\.\d+)(?: - [^\r\n]+)?\r?\n(?<body>.*?)(?=^## |\z)')
$section = @($sections | Where-Object { $_.Groups['version'].Value -eq $normalized })
if ($section.Count -ne 1) { throw "Release must have exactly one matching CHANGELOG section: $normalized" }
$releaseNotes = @([regex]::Matches($section[0].Groups['body'].Value, '(?m)^- (.+)$') |
  ForEach-Object { $_.Groups[1].Value.Trim().Replace('`', '') })
if ($releaseNotes.Count -eq 0 -or $releaseNotes.Count -gt 24 -or @($releaseNotes | Where-Object { $_.Length -gt 1000 }).Count -gt 0) {
  throw 'Release notes require 1-24 bullets, each no longer than 1000 characters.'
}

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
  releaseNotes = $releaseNotes
  publishedAtUtc = [DateTime]::UtcNow.ToString("o")
  assets = [ordered]@{
    "win-x64" = Get-Asset $windows "win-x64"
    "osx-arm64" = Get-Asset $mac "osx-arm64"
  }
}
$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
if ($ReleaseNotesPath) {
  $section[0].Groups['body'].Value.Trim() | Set-Content -LiteralPath $ReleaseNotesPath -Encoding UTF8
}
Write-Host "Update manifest: $OutputPath"
