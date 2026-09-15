#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
REPO_DIR="$(cd "$ROOT_DIR/.." && pwd)"
VERSION="${MOTUPERF_VERSION:-0.21.7}"
CONFIGURATION="${CONFIGURATION:-Release}"
VERSION_SHORT="${VERSION%%-*}"
BUILD_NUMBER="${MOTUPERF_BUILD_NUMBER:-1}"
VERSION_BUILD="${VERSION_SHORT}.${BUILD_NUMBER}"
OUTPUT_ROOT="${OUTPUT_ROOT:-$ROOT_DIR/dist/macos-arm64}"
APP_DIR="$OUTPUT_ROOT/MoTuPerf.app"
MACOS_DIR="$APP_DIR/Contents/MacOS"
RESOURCES_DIR="$APP_DIR/Contents/Resources"
PUBLISH_DIR="$ROOT_DIR/artifacts/publish/osx-arm64"
PYTHON_RUNTIME_DIR="$RESOURCES_DIR/runtime/python"
ADB_RUNTIME_DIR="$RESOURCES_DIR/runtime/android"

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  echo "MoTuPerf 首个 macOS 版本只能在 Apple Silicon Mac 上构建。" >&2
  exit 2
fi

command -v dotnet >/dev/null || { echo "缺少 .NET 8 SDK。" >&2; exit 3; }
command -v adb >/dev/null || { echo "缺少 Android platform-tools (adb)。" >&2; exit 3; }

rm -rf "$APP_DIR" "$PUBLISH_DIR"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR/tools" "$PYTHON_RUNTIME_DIR" "$ADB_RUNTIME_DIR"

dotnet restore "$ROOT_DIR/MoTuPerf.CrossPlatform.sln" --locked-mode
dotnet publish "$ROOT_DIR/src/MoTuPerf.Desktop/MoTuPerf.Desktop.csproj" \
  -c "$CONFIGURATION" -r osx-arm64 --self-contained true \
  -p:UseAppHost=true -p:PublishSingleFile=false \
  -o "$PUBLISH_DIR" --no-restore

cp -R "$PUBLISH_DIR/." "$MACOS_DIR/"
rm -rf "$MACOS_DIR/tools"
sed \
  -e "s/__MOTUPERF_SHORT_VERSION__/$VERSION_SHORT/g" \
  -e "s/__MOTUPERF_BUILD_VERSION__/$VERSION_BUILD/g" \
  "$SCRIPT_DIR/Info.plist" > "$APP_DIR/Contents/Info.plist"
cp "$REPO_DIR/csharp_perf_monitor/tools/"*.py "$RESOURCES_DIR/tools/"
cp "$SCRIPT_DIR/requirements-metrics-macos.txt" "$RESOURCES_DIR/tools/"

ICON_SOURCE="$REPO_DIR/csharp_perf_monitor/assets/motu-shortcut-icon.png"
ICONSET="$OUTPUT_ROOT/MoTuPerf.iconset"
rm -rf "$ICONSET"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$RESOURCES_DIR/MoTuPerf.icns"
rm -rf "$ICONSET"

PYTHON_BUILD_TAG="${PYTHON_BUILD_TAG:-20260814}"
PYTHON_BUILD_VERSION="${PYTHON_BUILD_VERSION:-3.11.16}"
PYTHON_ARCHIVE="cpython-${PYTHON_BUILD_VERSION}+${PYTHON_BUILD_TAG}-aarch64-apple-darwin-install_only_stripped.tar.gz"
PYTHON_URL="https://github.com/astral-sh/python-build-standalone/releases/download/${PYTHON_BUILD_TAG}/${PYTHON_ARCHIVE}"
PYTHON_SHA256="${PYTHON_SHA256:-a394f2bf78a48990fc88e7c5586c7e1be5e69f0c9bd027883211b68b768e11c5}"
PYTHON_DOWNLOAD="$OUTPUT_ROOT/$PYTHON_ARCHIVE"
curl --fail --location --retry 3 --output "$PYTHON_DOWNLOAD" "$PYTHON_URL"
echo "$PYTHON_SHA256  $PYTHON_DOWNLOAD" | shasum -a 256 -c -
tar -xzf "$PYTHON_DOWNLOAD" --strip-components=1 -C "$PYTHON_RUNTIME_DIR"
rm -f "$PYTHON_DOWNLOAD"
"$PYTHON_RUNTIME_DIR/bin/python3" -m ensurepip --upgrade >/dev/null 2>&1 || true
"$PYTHON_RUNTIME_DIR/bin/python3" -m pip install --upgrade pip
"$PYTHON_RUNTIME_DIR/bin/python3" -m pip install -r "$SCRIPT_DIR/requirements-metrics-macos.txt"
"$PYTHON_RUNTIME_DIR/bin/python3" -m pip check
"$PYTHON_RUNTIME_DIR/bin/python3" -m pip freeze > "$RESOURCES_DIR/tools/requirements-metrics-macos-lock.txt"

cp "$(command -v adb)" "$ADB_RUNTIME_DIR/adb"
chmod +x "$MACOS_DIR/MoTuPerf.CrossPlatform" "$PYTHON_RUNTIME_DIR/bin/python3" "$ADB_RUNTIME_DIR/adb"

cat > "$RESOURCES_DIR/motuperf-package.json" <<JSON
{
  "schema": 1,
  "appVersion": "$VERSION",
  "target": "osx-arm64",
  "bundledPython": "runtime/python/bin/python3",
  "bundledAdb": "runtime/android/adb",
  "signature": "ad-hoc-internal",
  "generatedAtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
JSON

"$SCRIPT_DIR/verify-app.sh" "$APP_DIR"

# Ad-hoc signing is only for internal acceptance. Public distribution requires
# Developer ID signing and Apple notarization of every nested executable.
codesign --force --deep --sign - "$APP_DIR"
codesign --verify --deep --strict --verbose=2 "$APP_DIR"

if [[ "${CREATE_DMG:-1}" == "1" ]]; then
  DMG_STAGING_DIR="$OUTPUT_ROOT/dmg-staging"
  DMG_PATH="$OUTPUT_ROOT/MoTuPerf-$VERSION-osx-arm64-internal.dmg"
  rm -rf "$DMG_STAGING_DIR"
  rm -f "$DMG_PATH"
  mkdir -p "$DMG_STAGING_DIR"
  cp -R "$APP_DIR" "$DMG_STAGING_DIR/MoTuPerf.app"
  ln -s /Applications "$DMG_STAGING_DIR/Applications"
  hdiutil create -volname "MoTuPerf" -srcfolder "$DMG_STAGING_DIR" -ov -format UDZO "$DMG_PATH"
  rm -rf "$DMG_STAGING_DIR"
  shasum -a 256 "$DMG_PATH" > "$DMG_PATH.sha256"
fi

echo "M 系列内测包已生成：$APP_DIR"
