# iOS 微信小游戏性能采集 MVP 实现

Status: implemented
Related requirements: [requirements/ios-wechat-minigame-performance-mvp.md](../requirements/ios-wechat-minigame-performance-mvp.md)

## 实现目标

实现一个第一版本地性能采集软件：提供操作台界面，支持 USB iOS 采集入口、实时 FPS/Jank/Memory 曲线和 CSV 导出。真实采集后端优先接入 `py-ios-device` 或 `tidevice`，同时保留明显标识的模拟模式用于无设备环境验证。

## 现状依据

- 当前仓库除 `AGENTS.md` 和 `docs/` 外无应用代码。
- 用户要求使用 `grill-docs-code` 和 `trellis-codex-workflow`；已写入 `AGENTS.md` 工作流块，并将技能包内 `resolve/apply/check` 脚本复制到项目 `scripts/`。该技能包不是 Trellis CLI 本体，`.trellis/` 尚未初始化。
- `py-ios-device` README 显示可获取应用 Memory/CPU、FPS、FPS/Jank 数据，其中 `display` 示例包含 `fps`、`jank`、`big_jank`。
- `tidevice` README 显示可通过 `tidevice perf -B <bundle id>` 获取 FPS、CPU、Memory 数据，并支持 Windows。
- UWA Gears 手册的 Realtime 模式包含设备/应用选择、开始采集、保存和 CSV 导出流程。
- 本机 Apple Mobile Device Service 已运行，但 `tidevice`、`pyidevice`、`pymobiledevice3`、`idevice_id` 和 `ideviceinfo` 均未安装。

## 方案

第一版采用本地 Web 操作台，而不是一开始做复杂桌面客户端打包。

理由：

- 当前仓库为空，本地 Web 应用实现最快，用户可直接在浏览器使用。
- 后端可以用 Python 调用 iOS USB 采集 CLI 或 Python 库，后续再封装为 Windows 可执行文件。
- 前端用 Canvas 绘制实时曲线，不依赖复杂图表库，便于在当前环境离线开发和验证。
- 模拟采集和真实采集共用同一份数据模型，便于无设备环境先验证 UI、导出和采集状态。

## 影响范围

- `backend/`: Python 本地服务、采集后端、CSV/JSON 导出。
- `frontend/`: 本地操作台页面、实时曲线、状态和导出按钮。
- `requirements.txt`: Python 依赖。
- `README.md`: 安装、运行、真实设备连接和限制说明。
- `docs/`: 需求、实现、来源和术语文档。

## 不改的内容

- 不创建云端服务、数据库或账号体系。
- 不做多设备并发架构。
- 不做完整 PerfDog/Gears 克隆。
- 不做 iOS 自动化脚本控制微信小游戏。

## 数据和接口

- 采样数据结构:
  - `timestamp`: ISO 时间。
  - `elapsed_sec`: 相对秒数。
  - `fps`: 当前 FPS。
  - `jank`: 当前或累计 Jank 值，按后端返回语义标注。
  - `big_jank`: 当前或累计 BigJank 值，按后端返回语义标注。
  - `memory_mb`: 内存 MB。
  - `source`: `mock`、`py-ios-device` 或 `tidevice`。
  - `note`: 后端提示或异常摘要。
- HTTP 接口:
  - `GET /api/status`: 当前采集状态和依赖检查。
  - `POST /api/sessions/start`: 开始采集，参数包含 bundle、mode。
  - `POST /api/sessions/stop`: 停止采集。
  - `GET /api/sessions/current/samples`: 获取当前采样。
  - `GET /api/sessions/current/export.csv`: 导出 CSV。
  - `GET /api/sessions/current/export.json`: 导出 JSON，可选。

## 编码步骤

1. 创建 Python 本地服务和采样模型 -> verify: 服务能启动，`/api/status` 返回 JSON。
2. 实现模拟采集器 -> verify: 开始后每秒产生 FPS/Jank/Memory 样本。
3. 实现前端操作台 -> verify: 浏览器中开始/停止后曲线实时刷新。
4. 实现 CSV/JSON 导出 -> verify: 停止后能下载包含采样明细的文件。
5. 增加真实采集器适配层 -> verify: 无依赖时显示清晰提示；有依赖时能调用后端命令并解析样本。
6. 写 README 和运行检查 -> verify: 按 README 命令能启动本地工具。

## 验证计划

- `python -m compileall backend`
- 启动本地服务并调用 `/api/status`
- 浏览器打开操作台，使用模拟模式采集至少 10 秒
- 检查 FPS/Jank/Memory 曲线非空且持续刷新
- 导出 CSV 并检查字段和行数
- 如本机接入 iOS 设备，再测试真实后端；当前无设备时不把真实采集标记为完成

## 风险和回滚

- 风险: Windows 上 iOS USB 采集依赖不完整。发现方式: `/api/status` 和开始采集错误。回滚: 使用模拟模式验证 UI，并在 README 标注真实采集依赖。
- 风险: iOS 17+ 采集链路和旧版不同。发现方式: 后端命令失败或无数据。回滚: 在真实采集器中保留 `py-ios-device`/`tidevice` 可替换接口。
- 风险: Jank 语义因后端不同而不一致。发现方式: 样本字段来源不同。回滚: UI 标注数据来源，避免跨后端强行比较。

## 已确认决策

- 第一版采用“本地 Web 操作台 + Python 后端”的实现路线，后续稳定后再考虑原生 Windows 桌面封装或 exe 打包。

## 实现结果

- `backend/`: Python 标准库 HTTP 服务、会话管理、模拟采集器、iOS USB 依赖探测和真实采集适配器。
- `frontend/`: 本地 Web 操作台、实时指标卡、FPS/Jank/BigJank Canvas 曲线、Memory Canvas 曲线和采样表。
- `run.ps1`: 默认以 `8770` 端口启动本地服务。
- `requirements-ios.txt` 和 `scripts/check_ios_usb.py`: 真实 iOS USB 采集的可选依赖和本机检查入口。
- `tests/`: 覆盖模拟采样、CSV 表头、`pyidevice`/`tidevice` 输出解析。

## 验证结果

- `python -m compileall backend tests`: 通过。
- `python -m unittest discover -s tests`: 5 个测试通过。
- HTTP 验证：模拟模式开始后产生 3 条样本，CSV 导出 4 行（含表头），表头为 `timestamp,elapsed_sec,fps,jank,big_jank,memory_mb,source,note`。
- 浏览器验证：Chrome 打开 `http://127.0.0.1:8770`，默认 bundle 为 `com.tencent.xin`，点击开始后 FPS/Jank 和 Memory 曲线非空，页面无横向溢出。
- 移动视口验证：390px 宽度下页面无横向溢出，CSV 下载按钮真实点击可下载 `ios-performance-samples.csv`。
- iOS USB 缺依赖验证：当前机器缺少 `pyidevice`/`tidevice` 时，`ios` 模式返回“真实 iOS USB 采集依赖未安装”提示。
