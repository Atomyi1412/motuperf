param(
  [string]$OutputName = "MoTuPerf-Setup",
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$packagingRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent (Split-Path -Parent $packagingRoot)
$repoRoot = Split-Path -Parent $projectRoot
$projectFile = Join-Path $projectRoot "src\MoTuPerf.Desktop\MoTuPerf.Desktop.csproj"
$dist = Join-Path $projectRoot "dist"
$installerScript = Join-Path $packagingRoot "MoTuPerf.CrossPlatform.nsi"
$processHelper = Join-Path $packagingRoot "StopInstalledProcesses.ps1"
$icon = Join-Path $repoRoot "csharp_perf_monitor\assets\motu-icon.ico"
$shortcutIcon = Join-Path $repoRoot "csharp_perf_monitor\assets\motu-shortcut-icon.ico"
$runtimeScript = Join-Path $repoRoot "csharp_perf_monitor\scripts\build_runtime.ps1"
$requirements = Join-Path $repoRoot "csharp_perf_monitor\tools\requirements-metrics.txt"
$constraints = Join-Path $repoRoot "csharp_perf_monitor\tools\requirements-metrics-lock.txt"

if ($OutputName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
  throw "OutputName must contain only letters, digits, dot, underscore, or hyphen."
}
foreach ($required in @($projectFile, $installerScript, $processHelper, $icon, $shortcutIcon, $runtimeScript, $requirements, $constraints)) {
  if (!(Test-Path -LiteralPath $required)) { throw "Installer source file is missing: $required" }
}

[xml]$projectXml = Get-Content -LiteralPath $projectFile
$appVersion = @($projectXml.Project.PropertyGroup | ForEach-Object { if ($_.Version) { [string]$_.Version } } | Where-Object { $_ }) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($appVersion) -or $appVersion -notmatch '^\d+\.\d+\.\d+$') {
  throw "Cross-platform project version must be three numeric components: $appVersion"
}
$appFileVersion = "$appVersion.0"

$makensis = Get-Command makensis -ErrorAction SilentlyContinue
$makensisPath = if ($makensis) { $makensis.Source } else { "" }
if ([string]::IsNullOrWhiteSpace($makensisPath)) {
  $knownCompilers = @(
    (Join-Path $env:LOCALAPPDATA "NSIS-3.12-portable\nsis-3.12\makensis.exe"),
    (Join-Path $env:ProgramFiles "NSIS\makensis.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe")
  )
  $makensisPath = $knownCompilers | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($makensisPath)) {
  throw "NSIS compiler makensis.exe was not found."
}

$payloadName = "$OutputName-payload-v$appVersion"
$payloadDir = Join-Path $dist $payloadName
$setupDir = Join-Path $dist "installer"
$setupFile = Join-Path $setupDir "$OutputName-v$appVersion.exe"
$publishDir = Join-Path $payloadDir "_publish"

if (Test-Path -LiteralPath $payloadDir) { Remove-Item -LiteralPath $payloadDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

dotnet restore $projectRoot\MoTuPerf.CrossPlatform.sln --locked-mode -p:Platform="Any CPU"
if ($LASTEXITCODE -ne 0) { throw "NuGet locked restore failed." }
dotnet publish $projectFile -c $Configuration -r win-x64 --self-contained true `
  -p:Platform="Any CPU" `
  -p:UseAppHost=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
  -o $publishDir --no-restore
if ($LASTEXITCODE -ne 0) { throw "Windows self-contained publish failed." }
Get-ChildItem -LiteralPath $publishDir -Force | Move-Item -Destination $payloadDir -Force
Remove-Item -LiteralPath $publishDir -Recurse -Force
Get-ChildItem -LiteralPath $payloadDir -Recurse -File -Filter "*.pdb" | Remove-Item -Force

$toolsTarget = Join-Path $payloadDir "tools"
New-Item -ItemType Directory -Path $toolsTarget -Force | Out-Null
Get-ChildItem (Join-Path $repoRoot "csharp_perf_monitor\tools") -File | Where-Object { $_.Extension -in @('.py', '.txt') } | Copy-Item -Destination $toolsTarget -Force

& powershell -ExecutionPolicy Bypass -File $runtimeScript -OutputDirectory (Join-Path $payloadDir "runtime\python")
if ($LASTEXITCODE -ne 0) { throw "Bundled Python runtime build failed." }

$adbCommand = Get-Command adb -ErrorAction SilentlyContinue
if (!$adbCommand) { throw "ADB was not found. Install Android platform-tools before packaging." }
$androidRuntime = Join-Path $payloadDir "runtime\android"
New-Item -ItemType Directory -Path $androidRuntime -Force | Out-Null
$adbSource = Split-Path -Parent $adbCommand.Source
foreach ($file in @("adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "NOTICE.txt")) {
  $source = Join-Path $adbSource $file
  if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $androidRuntime $file) -Force }
  elseif ($file -ne "NOTICE.txt") { throw "Required Android platform-tools file not found: $source" }
}
Copy-Item -LiteralPath $shortcutIcon -Destination (Join-Path $payloadDir "motu-shortcut-icon.ico") -Force
[ordered]@{ schema = 1; appVersion = $appVersion; target = "win-x64"; bundledPython = "runtime/python/python.exe"; bundledAdb = "runtime/android/adb.exe"; generatedAtUtc = [DateTime]::UtcNow.ToString("o") } |
  ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payloadDir "motuperf-package.json") -Encoding UTF8

New-Item -ItemType Directory -Path $setupDir -Force | Out-Null
if (Test-Path -LiteralPath $setupFile) { Remove-Item -LiteralPath $setupFile -Force }
& $makensisPath "/INPUTCHARSET" "UTF8" "/DAPP_VERSION=$appVersion" "/DAPP_FILE_VERSION=$appFileVersion" "/DSOURCE_DIR=$payloadDir" "/DOUTPUT_FILE=$setupFile" "/DSETUP_ICON=$icon" "/DPROCESS_HELPER=$processHelper" $installerScript
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $setupFile)) { throw "NSIS installer build failed." }

$hash = Get-FileHash -LiteralPath $setupFile -Algorithm SHA256
Set-Content -LiteralPath "$setupFile.sha256" -Value "$($hash.Hash)  $([IO.Path]::GetFileName($setupFile))" -Encoding ASCII
Write-Host "Windows installer: $setupFile"
Write-Host "SHA256: $($hash.Hash)"
