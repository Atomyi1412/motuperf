# MoTuPerf v0.14.3 至 v0.15.0 更新说明

## v0.15.0

- iOS 目标应用主进程重启或 PID 变化后，按 Bundle ID、进程名和 `isApplication=true` 唯一确认新 PID，并在新 PID 真正进入 sysmontap 后自动恢复 CPU/内存采集。
- PID 暂时消失期间不复用旧值、不插值、不平滑，CPU/内存保持不可用；FPS、FrameTime、Jank、温度等原有屏幕级或设备级数据继续按各自真实来源采集。
- WebKit、GPU、网络等子进程不参与应用级自动重绑定；候选不唯一、身份无法确认或进程名不一致时继续等待或按原规则报错，避免把数据归属到错误进程。
- 增加 `target_rebound` 事件和 C# 诊断日志，记录旧 PID、新 PID、Bundle ID 与重绑定原因，便于排查采集中断和进程重启。

## 验证与交付

- Python 回归：273 passed，2 warnings，12 subtests passed。
- 跨平台 .NET Release 测试：Core 12、Platform 20、Desktop 34 全部通过；Avalonia 分析器仅有本机编译器版本提示。
- Windows x64 安装包：`motuperf_cross_platform/dist/installer/MoTuPerf-Setup-v0.15.0.exe`，104740229 bytes；SHA-256 `E8CBE1F5AAC75DE67B1E53F52C4802AEDFC52D174CD627E159D3F6FAC7E1D938`，sidecar 回读一致。
- iOS 真机进程重启后的连续采集仍需在同事设备上现场验收；自动化测试已覆盖唯一候选、PID 缺失窗口、重绑定等待和错误身份拒绝。
