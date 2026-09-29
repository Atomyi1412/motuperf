# MoTuPerf Windows x64 安装包

在项目根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File motuperf_cross_platform\packaging\windows\build-installer.ps1
```

默认生成：

- `motuperf_cross_platform/dist/installer/MoTuPerf-Setup-v0.41.3.exe`
- `motuperf_cross_platform/dist/installer/MoTuPerf-Setup-v0.41.3.exe.sha256`

自动更新优先使用发起更新的客户端安装目录，显式 `/D=` 参数优先于旧安装记录。隔离验证必须同时传入 `/TESTMODE` 和最后一个参数 `/D=绝对目录`，不写真实卸载记录或快捷方式。

安装包包含 Windows x64 自包含 .NET、固定依赖的 Python/iOS 运行时、ADB 和采集脚本。鸿蒙 HDC 不在本安装包内捆绑；首次使用鸿蒙设备时，在设备选择页点击“下载鸿蒙连接工具”，只下载官方 `Command Line Tools`，解压后在向导中选择最外层的 `command-line-tools` 文件夹，MoTuPerf 会自动找到 `sdk\default\openharmony\toolchains\hdc.exe` 并保存位置。安装目录和卸载标识与 WPF `v0.4.65` 相互独立，不覆盖旧版安装记录。
