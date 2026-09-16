# 知识库日志

- 2026-09-16: GitHub 设备授权完成，已创建公开仓库 `Atomyi1412/motuperf` 并推送 `master` 与 `v0.21.7`；首次 CI/Release 暴露缺少 Pillow、iOS 测试依赖和 Avalonia 名称生成器字段冲突，修复后依次提升为 `v0.21.8`、`v0.21.9`，待重新发布并回读真实 Release 资产。
- 2026-09-15: 完成 GitHub 公共仓库两期自动发布与客户端更新实现，版本提升至 `v0.21.7`；补充 CHANGELOG、MIT License、CI/Release 工作流、平台清单校验和公开仓库过滤规则，远程创建与 Release 回读待 GitHub 设备授权完成。
- 2026-09-15: 用户确认采用 GitHub 公共仓库 `Atomyi1412/motuperf`，macOS 更新采用下载并打开 DMG 的方式；需求和实现文档已确认，进入两期开发。
- 2026-09-15: 根据用户确认，新增 GitHub 公共仓库 `Atomyi1412/motuperf` 的自动发布与客户端更新需求草案；范围包含 GitHub Actions、Windows/macOS 安装包、公开 Release 清单、SHA-256 校验和客户端更新设置，待确认 macOS 安装动作后进入实现设计。

- 2026-06-30: 创建项目知识库，纳入 PerfDog 页面、UWA Gears 手册、用户提供的 FPS/Memory 参考图，形成 iOS 微信小游戏性能采集 MVP 的需求和实现草案。
- 2026-06-30: 尝试安装 Trellis CLI：`npm install -g @mindfoldhq/trellis` 超过两分钟无输出后停止。已用 `trellis-codex-workflow` 的脚本写入 `AGENTS.md` 工作流块，但 `.trellis/` 尚未初始化。
- 2026-06-30: 增加 `docs/AGENTS.md` 和 `docs/wiki/questions/open-decisions.md`，将进入编码前的两个关键确认点合并为“确认即可继续”的决策清单。
- 2026-06-30: 用户指出 Trellis 在 `E:\download\trellis-codex-workflow.zip`；已确认该文件是 `trellis-codex-workflow` 技能包而非 Trellis CLI 本体。已把技能包内 `resolve/apply/check` 三个脚本复制到项目 `scripts/`。
- 2026-06-30: 探测本机 iOS 采集依赖：Apple Mobile Device Service 已运行，但 `tidevice`、`pyidevice`、`pymobiledevice3`、`idevice_id`、`ideviceinfo` 均未安装。
- 2026-06-30: 增加 `README.md` 和 `.gitignore`，记录当前项目状态、第一版范围、Trellis 工作流状态和后续实现建议。
- 2026-06-30: 增加 `docs/implementation/mvp-coding-readiness.md`，将确认后的编码顺序、API 契约和 UI 验收要点收敛为可执行清单。
- 2026-06-30: 用户回复“确认”，接受 `com.tencent.xin` 微信宿主采集边界和“本地 Web 操作台 + Python 后端”实现形态；需求和实现文档状态改为 confirmed。
- 2026-06-30: 完成第一版 MVP，实现 Python 后端、本地 Web 操作台、模拟采集、iOS USB 适配器入口、CSV 导出和单元测试；通过编译、测试、HTTP 和 Chrome 页面验证。
- 2026-06-30: 补充移动视口和真实下载点击验证；390px 宽度无横向溢出，CSV 下载文件名为 `ios-performance-samples.csv`。
- 2026-06-30: 增加 `requirements-ios.txt` 和 `scripts/check_ios_usb.py`，用于安装和检查真实 iOS USB 采集依赖。
- 2026-08-21: 在 `dist/backups/MoTuPerf-v0.4.65-baseline/` 固化 WPF v0.4.65 源码与安装包，记录并回读校验版本、关键文件和 SHA-256；创建跨平台桌面版需求草案，待确认 macOS 首期 CPU 架构范围后进入技术设计。
- 2026-08-21: 用户确认跨平台桌面版首期只完成 Apple Silicon `osx-arm64`，Intel Mac 不进入首期发布与验收；需求状态改为 confirmed。
- 2026-08-21: 完成跨平台桌面版技术设计和实施计划，采用 `.NET 8 + Avalonia`，按共享核心、Windows 验收、Apple Silicon 真机验收分阶段推进；实现文档状态为 confirmed。
- 2026-08-28: 读取飞书“魔兔测试设备”IOS 有效设备视图，形成“自研游戏 + 1 款竞品”的 iOS 内部热表现正式对比草案；采用四台核心设备、多人按设备并行、新手 60 分钟 + 严格场景三轮、AB/BA 反平衡，并明确 MoTuPerf Battery Temperature、USB 充电和无表面测温边界。
- 2026-08-28: 创建飞书云文档“iOS 微信小游戏内部热表现对比测试方案（会议讨论稿）”，回读确认设备矩阵、执行门禁、数据口径、无效样本、判定规则和会议清单完整写入。
- 2026-08-28: 根据会议确认，将测试对象名称统一为自研游戏“大神”和竞品“时光杂货铺”；同步更新本地需求/设计/执行材料及飞书会议讨论稿，线上文档回读至 revision 37，确认无旧占位名称残留。
- 2026-08-28: 根据会议确认，取消独立严格场景，仅保留“大神”和“时光杂货铺”各 1 次 90 分钟连续综合场景（0～60 分钟新手段、60～90 分钟复杂段）；同步更新本地与飞书文档，线上回读至 revision 67。
