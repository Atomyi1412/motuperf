# MoTuPerf macOS Apple Silicon 内测构建

首期只支持 Apple Silicon (`osx-arm64`)，不生成 Intel (`osx-x64`) 包。

## 构建前置

- M1/M2/M3/M4 Mac，macOS 12 或更高版本。
- .NET 8 SDK。
- arm64 Python 3。
- Android platform-tools (`adb`)。
- Xcode Command Line Tools（用于原生 Python 依赖和签名工具）。

## 构建

在 M 系列 Mac 上解压构建包后，打开“终端”，进入解压目录并执行：

```bash
bash motuperf_cross_platform/packaging/macos-arm64/build-on-mac.command
```

脚本成功运行一次并获得可执行权限后，也可以直接双击 `build-on-mac.command`。使用 `bash` 启动不依赖 Windows ZIP 是否保留了 macOS 可执行位。

也可以在终端执行：

```bash
chmod +x packaging/macos-arm64/*.sh
./packaging/macos-arm64/build-app.sh
```

默认生成：

- `dist/macos-arm64/MoTuPerf.app`
- `dist/macos-arm64/MoTuPerf-v0.24.1-osx-arm64.dmg`
- `dist/macos-arm64/MoTuPerf-v0.24.1-osx-arm64.dmg.sha256`

打开 DMG 后，将 `MoTuPerf.app` 拖到 `Applications`。当前内测包使用 ad-hoc 签名，第一次启动需在 Finder 中右键应用并选择“打开”；它不是已公证的公开发行包。

当前脚本下载固定版本、校验 SHA-256 的可重定位 Python 运行时，并进行 ad-hoc 签名。Developer ID 签名与 Apple 公证尚未配置，不能把构建成功当作已通过 Gatekeeper 验证。

Windows 交叉发布和目录检查不能替代 M 系列 Mac 上的应用启动、USB 设备、采集、截图、现场文件和 CSV 验收。
