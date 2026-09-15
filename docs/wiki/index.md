# 知识库索引

## 资料来源

- [PerfDog 与 UWA Gears 参考摘要](./sources/perfdog-and-gears.md): 记录本项目参考的性能工具能力边界、实时采集和导出行为。
- [trellis-codex-workflow 技能包摘要](./sources/trellis-codex-workflow-skill.md): 记录用户提供 zip 的内容、能力边界和当前项目治理状态。
- [本机 iOS 采集依赖探测](./sources/local-ios-dependency-probe.md): 记录本机 Apple Mobile Device Service 和采集 CLI 可用性。

## 决策记录

- [已确认决策](./questions/open-decisions.md): 记录进入编码前已确认的产品边界和实现形态。

## 需求

- [GitHub 自动发布与客户端更新](../requirements/github-release-auto-update.md): 记录公开仓库、自动构建、Release 产物和客户端更新策略。
- [iOS 微信小游戏性能采集 MVP](../requirements/ios-wechat-minigame-performance-mvp.md): 第一版 USB 模式采集需求草案。
- [MoTuPerf 跨平台桌面版](../requirements/motuperf-cross-platform-desktop.md): 保留 WPF v0.4.65 并迁移到 Windows/macOS 共用 Avalonia UI 的需求草案。
- [iOS 微信小游戏内部热表现对比测试](../requirements/ios-wechat-minigame-thermal-comparison.md): 自研游戏与一款同类竞品的多人并行、同设备配对测试口径。

## 实现

- [GitHub 自动发布与客户端更新实现](../implementation/github-release-auto-update.md): 记录 GitHub Actions、Release 清单和跨平台客户端更新的实现边界。
- [iOS 微信小游戏性能采集 MVP 实现](../implementation/ios-wechat-minigame-performance-mvp.md): 第一版桌面工具实现草案。
- [MVP 编码就绪清单](../implementation/mvp-coding-readiness.md): 记录确认后的最小编码顺序、API 契约和 UI 验收要点。
- [MoTuPerf 跨平台桌面版实现](../implementation/motuperf-cross-platform-desktop.md): Avalonia 共享 UI、平台适配、旧版保护和 Apple Silicon 发布顺序。
- [iOS 微信小游戏内部热表现对比执行方案](../implementation/ios-wechat-minigame-thermal-comparison.md): 设备矩阵、USB 条件、基线冷却、AB/BA 顺序、记录和判定 SOP。

## 报告

- [MoTuPerf v0.4.62 至 v0.10.1 更新总结](../reports/motuperf-update-summary-v0.4.62-to-v0.10.1.md): 汇总版本演进、核心能力、验证情况、交付状态和遗留风险。
- [MoTuPerf v0.14.1 至 v0.14.2 更新说明](../reports/motuperf-update-summary-v0.14.1-to-v0.14.2.md): 记录 Apple 驱动、服务和 USB 通道异常诊断修复。
- [MoTuPerf v0.14.2 至 v0.14.3 更新说明](../reports/motuperf-update-summary-v0.14.2-to-v0.14.3.md): 记录采集异常诊断日志、停止原因和 Windows 交付验证。
- [MoTuPerf v0.14.3 至 v0.15.0 更新说明](../reports/motuperf-update-summary-v0.14.3-to-v0.15.0.md): 记录 iOS 目标应用进程重启后的安全 PID 重绑定和发布验证。
- [MoTuPerf v0.15.0 至 v0.15.1 更新说明](../reports/motuperf-update-summary-v0.15.0-to-v0.15.1.md): 记录进程选择 PID 0 误传修复和发布验证边界。
- [MoTuPerf v0.15.1 至 v0.15.2 更新说明](../reports/motuperf-update-summary-v0.15.1-to-v0.15.2.md): 补全 iOS 设备名称、CPU/SoC 和屏幕分辨率。
- [MoTuPerf v0.15.2 至 v0.16.0 更新说明](../reports/motuperf-update-summary-v0.15.2-to-v0.16.0.md): 新增可选曲线框选缩放和视图还原，缩放不影响采集与现场数据。
- [MoTuPerf v0.16.0 至 v0.16.1 更新说明](../reports/motuperf-update-summary-v0.16.0-to-v0.16.1.md): 优化大现场文件后台加载、缩略图内存占用和截图查看稳定性。
- [MoTuPerf v0.16.1 至 v0.16.2 更新说明](../reports/motuperf-update-summary-v0.16.1-to-v0.16.2.md): 修复大现场打开过慢、截图列表集中创建和闪退缺少日志的问题。
