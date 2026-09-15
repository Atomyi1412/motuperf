# C# Native iOS Performance Monitor

这是第三个独立版本，和现有 Web 版、`native_perf_monitor` PyQt 版并排保留。

## 技术路线

- C# / WPF 原生 Windows 窗口。
- 不依赖浏览器，不依赖 Python UI。
- 当前工程使用 .NET Framework 自带 `csc.exe` 编译，避免当前机器没有 .NET SDK 时无法构建。
- 图表使用 WPF Canvas 自绘，避免 NuGet 依赖。
- 底层 iOS 通信仍调用已验证的 `tidevice` / `pyidevice` 命令。

## 本机运行

```powershell
cd D:\文档\motutest
powershell -ExecutionPolicy Bypass -File csharp_perf_monitor\scripts\build_portable.ps1
dist\csharp-ios-perf-monitor-portable\CSharpIosPerfMonitor.exe
```

## 功能

- 选择设备、应用、进程。
- 应用和进程支持关键词搜索。
- 实时显示 FPS、Jank、BigJank、内存。
- FPS/Jank/BigJank、内存、截图时间线三块联动。
- 截图默认 3 秒一次，后台异步执行。
- CSV 导出。

## 说明

- 目标电脑仍然需要 Apple Mobile Device Support。
- iPhone 需要 USB 连接并信任电脑。
- 微信小游戏建议选择微信 `com.tencent.xin`，并关注 `WeChat`、`WebContent` 相关进程。
