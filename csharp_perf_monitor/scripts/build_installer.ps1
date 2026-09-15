param(
  [string]$OutputName = "MoTuPerf-Setup"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$repoRoot = Split-Path -Parent $projectRoot
$projectFile = Join-Path $projectRoot "CSharpIosPerfMonitor.csproj"
$dist = Join-Path $repoRoot "dist"
$installerScript = Join-Path $projectRoot "installer\MoTuPerf.nsi"
$processHelper = Join-Path $projectRoot "installer\StopInstalledProcesses.ps1"
$icon = Join-Path $projectRoot "assets\motu-icon.ico"

if ([string]::IsNullOrWhiteSpace($OutputName) -or $OutputName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
  throw "OutputName must contain only letters, digits, dot, underscore, or hyphen."
}
if (!(Test-Path $projectFile) -or !(Test-Path $installerScript) -or !(Test-Path $processHelper) -or !(Test-Path $icon)) {
  throw "Installer source files are incomplete."
}

[xml]$projectXml = Get-Content -LiteralPath $projectFile
$appVersion = ""
foreach ($propertyGroup in $projectXml.Project.PropertyGroup) {
  if ($propertyGroup.Version) {
    $appVersion = [string]$propertyGroup.Version
    break
  }
}
if ([string]::IsNullOrWhiteSpace($appVersion)) {
  throw "Project Version was not found in: $projectFile"
}

$makensisCommand = Get-Command makensis -ErrorAction SilentlyContinue
$makensisPath = if ($makensisCommand) { $makensisCommand.Source } else { "" }
if ([string]::IsNullOrWhiteSpace($makensisPath)) {
  $knownCompilers = @(
    (Join-Path ${env:LOCALAPPDATA} "NSIS-3.12-portable\nsis-3.12\makensis.exe"),
    (Join-Path ${env:ProgramFiles} "NSIS\makensis.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe")
  )
  $makensisPath = $knownCompilers | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($makensisPath)) {
  throw "NSIS compiler makensis.exe was not found. Install NSIS 3.12 or extract its portable ZIP under LOCALAPPDATA\NSIS-3.12-portable."
}

$versionParts = $appVersion.Split('.')
$appFileVersion = if ($versionParts.Count -eq 3) { "$appVersion.0" } else { $appVersion }
if ($appFileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
  throw "Project Version must contain three or four numeric components: $appVersion"
}

$payloadName = "$OutputName-payload-v$appVersion"
$payloadDir = Join-Path $dist $payloadName
$setupOutputDir = Join-Path $dist "installer"
$setupFile = Join-Path $setupOutputDir ($OutputName + "-v" + $appVersion + ".exe")

$payloadScript = Join-Path $projectRoot "scripts\build_portable.ps1"
& powershell -ExecutionPolicy Bypass -File $payloadScript -OutputName $payloadName -IncludeRuntime -SkipArchive
if ($LASTEXITCODE -ne 0) {
  throw "Installer payload build failed with exit code $LASTEXITCODE"
}
if (!(Test-Path (Join-Path $payloadDir "motuperf-package.json")) -or !(Test-Path (Join-Path $payloadDir "runtime\python\python.exe"))) {
  throw "Installer payload is missing its package manifest or bundled Python runtime."
}

New-Item -ItemType Directory -Path $setupOutputDir -Force | Out-Null
if (Test-Path $setupFile) {
  Remove-Item -LiteralPath $setupFile -Force
}

& $makensisPath `
  "/INPUTCHARSET" `
  "UTF8" `
  "/DAPP_VERSION=$appVersion" `
  "/DAPP_FILE_VERSION=$appFileVersion" `
  "/DSOURCE_DIR=$payloadDir" `
  "/DOUTPUT_FILE=$setupFile" `
  "/DSETUP_ICON=$icon" `
  "/DPROCESS_HELPER=$processHelper" `
  $installerScript
if ($LASTEXITCODE -ne 0) {
  throw "NSIS compilation failed with exit code $LASTEXITCODE"
}
if (!(Test-Path $setupFile)) {
  throw "Installer output was not created: $setupFile"
}

Write-Host "MoTuPerf installer build complete:"
Write-Host $setupFile
