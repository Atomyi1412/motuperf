param(
  [Parameter(Mandatory = $true)]
  [string]$OutputDirectory,
  [string]$BuildPython = "python",
  [string]$PythonVersion = "3.11.9",
  [string]$PythonSha256 = "009d6bf7e3b2ddca3d784fa09f90fe54336d5b60f0e0f305c37f400bf83cfd3b"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$repoRoot = Split-Path -Parent $projectRoot
$requirements = Join-Path $projectRoot "tools\requirements-metrics.txt"
$constraints = Join-Path $projectRoot "tools\requirements-metrics-lock.txt"
$cacheDir = Join-Path $repoRoot ".tmp\runtime-cache"
$archiveName = "python-$PythonVersion-embed-amd64.zip"
$archivePath = Join-Path $cacheDir $archiveName
$downloadUrl = "https://www.python.org/ftp/python/$PythonVersion/$archiveName"

if (!(Test-Path $requirements)) {
  throw "Runtime requirements not found: $requirements"
}
if (!(Test-Path $constraints)) {
  throw "Runtime dependency lock not found: $constraints"
}
if (!(Get-Command $BuildPython -ErrorAction SilentlyContinue)) {
  throw "Build Python was not found: $BuildPython"
}

$repoResolved = [System.IO.Path]::GetFullPath($repoRoot)
$outputResolved = [System.IO.Path]::GetFullPath($OutputDirectory)
if (!$outputResolved.StartsWith($repoResolved + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Refusing to replace a runtime directory outside the repository: $outputResolved"
}

New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
if (!(Test-Path $archivePath)) {
  Write-Host "Downloading official Python $PythonVersion embeddable runtime..."
  Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $archivePath
}

$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
if ($actualHash -ne $PythonSha256.ToLowerInvariant()) {
  throw "Python runtime SHA256 mismatch. Expected $PythonSha256, got $actualHash."
}

if (Test-Path $outputResolved) {
  Remove-Item -LiteralPath $outputResolved -Recurse -Force
}
New-Item -ItemType Directory -Path $outputResolved -Force | Out-Null
Expand-Archive -LiteralPath $archivePath -DestinationPath $outputResolved -Force

$pthFile = Get-ChildItem -LiteralPath $outputResolved -Filter "python*._pth" | Select-Object -First 1
if (!$pthFile) {
  throw "Embedded Python path configuration was not found in $outputResolved"
}
@(
  "python311.zip",
  ".",
  "Lib",
  "Lib\site-packages",
  "..\..\tools",
  "import site"
) | Set-Content -LiteralPath $pthFile.FullName -Encoding ASCII

$sitePackages = Join-Path $outputResolved "Lib\site-packages"
New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null
& $BuildPython -m pip install `
  --disable-pip-version-check `
  --no-input `
  --upgrade `
  --target $sitePackages `
  --requirement $requirements `
  --constraint $constraints
if ($LASTEXITCODE -ne 0) {
  throw "Python runtime dependency installation failed with exit code $LASTEXITCODE"
}

$licenseRoot = Join-Path $outputResolved "third-party-licenses"
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
$noticeLines = New-Object System.Collections.Generic.List[string]
$noticeLines.Add("MoTuPerf bundled Python dependencies")
$noticeLines.Add("====================================")
$noticeLines.Add("")
$noticeLines.Add("Python $PythonVersion - https://www.python.org/")
$noticeLines.Add("")
foreach ($distInfo in Get-ChildItem -LiteralPath $sitePackages -Directory -Filter "*.dist-info" | Sort-Object Name) {
  $metadataPath = Join-Path $distInfo.FullName "METADATA"
  $name = $distInfo.BaseName
  $version = ""
  $license = ""
  if (Test-Path $metadataPath) {
    foreach ($line in Get-Content -LiteralPath $metadataPath) {
      if (!$name -and $line.StartsWith("Name: ")) { $name = $line.Substring(6).Trim() }
      if (!$version -and $line.StartsWith("Version: ")) { $version = $line.Substring(9).Trim() }
      if (!$license -and $line.StartsWith("License-Expression: ")) { $license = $line.Substring(20).Trim() }
      if (!$license -and $line.StartsWith("License: ")) { $license = $line.Substring(9).Trim() }
    }
  }
  if ([string]::IsNullOrWhiteSpace($license)) { $license = "See package metadata/license files" }
  $noticeLines.Add("$name $version - $license")

  $distLicenseTarget = Join-Path $licenseRoot $distInfo.BaseName
  $licenseFiles = Get-ChildItem -LiteralPath $distInfo.FullName -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE)' }
  if ($licenseFiles.Count -gt 0) {
    New-Item -ItemType Directory -Path $distLicenseTarget -Force | Out-Null
    foreach ($licenseFile in $licenseFiles) {
      Copy-Item -LiteralPath $licenseFile.FullName -Destination $distLicenseTarget -Force
    }
  }
}
$noticeLines | Set-Content -LiteralPath (Join-Path $outputResolved "THIRD_PARTY_NOTICES.txt") -Encoding UTF8

$runtimeManifest = [ordered]@{
  schema = 1
  pythonVersion = $PythonVersion
  pythonSha256 = $actualHash
  requirementsSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $requirements).Hash.ToLowerInvariant()
  constraintsSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $constraints).Hash.ToLowerInvariant()
  generatedAtUtc = [DateTime]::UtcNow.ToString("o")
}
$runtimeManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputResolved "runtime-manifest.json") -Encoding UTF8

$runtimePython = Join-Path $outputResolved "python.exe"
$oldPath = $env:PATH
try {
  $env:PATH = $outputResolved
  & $runtimePython -I -c "import ios_device, pymobiledevice3, tidevice, android_perf_runner, pid_perf_runner, ios_pmd3_metrics, ios_process_list; from pymobiledevice3.services.afc import AfcHeader; from pymobiledevice3.remote.userspace_tunnel import UserspaceRsdTunnel; from pymobiledevice3.services.dvt.instruments.sysmontap import Sysmontap; print('motuperf-runtime-imports-ok')"
  if ($LASTEXITCODE -ne 0) {
    throw "Bundled Python import smoke failed with exit code $LASTEXITCODE"
  }
} finally {
  $env:PATH = $oldPath
}

Write-Host "Bundled Python runtime complete: $outputResolved"
