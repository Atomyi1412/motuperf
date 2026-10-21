# Gears 与 MoTuPerf 表格导出对比

研究日期：2026-10-20。只读研究，不修改产品、不提交、不发布。MoTuPerf 为当前 v0.46.1 工作区源码；Gears CSV 为用户历史文件，不是本轮同场采集。

## 1. 结论

两边都采用“设备信息 + Stat 统计 + Data 明细”的 CSV，并非 Gears 导出的是更高级的 Excel 工作簿。MoTuPerf 已有每秒汇总、原始明细、有效样本统计和来源追溯，不需要照抄整套格式。

最值得改进的是：**把多核 CPU、各温度传感器拆成独立数值列；补全各指标统计；提供区间导出；改善 Excel 打开和阅读体验。** 不建议为了数值一致去模仿 Gears 的小数 Jank 或把缺失数据补零。

## 2. 本轮逆向方法和证据边界

- 检查本机 `E:/Program Files/Gears/bin/UWA-Gears.exe`：原生 x64 PE，CLR 目录为 0，使用 SDL2 等组件；不能使用 .NET 反编译器直接恢复 C# 源码。
- 使用 pefile 解析 PE，提取导出相关字符串，通过 x64 RIP 相对地址引用及异常表定位函数，再用 Capstone 反汇编。未执行竞争工具采集业务、未上传二进制、未复制实现代码。
- `FPS(avg),` 的代码引用位于 RVA `0x5d1dd9`、`0x5d1df7`，所属函数起点 `0x5d1540`；导出类型控件 `##rd_export_type_combo` 的引用位于 `0x5ee4c3`，所属函数起点 `0x5ede90`。
- 相邻常量包含 `CSVDataTable.h`、`RealtimeFile.cpp`、`Min Value`、`Max Value`、`Avg Value`、`##rd_bookmark1_combo`、`##rd_bookmark2_combo`、`##rd_time1_input`、`##rd_time2_input`。这些说明存在统计和区间配置线索，**尚未通过 Gears 界面完整验证所有选项的行为**。
- 已解析用户样本 `D:/大神性能测试数据/性能测试/gears-20260706-1540.csv`，并与当前 MoTuPerf 导出源码交叉核对。旧 MoTuPerf CSV 当前不在原桌面路径，未将其冒充当前版本导出结果。
- 未完整恢复 Gears 插值函数、Stat 所有公式、FrameTime 精确口径；不声称恢复了完整源码，也不把字符串存在视为功能已实测。

指纹 SHA-256：

| 文件 | SHA-256 |
|---|---|
| Gears EXE | `E40CD7B2FF7F7D0DBFCE3BFBA54482EB510EA66EF28099816AB9B24FFEC301F7` |
| Gears CSV | `B1B34C5395007233958F7E31C58C0C3A0F90B7B66CDF06778D53CE5A737503C1` |

## 3. 逐项差异

| 项目 | Gears 证据 | MoTuPerf 当前实现 | 判断 |
|---|---|---|---|
| 文件结构 | CSV；设备信息、Stat、Data，末尾还有 Min/Max/Avg | CSV；设备信息、Stat、Data，没有末尾各列汇总 | 主体相似；我们的汇总覆盖不够完整 |
| 时间粒度 | 样本 1289 条 Data，0～1288 秒，每秒一行；文本仍保留六位小数 | 默认每秒一行且时间显示整数；另可选保留原采样事件行、六位时间的小数明细 | 默认模式已满足相近的阅读需求，不必再降时间精度 |
| 数值精度 | 明细通常六位小数；Stat 精度各异 | 明细通常六位，Stat 通常两位；逐帧数组保留往返精度 | 阅读模式可减少显示位数，原始模式保留精度 |
| 缺失值 | 样本用 `-`；后面还有空的预留列 | 数值无真实更新时大多用 `-`，温度不向后填充；附 Updated 标记 | 保留当前真实缺失策略，不为连续好看填零 |
| 秒级 Jank | 有小数：普通 Jank 233 个非整数值，BigJank 218 个；最大分别约 1.873598、1.887240 | 真实窗口计数在秒桶内求和；普通 Jank 总列含 BigJank，另有 excluding Big 列 | 两种表格不能直接把列值当成相同的“次数” |
| FrameTime | 样本只有 `FrameTime-FrameTime[ms]` 通用列；没有据此确认其是均值还是最大值 | 分开导出 Mean、Max、窗口 P95；汇总模式明确叫 WindowP95Max，Stat 可合并有效原始逐帧数据 | 我们语义更明确；不能为了对齐而改成单个含糊列 |
| CPU 核心 | CPU0～CPU7 各独立数值列，另有每核频率、频率归一化负载 | 所有核放进同一单元格，以分号分隔 | 我们不方便 Excel 筛选、作图，值得优先拆列 |
| 温度 | Stat 有 CPU/GPU/Battery 平均和最大温度字段，本样本全部缺失 | 温度传感器打包成 `名称=数值;名称=数值`；Stat 没有各温度汇总 | 我们应拆传感器列，并在可用时补充平均/最低/最高 |
| 热状态 | 当前样本没有对应列，不能推断其他版本均不支持 | 导出等级、名称、来源、范围、更新标记 | 保留；状态平均数不应解释为物理温度 |
| 内存 | PSS 多项独立列，包含 Native/Dalvik/Graphics 等，以及设备内存字段 | 首选内存口径与 RSS 分开，保留 Metric、Source；细分项较少 | 细分项需要采集能力支持，不能靠调整导出凭空新增 |
| 可追溯性 | 样本主要是指标数值，没有我们的来源质量列 | PID、平台、FPS Scope、Frame Source、真实性验证、断流、缺失窗口等 | 我们更适合定位异常，但普通用户表格过宽 |
| 区间导出 | 二进制存在书签和起止时间控件；具体菜单和边界处理未现场验证 | 当前导出 UI 只选择秒级/原始，使用整个 SessionDocument，不传图表缩放区间 | 可明确提供全部／当前查看区间，默认全部 |
| 跨记录对比 | 二进制另有比较窗口、`ExportCompareDatas` 和 `Avg,Max,` 线索 | 当前单现场导出 | 可作为后续功能，不能把静态线索等同已验证比较报表 |
| 截图 | 本样本无截图列 | CSV 只有 Screenshot-Path，不内嵌图片 | 分享 CSV 并不保证对方能访问原电脑截图；完整复现仍使用现场文件 |
| 大文件与写入 | 本轮未运行 Gears 大文件导出 | 后台写入、操作拦截、临时文件写完后替换；但仍有全量排序、秒桶及投影列表 | 已有保护；极长采集的峰值内存仍值得单独测量 |

## 4. 不能直接比较的两类统计

### 4.1 “归一化 CPU”不是同一公式

Gears 随包 `resource/metrics_en.ini:117` 明确说明：

`Current Usage * Sum of Current CPU Frequencies / Sum of Max CPU Frequencies`

每核负载说明在第 133 行，是当前负载乘以该核当前频率／最大频率。此为工具随包语义说明，本轮没有逐指令恢复公式全部细节。

MoTuPerf Android 的 `normalized_cpu_percent_from_raw` 使用 `raw_percent / online_cpus`，输出 source 也明确写 `raw_process_cpu_divided_by_online_cores`。因此两种 Normalized **衡量的含义不同**；不能当作采集不准确，也不能悄悄替换已有指标。建议导出列名和指标字典明确“按在线核数归一化”，需要频率加权时另立指标。

### 4.2 Stat 与 Data 的平均值不保证相等

Gears 样本顶部 FPS(avg)=53.23，Data 有效 FPS 的普通平均及表尾 Avg Value 为 53.260924；CPU 顶部 16.96，Data/表尾约 16.979588。两组数字确有差异，不能在未恢复算法时说成计算错误。

MoTuPerf 顶部 Stat 来自原始有效样本，并有 FPS 来源筛选、观察时长加权；秒级 Data 是另一层展示投影。对秒级行做 Excel AVERAGE 同样未必重现 Stat。这种差异应通过统计模式、样本数、有效时长和口径说明解释，不应强行把所有统计改成表格行平均。

Gears 小数 Jank 与时间重采样/插值相符，但本轮没有恢复足够的调用与数学实现来确认具体算法。MoTuPerf 不应据此给离散事件次数做线性插值。

## 5. 建议优先级

1. **P1：增加易读报告格式**。优先考虑 XLSX 分“概览、指标统计、秒级明细、指标说明”工作表；原 CSV 两种模式继续保留。所有核和温度拆列，缺失仍显示 `-`，不要强行补零。
2. **P1：补齐指标统计**。CPU raw/按核数归一化、各核、内存、各温度都有可用样本数、最小、最大、平均；FPS、逐帧和事件次数继续使用专用算法。热状态提供最高等级和有效观测分布，平均等级如保留需明确无温度单位。
3. **P1：加入导出范围**。全部数据／当前缩放区间，显示起止和时长，默认全部。区间统计从原始数据计算；跨边界帧窗口及离散事件应有明确归属规则，不按比例凭空拆 Jank。
4. **P1：指标字典和列名纠偏**。说明 CPU 归一化公式、Jank 包含关系、FrameTime 各列含义。当前 `FPS-Jank[frame/s]` 容易误读，实际导出是窗口/秒桶计数；后续应使用带版本的格式变更，兼容旧脚本。
5. **P2：Excel 兼容与安全**。当前为 UTF-8 无 BOM，部分 Windows Excel 双击打开可能乱码，可提供 BOM 或 XLSX；普通阅读减少小数，原始明细不截断。CSV 已转义逗号/引号/换行，但没有单独处理文本公式前缀；外部导入的 App 名、Note 等不可信文本需防公式注入，并对分享提供设备标识脱敏选项。
6. **P2：极长记录性能验证**。测试 4～8 小时记录的导出耗时、峰值内存、磁盘满、替换失败、关闭保护；是否引入流式秒桶和可取消进度以实测决定。

不建议：照搬频率归一化替换当前 CPU、插值 Jank、补零缺失传感器、从多个窗口 P95 推算全局 P95、把 Gears 所有预留列搬进我们的报表。

## 6. Gears 自身样本也有格式问题

本样本共 92 列，其中只有 56 个具名列，后面 36 列为空；Stat 表头和首数值含 NUL 字符，GPU/分辨率字段也有疑似拼接异常。这些仅代表本样本，不推断所有版本；说明我们应借鉴分析便利性，而不是逐字复刻文件布局。

## 7. 验证与文件

- 实际 CSV 解析：1289 条数据、逐列数值数量及平均值已复算，尾部 Min/Max/Avg 与对应数据列核对。
- 当前 MoTuPerf `CsvExportServiceTests`：8/8 通过；未改产品，无需执行发布或全量 UI 验收。
- 源码：`motuperf_cross_platform/src/MoTuPerf.Core/CsvExportService.cs`、`Shared/MetricStatistics.cs`、Desktop `ExportCsvDialogWindow.axaml`、`MainWindow.axaml.cs:573`；Android CPU 公式在 `csharp_perf_monitor/tools/android_perf_runner.py:404`。
- 本轮脚本、指纹、字符串索引、样本结构和反汇编：`.outputs/gears-export-comparison/`。保留验证边界：没有两工具同一设备同一时段采集，也没有实际点击 Gears 导出界面。
