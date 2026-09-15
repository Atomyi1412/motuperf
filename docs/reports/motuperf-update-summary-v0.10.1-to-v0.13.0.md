# MoTuPerf v0.10.1 至 v0.13.0 更新说明

## v0.13.0

- 设备检测提示改为跨平台通用文案，不再只提示“未检测到 iOS 设备”。
- Windows 检测到缺少 Apple 移动设备驱动时，明确提示安装官方“Apple 设备”应用，并提供可点击的 Apple 官方下载页面入口。
- Android 设备检测仍按 USB 调试和授权状态提示；普通未连接设备不会显示苹果驱动下载按钮。

## v0.11.0

- 精简“实时数据”和“选中数据”面板，移除固定来源说明 `Temp Sensors` 与 `Thermal Source`，保留实际动态性能数据。
- 温度传感器集合和热状态来源仍完整保留在原始样本、CSV 与诊断链路中，不影响数据记录和导出。

## v0.12.0

- CSV 导出新增“秒级汇总”和“原始明细”两种模式。
- 默认秒级汇总每秒一行，便于查看、筛选和与其他工具对比；原始明细继续保留全部采样行和精确时间。
- `.motuperf` 现场文件、图表和顶部统计继续使用完整原始数据，不因 CSV 汇总而丢失数据。
- 秒级汇总按指标语义处理：FPS 按观察时长加权，Jank/BigJank 累加，FrameTime 峰值保留最大值，CPU 取同秒平均，内存、温度和 Thermal State 取同秒最后一次真实更新且不伪造补值。
- 新增导出方式选择弹框和对应帮助说明，原始明细文件使用 `motuperf-raw-*` 命名。

## v0.12.1

- 优化“选择导出方式”弹框，两个导出选项改为等宽铺满并收紧窗口、正文和底栏间距，减少无效留白。
- 保持深色、明亮、绿色三套主题的推荐、悬浮和边框状态一致。

## 数据验证

- 使用真实现场验证：7,307 条原始样本汇总为 1,316 条秒级记录，顶部统计保持一致。
- FPS、Jank、BigJank 与 FrameTime Max 的有效点、总量和最大值在原始与秒级导出中保持一致。
- 当前自动化验证覆盖 Core 9 项、Platform 5 项、Desktop 34 项。

## 发布边界

- Windows 安装包为 x64 自包含版本，包含 .NET、固定 Python/iOS 依赖、ADB 和采集脚本。
- 安装器尚未进行商业代码签名；macOS M 系列的真实设备、签名和公证验收不属于本 Windows 安装包。

## Windows v0.12.1 安装包（历史）

- 文件：`motuperf_cross_platform/dist/installer/MoTuPerf-Setup-v0.12.1.exe`
- 大小：`104,752,009` bytes
- ProductVersion：`0.12.1`
- FileVersion：`0.12.1.0`
- SHA-256：`89442B6E2B1B8C07A3341F1ED787A31B6A75595653AD8CD82D7612EEB3016117`
- 隔离安装验证：安装成功，程序可启动且窗口保持响应，内置 iOS 依赖可导入，ADB 可执行，发布目录无 PDB，静默卸载后测试目录完整移除。

## Windows v0.13.0 安装包

- 文件：`motuperf_cross_platform/dist/installer/MoTuPerf-Setup-v0.13.0.exe`
- 内容：在 v0.12.1 基础上加入跨平台设备提示、Windows 苹果驱动缺失诊断，以及 Apple 官方驱动下载入口。
- 安装包包含 x64 自包含 .NET、固定 Python/iOS 依赖、ADB 和采集脚本；安装器未进行商业代码签名。
