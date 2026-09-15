# MoTuPerf v0.14.2 至 v0.14.3 更新说明

## v0.14.3

- 新增每次采集独立的 JSONL 诊断日志，记录采集配置、runner 启动、stderr、runner 退出、fatal 和最终停止原因。
- 采集异常停止时，界面状态会显示“详细日志”绝对路径，并沿用现有现场数据保存询问，便于同事直接保留问题现场。
- 自动停止原因区分为设备断开、采集子进程退出、采集输出读取失败、runner fatal 和启动失败；正常点击停止记录为用户主动停止。
- 日志中的设备标识仅保留首尾字符，密码、token、secret 和授权信息会脱敏；日志写入失败不会反过来中断采集。

## 日志位置

Windows 跨平台版：

```text
%LOCALAPPDATA%\\MoTuPerf\\data\\logs\\collector-*.log
```

## 验证与交付

- Platform 20、Core 12、Desktop 34 测试通过；旧版 WPF Release 构建通过。
- Windows x64 安装包：`motuperf_cross_platform/dist/installer/MoTuPerf-Setup-v0.14.3.exe`，104,750,403 bytes。
- ProductVersion `0.14.3`、FileVersion `0.14.3.0`；SHA-256 `2F9E6C27D67FC969D7157B5C56030D2730979E0C168D61630791D138AE502B68`，sidecar 回读一致。
- 隔离安装、启动响应、版本/manifest、内置 Python、ADB、采集脚本和静默卸载均通过；另一台电脑的真实设备异常仍需用该版本现场验证。
