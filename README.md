# MoTuPerf

MoTuPerf 是一款面向 Android 和 iOS 真机的性能采集工具，支持 FPS、FrameTime、Jank、BigJank、进程 CPU、内存、设备温度、Thermal Status/State、实时截图和现场文件保存。

当前跨平台桌面版使用 .NET 8 + Avalonia：

- Windows x64：提供自包含安装包，内置采集所需的 Python、ADB 和脚本依赖。
- macOS Apple Silicon：提供 `osx-arm64` DMG，暂不支持 Intel Mac；打开 DMG 后将应用拖入 Applications。
- 现场文件、日志和设备数据保存在用户配置的数据目录，不会上传到 GitHub。

## 获取与更新

源码和 Release 位于 [Atomyi1412/motuperf](https://github.com/Atomyi1412/motuperf)。安装包发布在 GitHub Releases，客户端默认异步检查新版本，也可以在设置中关闭。下载前会校验 SHA-256；Windows 启动安装器，macOS 打开 DMG 供用户手动安装。

macOS 内测包使用 ad-hoc 签名，正式分发前仍需要 Developer ID 签名和 Apple 公证。

## 从源码运行

### Windows

需要 .NET 8 SDK、Python 3.11、Android platform-tools 和 NSIS：

```powershell
dotnet restore motuperf_cross_platform/MoTuPerf.CrossPlatform.sln --locked-mode
dotnet run --project motuperf_cross_platform/src/MoTuPerf.Desktop/MoTuPerf.Desktop.csproj
```

构建安装包：

```powershell
powershell -ExecutionPolicy Bypass -File motuperf_cross_platform/packaging/windows/build-installer.ps1
```

### macOS Apple Silicon

需要 M 系列 Mac、.NET 8 SDK、Xcode Command Line Tools、Android platform-tools 和 arm64 Python 构建环境：

```bash
bash motuperf_cross_platform/packaging/macos-arm64/build-on-mac.command
```

详细打包边界见 [macOS 打包说明](motuperf_cross_platform/packaging/macos-arm64/README.md)。

## 测试

```powershell
dotnet test motuperf_cross_platform/MoTuPerf.CrossPlatform.sln --configuration Release --no-restore
$env:PYTEST_DISABLE_PLUGIN_AUTOLOAD = "1"
python -m pytest tests -q
```

自动发布要求项目版本、标签和安装包保持一致。推送 `vX.Y.Z` 标签后，GitHub Actions 会先运行测试，再构建 Windows 安装包和 macOS arm64 DMG，生成 `latest.json` 和校验文件并创建 Release。

## 文档

- [GitHub 自动发布与客户端更新需求](docs/requirements/github-release-auto-update.md)
- [GitHub 自动发布与客户端更新实现](docs/implementation/github-release-auto-update.md)
- [更新日志](CHANGELOG.md)
- [跨平台桌面版实现说明](docs/implementation/motuperf-cross-platform-desktop.md)
- [PerfDog 与 Gears 资料摘要](docs/wiki/sources/perfdog-and-gears.md)

## 许可证

本项目采用 [MIT License](LICENSE)。
