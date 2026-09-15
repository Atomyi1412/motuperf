# 本机 iOS 采集依赖探测

## 探测时间

2026-06-30

## 命令结果摘要

- `tidevice`: 未找到。
- `pyidevice`: 未找到。
- `pymobiledevice3`: 未找到。
- `idevice_id`: 未找到。
- `ideviceinfo`: 未找到。
- `python`: `C:\Users\Administrator\AppData\Local\Programs\Python\Python311\python.exe`
- `pip`: `C:\Users\Administrator\AppData\Local\Programs\Python\Python311\Scripts\pip.exe`
- `Apple Mobile Device Service`: 已安装，状态为 `Running`，启动类型为 `Automatic`。
- Apple Mobile Device Support 文件存在于：
  - `C:\Program Files\Common Files\Apple\Mobile Device Support\`
  - `C:\Program Files (x86)\Common Files\Apple\Mobile Device Support\`

## 对实现的影响

- 本机具备 iOS USB 通信的 Apple 基础服务，但缺少开源采集 CLI。
- 第一版代码需要做依赖探测和清晰提示，而不是假设真实采集后端可用。
- 模拟模式可以先完成 UI、实时曲线和导出验证；真实采集后端应作为可插拔适配器。
- README 需要说明后续真实采集建议安装 `py-ios-device` 或 `tidevice`，并提示 iOS 版本兼容性。

## 待验证

- 安装 `py-ios-device` 后，`pyidevice instruments display` 是否能在 Windows 上获取 `fps`、`jank`、`big_jank`。
- 安装 `tidevice` 后，`tidevice perf -B com.tencent.xin` 是否能稳定返回 FPS 和 Memory。
