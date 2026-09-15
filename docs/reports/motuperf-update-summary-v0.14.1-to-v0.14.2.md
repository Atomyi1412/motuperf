# MoTuPerf v0.14.1 至 v0.14.2 更新说明

## v0.14.2

- 修复 Windows 电脑已安装 Apple Mobile Device Support，但 Apple Mobile Device 服务未运行或 USB 通信通道异常时，设备选择框不显示驱动修复入口的问题。
- 设备诊断现在区分“未安装驱动”和“驱动已安装但服务/通信异常”，并在无设备时保留可下载、重新安装驱动的入口。
- 修复状态只影响设备检测提示和驱动修复入口，不改变 Android/iOS 性能指标的采集口径。
