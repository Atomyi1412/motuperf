# Native iOS Performance Monitor

这是独立于现有 Web 版的新原生桌面版本。现有 Web 版文件保持保留，新版本放在 `native_perf_monitor/` 下。

## 设计目标

- 使用 PyQt5 原生窗口，不再依赖浏览器作为主界面。
- 使用 pyqtgraph 绘制实时曲线，减少长时间采集时的 UI 卡顿。
- 采集、截图、设备/应用/进程刷新全部放到后台线程。
- 截图默认 3 秒一次，截图命令异步执行，不阻塞图表刷新。
- 横轴跟随最新数据时从 0 秒开始，FPS、Jank、BigJank、内存和截图时间线联动。
- 开发运行时数据写入 `native_perf_monitor/data`，不会混到旧 Web 版的数据目录里。

## 本地运行

```powershell
cd D:\文档\motutest
python -m pip install -r native_perf_monitor\requirements-native.txt
python native_perf_monitor\launcher.py
```

## 打包绿色版

```powershell
cd D:\文档\motutest
powershell -ExecutionPolicy Bypass -File native_perf_monitor\scripts\build_native_portable.ps1
```

输出：

- `dist\native-ios-perf-monitor-portable\`
- `dist\native-ios-perf-monitor-portable.zip`

## 使用提醒

- 目标电脑仍然需要 Apple Mobile Device Support。
- iPhone 需要 USB 连接，并在手机上信任当前电脑。
- 微信小游戏建议先选择微信 `com.tencent.xin`，再观察 `WeChat` / `WebContent` 相关进程。
- 截图频率建议保持 3 秒或更高，太低会增加 PC 和手机侧的额外负载。
