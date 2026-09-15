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

- GitHub Actions 只能在仓库创建并授权后完成远程发布；本地没有 GitHub CLI，不能凭空创建远程仓库。
- macOS 暂不做静默安装；公共分发前仍应补充 Developer ID 签名和公证。
