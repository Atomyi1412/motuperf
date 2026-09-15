#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

finish() {
  status=$?
  if [[ $status -eq 0 ]]; then
    echo
    echo "构建完成，已打开安装包所在目录。"
  else
    echo
    echo "构建失败，请保留上方错误信息。"
  fi
  echo "按回车键关闭窗口。"
  read -r _ || true
  exit "$status"
}
trap finish EXIT

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  echo "此构建入口只支持 Apple Silicon Mac（M1/M2/M3/M4）。"
  exit 2
fi

missing=()
command -v dotnet >/dev/null || missing+=(".NET 8 SDK")
command -v adb >/dev/null || missing+=("Android platform-tools")
xcode-select -p >/dev/null 2>&1 || missing+=("Xcode Command Line Tools")

if (( ${#missing[@]} > 0 )); then
  echo "缺少构建环境："
  printf '  - %s\n' "${missing[@]}"
  echo
  echo "安装完成后重新双击本文件。"
  echo "Xcode 工具可运行：xcode-select --install"
  echo "Homebrew 可运行：brew install --cask dotnet-sdk android-platform-tools"
  exit 3
fi

if ! dotnet --list-sdks | grep -q '^8\.'; then
  echo "已找到 dotnet，但没有安装 .NET 8 SDK。"
  exit 3
fi

chmod +x "$SCRIPT_DIR/build-app.sh" "$SCRIPT_DIR/verify-app.sh"
"$SCRIPT_DIR/build-app.sh"
open "$SCRIPT_DIR/../../dist/macos-arm64"
