param(
  [string]$Version = "0.42.1"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$publish = Join-Path $root "dist/macos-arm64-check"
$packageRoot = Join-Path $root "dist/macos-arm64-test-v$Version"
$app = Join-Path $packageRoot "MoTuPerf.app"
$zip = Join-Path $root "dist/MoTuPerf-v$Version-macos-arm64-ui-test.zip"

if (!(Test-Path (Join-Path $publish "MoTuPerf.CrossPlatform"))) {
  throw "Missing osx-arm64 publish output: $publish"
}
if (Test-Path $packageRoot) { Remove-Item $packageRoot -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Path "$app/Contents/MacOS", "$app/Contents/Resources/Assets", "$app/Contents/Resources/tools" -Force | Out-Null
Copy-Item (Join-Path $publish "*") (Join-Path $app "Contents/MacOS") -Recurse -Force
Remove-Item (Join-Path $app "Contents/MacOS/Assets") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $app "Contents/MacOS/tools") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $app "Contents/MacOS/*.exe") -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $publish "tools/*") (Join-Path $app "Contents/Resources/tools") -Recurse -Force
Copy-Item (Join-Path $publish "Assets/*") (Join-Path $app "Contents/Resources/Assets") -Recurse -Force
$plist = Get-Content (Join-Path $root "packaging/macos-arm64/Info.plist") -Raw
$plist = $plist.Replace("__MOTUPERF_SHORT_VERSION__", $Version).Replace("__MOTUPERF_BUILD_VERSION__", "$Version.1")
Set-Content (Join-Path $app "Contents/Info.plist") $plist -Encoding utf8
Copy-Item (Join-Path $root "../motuperf_cross_platform/src/MoTuPerf.Desktop/Assets/fluent-icons-LICENSE.txt") (Join-Path $app "Contents/Resources/Assets/fluent-icons-LICENSE.txt") -Force
Copy-Item (Join-Path $PSScriptRoot "launch-test-package.command") (Join-Path $packageRoot "launch-test-package.command") -Force
Copy-Item (Join-Path $PSScriptRoot "README-ui-test-package.md") (Join-Path $packageRoot "README.md") -Force
$manifest = [ordered]@{ schema = 1; appVersion = $Version; target = "osx-arm64"; packageType = "cross-published-ui-test"; bundledRuntime = "not included; build natively on macOS for collection"; entryPoint = "launch-test-package.command" } | ConvertTo-Json
Set-Content (Join-Path $packageRoot "motuperf-package.json") $manifest -Encoding utf8
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zip -CompressionLevel Optimal
Get-Item $zip | Select-Object FullName, Length
