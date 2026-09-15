# iOS 微信小游戏性能采集工具

这是一个面向测试人员的本地性能采集工具项目。第一版目标是通过 USB 连接 iOS 设备，观察 iOS 应用和微信小游戏运行时的 FPS、Jank* 和内存实时曲线，并支持将采样数据导出到本地。

## 当前状态

项目已完成第一版 MVP：本地 Web 操作台 + Python 后端。

已完成：

- 写入 `trellis-codex-workflow` 的 `AGENTS.md` 工作流块。
- 建立 `docs/` 中文知识库。
- 整理 PerfDog、UWA Gears 和用户参考图中的 MVP 能力边界。
- 探测本机 iOS USB 基础环境：Apple Mobile Device Service 已运行，`tidevice` 和 `pyidevice` 可识别 USB 设备。
- 实现 iOS USB 真实采集、实时 FPS/Jank*/Memory 曲线、采集控制和 CSV 导出。
- 实现 iOS USB 真实采集适配器入口：检测 `pyidevice`/`tidevice`，依赖缺失时给出清晰提示。
- 实现 iOS 进程刷新和候选进程选择，优先展示 WeChat、WebContent 和 WebKit 相关进程。
- 实现 iOS USB 采集时的实时截图记录，截图保存到 `data/screenshots/` 并在页面显示最近截图。

## 第一版范围

包含：

- USB 模式。
- 目标 bundle 输入，微信默认 `com.tencent.xin`。
- iOS 进程刷新和候选进程选择，用于定位微信小游戏相关进程。
- 开始、停止、清空采集。
- 实时 FPS、Jank*、BigJank* 和内存曲线。
- 实时截图预览和截图文件记录。
- CSV 本地导出。
- 无设备或依赖缺失时的清晰提示。

不包含：

- Wi-Fi 模式。
- 多设备并发。
- 云端账号、云端报告或团队协作。
- 自动打开微信小游戏。
- CPU/GPU/网络/温度/电量等完整 PerfDog/Gears 指标。
- 游戏源码埋点。

## 文档入口

- [需求草案](docs/requirements/ios-wechat-minigame-performance-mvp.md)
- [实现草案](docs/implementation/ios-wechat-minigame-performance-mvp.md)
- [术语表](docs/CONTEXT.md)
- [待确认决策](docs/wiki/questions/open-decisions.md)
- [参考资料摘要](docs/wiki/sources/perfdog-and-gears.md)
- [本机 iOS 依赖探测](docs/wiki/sources/local-ios-dependency-probe.md)

## Trellis 工作流状态

用户提供的 `E:\download\trellis-codex-workflow.zip` 已安装为全局技能，并将其中三个脚本复制到了项目 `scripts/`：

- `scripts/resolve_trellis_version.py`
- `scripts/apply_trellis_codex_workflow.py`
- `scripts/check_trellis_codex_effective.py`

注意：该 zip 是 `trellis-codex-workflow` 技能包，不是 Trellis CLI 本体。因此当前项目还没有 `.trellis/` 初始化文件。

当前可运行检查：

```powershell
python scripts\resolve_trellis_version.py
python scripts\apply_trellis_codex_workflow.py --repo . --check
python scripts\check_trellis_codex_effective.py --repo .
```

其中 `check_trellis_codex_effective.py` 预计会报告 `.trellis/`、`.codex/hooks` 和本地 `.agents/skills/trellis-*` 缺失，这是当前已知状态。

## 启动方式

本地服务默认使用 `8770` 端口：

```powershell
.\run.ps1
```

打开：

```text
http://127.0.0.1:8770
```

如果端口冲突，可以指定：

```powershell
$env:IOS_PERF_PORT = "8780"
.\run.ps1
```

## 真实 iOS USB 采集

当前机器已检测到 Apple Mobile Device Service 正在运行，且 `tidevice`、`pyidevice` 可识别 USB 设备。

安装可选依赖并检查设备：

```powershell
python -m pip install -r requirements-ios.txt
python scripts\check_ios_usb.py
```

真实采集模式会优先使用：

1. `tidevice perf -B <bundle_id> -o fps,memory --json` 获取 FPS 和内存。
2. 如果没有 `tidevice` 但有 `pyidevice`，回退到 `pyidevice instruments display` 和 `pyidevice instruments appmonitor -b <bundle_id>`。

Jank* 说明：

- `tidevice perf` 当前主要返回 FPS 和内存，不返回 PerfDog 同口径的原生 Jank/BigJank。
- 工具会在缺少原生 Jank 字段时，用 `60 - FPS` 估算每秒缺失帧数并显示为 `Jank*`。
- `BigJank*` 在估计缺失帧数达到 15 帧及以上时记为 1。
- CSV 和表格备注会标明“Jank/BigJank 为 FPS 缺口估算值”。

测试微信小游戏时，页面会通过 `tidevice ps --json -A` 列出候选进程。PerfDog 文档建议 iOS 微信小游戏先选微信宿主进程，因此默认优先选择 `WeChat / com.tencent.xin`；如果是小程序或高性能模式小游戏，可观察刷新进程后新出现或 pid 较大的 `com.apple.WebKit.WebContent`，并用它辅助判断实际渲染进程。

截图记录：

- iOS USB 采集启动后，后端会异步调用 `tidevice screenshot`。
- 默认约每 5 秒保存一张 PNG，避免截图动作拖慢每秒性能采样。
- 文件保存到 `data/screenshots/`。
- 页面会显示最近截图，CSV 的 `screenshot_url` 字段会记录样本对应的最近截图链接。

未安装这些依赖时，界面会提示安装真实 iOS USB 采集工具。

## 验证命令

```powershell
python -m compileall backend tests
python -m unittest discover -s tests
```

本轮已验证：

- `/api/status` 返回依赖状态。
- `/api/processes` 返回 iOS 进程候选列表，并优先排序 WeChat / WebContent。
- iOS USB 模式可通过 `tidevice` 产生真实 FPS 和内存样本。
- iOS USB 模式可生成截图，并通过 `/captures/screenshots/<file>.png` 访问。
- `/api/sessions/current/export.csv` 可导出 CSV。
- Chrome 打开 `http://127.0.0.1:8770` 后，默认 bundle 为 `com.tencent.xin`，曲线非空且页面无横向溢出。
# MoTuPerf

MoTuPerf 是一个支持 Windows 与 macOS Apple Silicon 的 Android/iOS 真机性能采集工具。

本项目采用 MIT License。源码、构建流程和发布配置公开；用户现场文件、采集日志、设备信息和本地运行时数据不提交到仓库。

## 自动发布

推送 `v主版本.次版本.修订版本` 标签后，GitHub Actions 会运行测试并构建 Windows x64 安装包和 macOS arm64 DMG，随后发布 GitHub Release 与 `latest.json` 更新清单。

## 客户端更新

客户端默认异步检查 GitHub Release 更新，也可以在设置中关闭。下载前按系统和架构选择安装包，下载完成后校验 SHA-256；Windows 启动安装器，macOS 打开 DMG 供用户完成安装。更新不会覆盖现场文件、日志或数据目录。
