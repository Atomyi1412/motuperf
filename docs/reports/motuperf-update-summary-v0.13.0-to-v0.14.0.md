# MoTuPerf v0.13.0 至 v0.14.0 更新说明

## v0.14.0

- 苹果设备驱动入口改为直接下载 Apple 官方 Windows 64 位安装包，不再跳转支持页面让用户手动查找。
- 下载完成后弹出安装确认，用户确认后才启动 Windows 安装向导；取消安装时保留已下载文件，方便稍后处理。
- 下载过程显示进度，并使用临时文件和大小、HTTPS、Apple 域名、`.exe` 响应校验，避免把不完整或非安装包文件交给用户。
- 驱动下载入口仍只在 Windows 检测到缺少 Apple 移动设备支持时显示，Android 正常检测和 macOS 行为不受影响。

## 验证边界

- 通过跨平台 Core、Platform、Desktop 自动化测试，旧版 WPF 构建和 Windows 安装包回归。
- 下载地址使用 Apple 官方 iTunes Windows 64 位入口；该安装包包含 Apple Mobile Device Support。安装完成后仍需重新插拔设备并在工具中刷新。
- MoTuPerf 安装器和下载的 Apple 安装包均需要用户确认后启动；本版本不静默安装驱动。
