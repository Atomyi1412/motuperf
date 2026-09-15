# MoTuPerf Cross-Platform UI

This directory contains the Avalonia desktop UI for the next MoTuPerf version.
It is separate from the preserved Windows WPF `v0.4.65` project.

## Release Scope

- Windows development and shared-core verification.
- macOS Apple Silicon only: `osx-arm64` for M1/M2/M3/M4.
- Intel macOS (`osx-x64`) is intentionally out of scope for the first release.
- The UI uses the existing real-device collection, metric, screenshot, session, and CSV contracts. It does not enable simulated data.

## Build And Test

```powershell
dotnet restore MoTuPerf.CrossPlatform.sln --locked-mode
dotnet test MoTuPerf.CrossPlatform.sln -c Release --no-restore
dotnet publish src/MoTuPerf.Desktop/MoTuPerf.Desktop.csproj -c Release -r osx-arm64 --self-contained true -p:UseAppHost=true -p:PublishSingleFile=false -o artifacts/publish/osx-arm64 --no-restore
```

The cross-publish command proves dependency resolution and produces an arm64 .NET payload. It is not a substitute for launching the app on an M-series Mac.

## M-Series App Package

Run the following on an Apple Silicon Mac with .NET 8, `adb`, Xcode Command Line Tools, and network access:

```bash
chmod +x packaging/macos-arm64/*.sh
./packaging/macos-arm64/build-app.sh
```

The script bundles arm64 Python, ADB, the collection scripts, the Avalonia app, an ad-hoc internal signature, and optionally an arm64 DMG. Developer ID signing and notarization are still required for external distribution.

## Preserved Baseline

The Windows WPF `v0.4.65` source archive and installer are preserved in:

```text
../dist/backups/MoTuPerf-v0.4.65-baseline/
```

Do not overwrite that directory while developing the cross-platform version.
