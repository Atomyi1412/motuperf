#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$SCRIPT_DIR/MoTuPerf.app"
APP_EXECUTABLE="$APP_DIR/Contents/MacOS/MoTuPerf.CrossPlatform"

if [[ ! -f "$APP_EXECUTABLE" ]]; then
  echo "未找到 MoTuPerf.app，请确认文件夹未被移动或重命名。"
  exit 1
fi

chmod +x "$APP_EXECUTABLE"
open "$APP_DIR"
