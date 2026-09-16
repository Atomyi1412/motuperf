# GitHub 自动发布与客户端更新实现方案

## 状态

confirmed

## 实现范围

### 第一期：自动构建与 Release

1. 增加 Pull Request/主分支检查工作流，执行 Python 测试、C# 测试和 Release 构建。
2. 增加版本标签发布工作流，使用 Windows runner 构建 NSIS 安装包，使用 macOS arm64 runner 构建 DMG。
3. 将现有打包脚本改为支持 CI 输出目录和版本变量，避免依赖开发机固定目录。
4. 从最终产物计算 SHA-256，生成 `latest.json`，再创建 GitHub Release 并上传所有资产。
5. 发布前检查标签版本、项目版本、包清单版本、资产命名和清单版本一致。

### 第二期：客户端更新

1. 在 `MoTuPerf.Platform` 增加公开 Release 清单客户端，固定访问 `Atomyi1412/motuperf` 的 `releases/latest/download/latest.json`。
2. 清单模型只接受三段数字稳定版本，按当前操作系统和架构选择资产。
3. 在 `MoTuPerf.Desktop` 增加更新检查设置、异步启动检查、更新提示窗口和下载进度。
4. 更新设置保存到现有数据目录配置体系；默认开启，网络失败不影响主界面。
5. 下载使用临时文件，完成后校验 SHA-256；校验成功后 Windows 启动 EXE，macOS 打开 DMG。
6. 采集、文件操作、数据迁移中不启动安装；当前应用关闭后由安装器完成替换。

## 代码边界

- 发布流程放在 `.github/workflows/` 和 `scripts/`，不把 CI 逻辑写进采集器。
- 版本解析、清单校验和下载放在 Platform，UI 只负责状态和交互。
- 更新器不读取、不上传现场文件、设备信息、日志或任何用户数据。
- GitHub API 失败、下载失败和哈希不匹配均作为可恢复错误处理。
- 不把 GitHub Token、签名私钥或其他秘密写入仓库、清单或客户端。

## 版本与清单格式

```json
{
  "schema": 1,
  "version": "0.22.0",
  "mandatory": false,
  "releaseNotesUrl": "https://github.com/Atomyi1412/motuperf/releases/tag/v0.22.0",
  "publishedAtUtc": "2026-09-15T00:00:00Z",
  "assets": {
    "win-x64": {
      "fileName": "MoTuPerf-Setup-v0.22.0.exe",
      "url": "https://github.com/Atomyi1412/motuperf/releases/download/v0.22.0/MoTuPerf-Setup-v0.22.0.exe",
      "sha256": "..."
    },
    "osx-arm64": {
      "fileName": "MoTuPerf-v0.22.0-osx-arm64.dmg",
      "url": "https://github.com/Atomyi1412/motuperf/releases/download/v0.22.0/MoTuPerf-v0.22.0-osx-arm64.dmg",
      "sha256": "..."
    }
  }
}
```

## 验证方案

- 单元测试覆盖版本比较、清单格式、平台选择、非法 URL、哈希失败、取消下载和临时文件清理。
- CI 在 Pull Request 上验证测试和构建；标签发布验证最终安装包和 `latest.json`。
- Windows 本机验证安装包启动和更新器进程交接；macOS 需要在 Apple Silicon runner 或真机验证 DMG 打开。
- 无网络、旧版本、关闭更新开关、采集中点击更新、下载中取消和更新包损坏都必须保持旧版本可用。

## 当前限制

- GitHub Actions 已接入 `Atomyi1412/motuperf`，三段版本标签触发双平台构建；只有两种安装包都通过构建后才发布清单。
- macOS 暂不做静默安装；公共分发前仍应补充 Developer ID 签名和公证。

## Windows 升级目录（v0.22.1 起）

- 客户端使用当前 `AppContext.BaseDirectory`，通过 NSIS 最后一个未加引号的 `/D=完整目录` 传入安装器，支持中文和空格；不使用工作目录或下载目录作为安装目录。
- 对未传 `/D` 的旧客户端，安装器只读检测进程父链并验证包标记，优先发起更新的软件；无法建立父链时仅采用唯一有效运行目录。多实例不任意选择，探测失败仍保留注册表/默认目录供用户修改。
- 明确指定 `/D` 时不运行推断；目录页仍允许浏览修改。升级覆盖程序文件，不删除或搬移 `data`。
- 安装回归使用 `/TESTMODE /D=隔离目录`，禁止测试改写正式快捷方式、卸载记录或覆盖正式数据。仅下载哈希通过不足以证明安装目录正确。

## 更新日志发布合同（v0.22.0 起）

- 每次发布先将真实改动写入根目录 `CHANGELOG.md`，最新版本排在顶部，并标注日期。
- 主工具栏“帮助”后固定提供“更新日志”按钮；`ChangelogWindow` 离线展示最新三个版本。帮助窗口不再承担更新日志入口。
- 同步修改 `ChangelogWindow.axaml` 中的版本、日期和逐条内容。`BundledChangelogMatchesLatestThreeReleaseNotes` 对比 CHANGELOG，遗漏同步会阻止 CI/Release 测试通过。
- 飞书完整日志继续维护于 https://more2.feishu.cn/docx/EJ6Hdr6lbokuUsxr8FecLA7Qneg 。发布工作必须读取当前 block ID，在版本列表顶部局部插入，保留历史及标题自动编号，并回读核对版本顺序和内容。飞书凭据不进入客户端或仓库。
- 日志窗口使用共享二级框主题；顶部关闭列为 46px，正文可纵向滚动，底部完整日志入口固定可见。
- 发布验收分开记录：测试/构建通过、公开资产与清单哈希匹配、旧版发现更新、下载校验和安装器交接、安装后实际运行版本。构建成功不能替代后四项。
