# MoTuPerf Windows x64 安装包

在项目根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File motuperf_cross_platform\packaging\windows\build-installer.ps1
```

默认生成：

- `motuperf_cross_platform/dist/installer/MoTuPerf-Setup-v0.24.1.exe`
- `motuperf_cross_platform/dist/installer/MoTuPerf-Setup-v0.24.1.exe.sha256`

自动更新优先使用发起更新的客户端安装目录，显式 `/D=` 参数优先于旧安装记录。隔离验证必须同时传入 `/TESTMODE` 和最后一个参数 `/D=绝对目录`，不写真实卸载记录或快捷方式。

安装包包含 Windows x64 自包含 .NET、固定依赖的 Python/iOS 运行时、ADB 和采集脚本。安装目录和卸载标识与 WPF `v0.4.65` 相互独立，不覆盖旧版安装记录。
