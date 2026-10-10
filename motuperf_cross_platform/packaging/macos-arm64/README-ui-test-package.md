# MoTuPerf v0.42.1 macOS Apple Silicon UI 测试包

## 运行

1. 解压到本地目录。
2. 打开“终端”，进入解压后的目录。
3. 执行：

```bash
bash launch-test-package.command
```

也可以先执行 `chmod +x launch-test-package.command`，再双击脚本。

## 本包用途

本包用于验证 v0.42.1 的 macOS 界面和图标显示，目标设备为 M1/M2/M3/M4。

## 限制

- 这是 Windows 交叉构建的 UI 测试包，不是签名公证的 DMG 安装包。
- 未捆绑 macOS 专用 Python、ADB 和 iOS 依赖，因此不要用本包进行 Android/iOS/Harmony 真机采集。
- 完整采集包需要在 Apple Silicon Mac 上执行 `build-on-mac.command` 原生构建。
- 首次打开如被系统拦截，请在 Finder 中右键 `MoTuPerf.app`，选择“打开”。
