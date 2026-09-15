# PerfDog 与 UWA Gears 参考摘要

## 来源

- PerfDog 页面: `https://perfdog.qq.com/article_detail?id=10080&issue_id=0&plat_id=4`
- PerfDog 页面: `https://perfdog.qq.com/article_detail?id=10089&issue_id=0&plat_id=1`
- UWA Gears 使用手册: `https://public1.uwa4d.com/uwa_gears/release/doc/UWA%20Gears%E4%BD%BF%E7%94%A8%E6%89%8B%E5%86%8C.pdf`
- 用户提供参考图:
  - `docs/raw/perfdog-fps-reference.png`
  - `docs/raw/perfdog-memory-reference.png`
  - `docs/raw/perfdog-memory-notes-reference.png`

## 对本项目有用的能力点

- PerfDog 页面强调全平台性能测试、实时指标记录、非侵入、无需 ROOT/越狱、无需修改应用，并展示 FPS、Jank、CPU、Memory 等指标。
- PerfDog 的展示形态对第一版界面有参考价值：左侧指标选择，右侧按时间轴显示 FPS/Jank 和 Memory 曲线。
- UWA Gears 手册的 Realtime 模式流程包括选择设备、选择应用或进程、开始采集、停止保存、打开历史数据和 CSV 导出。
- UWA Gears 手册明确导出有“全部导出”和“区间导出”两类；第一版先做全部导出即可，区间导出可作为后续能力。
- 用户参考图里的 FPS 面板显示 FPS、Jank、BigJank 三条曲线；Memory 面板显示 PSS Total、Native Heap 等明细。第一版先做总内存曲线，保留扩展字段位置。
- 用户参考图的文字说明区分了 FootPrint、Real Memory、Virtual Memory、Available Memory、Wakeups 等指标；第一版优先展示可从 USB 后端稳定获得的 Memory/Footprint 值，并在 UI 中标注来源。

## 对第一版的取舍

- 第一版不复制 PerfDog/Gears 的完整功能，只实现 USB 连接、目标选择、实时 FPS/Jank/Memory 曲线、采集控制和本地导出。
- 第一版不做云端账号、远程设备、Wi-Fi 模式、多设备并发、深度 CPU/GPU 分析、日志分析、对比分析和自动报告。
- iOS 17 及以上设备可能需要额外隧道或依赖；第一版应在连接检查里给出明确提示，而不是静默失败。

## 采集后端候选

- `tidevice`: 官方 README 显示支持 Windows/Mac/Linux，通过 USB 与 iOS 设备通信，可用 `tidevice perf -B <bundle id>` 获取 FPS、CPU、Memory 等性能数据；README 也提示 iOS 17 需要关注新方案。
- `py-ios-device`: README 显示支持应用 Memory/CPU、FPS、FPS/Jank、网络等 instruments 数据，并给出 `pyidevice instruments display` 返回 `fps`、`jank`、`big_jank` 的示例；对本项目的 Jank 需求更贴合。

## 待验证

- Windows 上 `py-ios-device` 对当前目标 iOS 版本、微信 `com.tencent.xin` 和 FPS/Jank 数据的实际稳定性。
- 同一台设备上 FPS/Jank 数据是否可以可靠归因到正在前台运行的微信小游戏，而不是全局显示帧率。
