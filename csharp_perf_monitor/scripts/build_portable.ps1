param(
  [string]$OutputName = "csharp-ios-perf-monitor-portable",
  [switch]$IncludeRuntime,
  [switch]$SkipArchive
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$repoRoot = Split-Path -Parent $projectRoot
$projectFile = Join-Path $projectRoot "CSharpIosPerfMonitor.csproj"
$dist = Join-Path $repoRoot "dist"
if ([string]::IsNullOrWhiteSpace($OutputName) -or $OutputName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
  throw "OutputName must contain only letters, digits, dot, underscore, or hyphen."
}

$portableDir = Join-Path $dist $OutputName
$zipPath = Join-Path $dist ($OutputName + ".zip")
$dotnet = "dotnet"

if (Test-Path "C:\Program Files\dotnet\dotnet.exe") {
  $dotnet = "C:\Program Files\dotnet\dotnet.exe"
}

if (!(Test-Path $projectFile)) {
  throw "Project file not found: $projectFile"
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

if (!(Get-Command $dotnet -ErrorAction SilentlyContinue)) {
  throw ".NET SDK was not found. Install Microsoft .NET SDK 8.0 or newer."
}

$distResolved = [System.IO.Path]::GetFullPath($dist)
$portableResolved = [System.IO.Path]::GetFullPath($portableDir)
if (!$portableResolved.StartsWith($distResolved, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Refusing to delete outside dist: $portableResolved"
}

if (!(Test-Path $dist)) {
  New-Item -ItemType Directory -Path $dist | Out-Null
}
if (Test-Path $portableDir) {
  Get-ChildItem -LiteralPath $portableDir -Force | Remove-Item -Recurse -Force
}
if (Test-Path $zipPath) {
  Remove-Item -LiteralPath $zipPath -Force
}
if (!(Test-Path $portableDir)) {
  New-Item -ItemType Directory -Path $portableDir | Out-Null
}

& $dotnet publish $projectFile `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output $portableDir `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:EnableCompressionInSingleFile=true `
  /p:DebugType=None `
  /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
  throw "C# publish failed with exit code $LASTEXITCODE"
}

$toolsSource = Join-Path $projectRoot "tools"
$toolsTarget = Join-Path $portableDir "tools"
if (Test-Path $toolsSource) {
  New-Item -ItemType Directory -Path $toolsTarget -Force | Out-Null
  Copy-Item -LiteralPath (Join-Path $toolsSource "pid_perf_runner.py") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "android_perf_runner.py") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "display_metrics.py") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "ios_pmd3_metrics.py") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "ios_process_list.py") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "ios_device_info.py") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "ios_sysmon_schema_cache.py") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "pmd3_windows_compat.py") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "temperature_metrics.py") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "ios_target_rebinding.py") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "requirements-metrics.txt") -Destination $toolsTarget -Force
  Copy-Item -LiteralPath (Join-Path $toolsSource "requirements-metrics-lock.txt") -Destination $toolsTarget -Force
}

$shortcutIconSource = Join-Path $projectRoot "assets\motu-shortcut-icon.ico"
$shortcutIconTarget = Join-Path $portableDir "motu-shortcut-icon.ico"
if (!(Test-Path $shortcutIconSource)) {
  throw "Shortcut icon not found: $shortcutIconSource"
}
Copy-Item -LiteralPath $shortcutIconSource -Destination $shortcutIconTarget -Force

if ($IncludeRuntime) {
  $runtimeScript = Join-Path $projectRoot "scripts\build_runtime.ps1"
  & powershell -ExecutionPolicy Bypass -File $runtimeScript -OutputDirectory (Join-Path $portableDir "runtime\python")
  if ($LASTEXITCODE -ne 0) {
    throw "Bundled runtime build failed with exit code $LASTEXITCODE"
  }

  $adbCommand = Get-Command adb -ErrorAction SilentlyContinue
  if (!$adbCommand) {
    throw "ADB was not found. Install Android platform-tools before building an installed release."
  }
  $adbSource = Split-Path -Parent $adbCommand.Source
  $androidRuntime = Join-Path $portableDir "runtime\android"
  New-Item -ItemType Directory -Path $androidRuntime -Force | Out-Null
  foreach ($file in @("adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "NOTICE.txt")) {
    $source = Join-Path $adbSource $file
    if (Test-Path $source) {
      Copy-Item -LiteralPath $source -Destination (Join-Path $androidRuntime $file) -Force
    } elseif ($file -ne "NOTICE.txt") {
      throw "Required Android platform-tools file not found: $source"
    }
  }
  [ordered]@{
    schema = 1
    appVersion = $appVersion
    bundledPython = "runtime/python/python.exe"
    bundledAdb = "runtime/android/adb.exe"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
  } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $portableDir "motuperf-package.json") -Encoding UTF8
}

$readme = @(
  "MoTuPerf-v$appVersion C# portable package",
  "====================================",
  "",
  "Usage:",
  "1. Double-click CSharpIosPerfMonitor.exe.",
  "2. Connect and trust the iPhone over USB, or connect an Android phone with USB debugging authorized.",
  "3. Choose device, app and process.",
  "4. Click Start Capture.",
  "",
  "Notes:",
  "- This is the C# WPF native UI build.",
  "- The package is win-x64 self-contained; users do not need to install .NET.",
  ($(if ($IncludeRuntime) { "- The installed release includes its pinned Python iOS runtime and Android adb. Do not install Python packages manually." } else { "- This historical portable ZIP is a development handoff and does not include the iOS Python runtime." })),
  "- iOS still requires Apple Mobile Device Support from the official Apple Devices app and a trusted, unlocked device.",
  "- Android requires USB debugging authorization.",
  "- CPU and memory are collected strictly by the selected process pid.",
  "- iOS FPS/Jank prefer ordered display frame timing from CoreProfile; if unavailable, FPS falls back to screen/global graphics sampling and Jank stays unavailable.",
  "- Android FPS is collected from gfxinfo framestats when available, with a SurfaceFlinger current-layer fallback for SurfaceView mini-games.",
  "- Device temperature is sampled every 5 seconds. Android shows the actual sensors exposed by the system; non-jailbroken iOS devices expose battery temperature only.",
  "- iOS Thermal State is exported as 0 nominal, 1 fair, 2 serious, or 3 critical when Xcode Energy exposes the real field; unavailable values stay blank.",
  "- Screenshots, exports and crash logs are written to the data directory next to the EXE."
)
Set-Content -LiteralPath (Join-Path $portableDir "README.txt") -Value $readme -Encoding ASCII

if (!$SkipArchive) {
  $compressed = $false
  for ($i = 1; $i -le 5; $i++) {
    try {
      Start-Sleep -Milliseconds (400 * $i)
      Compress-Archive -LiteralPath $portableDir -DestinationPath $zipPath -Force -ErrorAction Stop
      $compressed = $true
      break
    } catch {
      if ($i -eq 5) {
        throw
      }
    }
  }

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive = $null
  try {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    $hasExecutable = $archive.Entries | Where-Object {
      ($_.FullName -replace '\\', '/').EndsWith('/CSharpIosPerfMonitor.exe', [System.StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1
    if (!$hasExecutable) {
      throw "Portable ZIP is incomplete: CSharpIosPerfMonitor.exe is missing."
    }
  } finally {
    if ($archive) {
      $archive.Dispose()
    }
  }
}

Write-Host "C# portable build complete:"
Write-Host $portableDir
if (!$SkipArchive) {
  Write-Host $zipPath
}
