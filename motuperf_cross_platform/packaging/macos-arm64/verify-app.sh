#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${1:-}"
[[ -n "$APP_DIR" && -d "$APP_DIR" ]] || { echo "用法：verify-app.sh /path/to/MoTuPerf.app" >&2; exit 2; }

required=(
  "Contents/Info.plist"
  "Contents/MacOS/MoTuPerf.CrossPlatform"
  "Contents/Resources/motuperf-package.json"
  "Contents/Resources/MoTuPerf.icns"
  "Contents/Resources/runtime/python/bin/python3"
  "Contents/Resources/runtime/android/adb"
  "Contents/Resources/tools/android_perf_runner.py"
  "Contents/Resources/tools/pid_perf_runner.py"
  "Contents/Resources/tools/ios_process_list.py"
)

for relative in "${required[@]}"; do
  [[ -e "$APP_DIR/$relative" ]] || { echo "缺少：$relative" >&2; exit 4; }
done

grep -q '"target": "osx-arm64"' "$APP_DIR/Contents/Resources/motuperf-package.json" || { echo "包目标不是 osx-arm64。" >&2; exit 5; }
! grep -q '__MOTUPERF_' "$APP_DIR/Contents/Info.plist" || { echo "Info.plist 仍包含未替换的版本占位符。" >&2; exit 5; }
if find "$APP_DIR" -type f -exec file {} + | grep -Eiq 'PE32|MS-DOS executable'; then
  echo "M 系列包包含 Windows 格式的可执行文件。" >&2
  exit 5
fi

file "$APP_DIR/Contents/MacOS/MoTuPerf.CrossPlatform" | grep -q "arm64" || { echo "主程序不是 arm64。" >&2; exit 5; }
file "$APP_DIR/Contents/Resources/runtime/android/adb" | grep -q "arm64" || { echo "ADB 不是 arm64。" >&2; exit 5; }
"$APP_DIR/Contents/Resources/runtime/python/bin/python3" -c "import pymobiledevice3, tidevice; print('iOS runtime OK')"
"$APP_DIR/Contents/Resources/runtime/android/adb" version

echo "MoTuPerf.app 结构与 arm64 运行时检查通过。"
