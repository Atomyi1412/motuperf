# Gears 对比分析参考与 MoTuPerf 实现

2026-10-20，v0.49.0。本地只读逆向，不复制竞争产品实现代码，不读取其账号或远端数据。

## 静态证据

本机 UWA-Gears.exe SHA-256 `E40CD7B2FF7F7D0DBFCE3BFBA54482EB510EA66EF28099816AB9B24FFEC301F7`。

PE 字符串引用通过 RIP 相对地址、异常表函数边界及 Capstone x64 反汇编核对：

- 五个 `##rc_panel_combo_data1`～`data5` 引用位于 RVA `0x5a3073/0x5a3137/0x5a31fc/0x5a321f/0x5a3242`，函数起点 `0x5a2330`。
- `##rc_select_show_max/avg` 引用位于 `0x5a5571/0x5a5677`，函数起点 `0x5a5370`。
- `ExportCompareDatas` 空数据和无路径检查字符串引用位于 `0x5a7f92/0x5a7fd8`，函数起点 `0x5a7f30`；同函数 `0x5a91b4` 引用 CSV `Avg,Max,`。
- 上述与用户提供的五槽、峰值/均值、区间选择和导出截图互相印证。尚未恢复全部 Gears 统计/区间归属算法或在其界面重新运行导出。

证据保存在 `.outputs/gears-export-comparison/gears-export-xrefs.json` 与 `gears-function-5a*.asm`。

## v0.49.0 界面可观察结构与二进制佐证

用户提供的 Gears 对比截图显示：顶部“选择数据”可折叠；A-E 使用高辨识色块；每个槽用下拉菜单选择或导入记录；表格左侧采用“指标组 + 子指标”两级标签，CPU 各核按组合并父标签；行高紧凑并固定表头滚动。该结构与二进制中 `rc_panel_combo_data1..5`、`rc_select_show_max/avg` 的独立控件引用一致。

可借鉴的是信息架构和交互密度：五槽并列、已载入记录复用、彩色槽与列对应、分组合并行、参数区与数据区分离。不能据此宣称已恢复 Gears 源码或完整布局算法；静态反汇编只确认控件与导出函数边界，未验证其全部绘制代码。MoTuPerf 因此自主使用 Avalonia `Expander`、`ContextMenu` 和 `Grid` 实现，并继续使用自身统计口径。

## 实现边界

MoTuPerf 使用自身 .motuperf 现场与 MetricStatistics，不读取 Gears 私有现场、不复制二进制、不模仿其小数 Jank 插值。最多五份、均值/峰值、相对秒区间和对比导出已实现；导出对应当前显示模式，而非声称与 Gears CSV 字节级相同。

对比只读，外部记录不替换主窗口现场；完整归档验证但不解压截图。按既有端点归属整个帧窗口，跨平台内存与热状态分行，缺测不补零。来源和口径保留在表格中；普通 CPU 等为真实更新样本平均，FPS 为有效观测时长加权，FrameTime 复用逐帧/旧汇总统计。

v0.49.0 继续不涉及在线发布；本地版本、帮助、最近三版日志及 CHANGELOG 保持同步。
