# MVP 编码就绪清单

Status: implemented
Related requirements: [requirements/ios-wechat-minigame-performance-mvp.md](../requirements/ios-wechat-minigame-performance-mvp.md)
Related implementation: [implementation/ios-wechat-minigame-performance-mvp.md](./ios-wechat-minigame-performance-mvp.md)

## 进入编码前必须满足

- [x] 用户确认第一版微信小游戏性能按 `com.tencent.xin` 微信宿主进程和系统可见 FPS/Jank/Memory 采集。
- [x] 用户确认第一版采用“本地 Web 操作台 + Python 后端”。
- [x] 将需求文档 `Status: draft` 改为 `Status: confirmed`。
- [x] 将实现文档 `Status: draft` 改为 `Status: confirmed`。
- [x] 将 [待确认决策](../wiki/questions/open-decisions.md) 改为已确认状态。

## 确认后的最小编码顺序

1. 创建后端基础结构。
   - 文件: `backend/`
   - 验证: `python -m compileall backend`

2. 实现采样数据模型和会话状态。
   - 文件: `backend/models.py`
   - 验证: 单独构造样本并序列化为 JSON/CSV 字段。

3. 实现模拟采集器。
   - 文件: `backend/collectors/mock.py`
   - 验证: 开始采集后每秒生成 FPS、Jank、BigJank、Memory 样本，`source=mock`。

4. 实现真实采集器接口和依赖探测。
   - 文件: `backend/collectors/ios.py`
   - 验证: 本机缺少 `tidevice`、`pyidevice` 时返回清晰错误，不启动伪真实采集。

5. 实现本地 HTTP API。
   - 文件: `backend/server.py`
   - 验证:
     - `GET /api/status`
     - `POST /api/sessions/start`
     - `POST /api/sessions/stop`
     - `GET /api/sessions/current/samples`
     - `GET /api/sessions/current/export.csv`

6. 实现前端操作台。
   - 文件: `frontend/`
   - 验证: 浏览器打开首页，默认 bundle 为 `com.tencent.xin`，模拟模式有明显标识。

7. 实现实时曲线和导出路径。
   - 文件: `frontend/app.js`
   - 验证: 开始采集后 FPS/Jank/Memory 曲线持续追加；停止后可导出 CSV。

8. 补充启动脚本和 README。
   - 文件: `run.ps1`、`requirements.txt`、`README.md`
   - 验证: 按 README 能启动本地服务并打开操作台。

## 最小 API 契约

### `GET /api/status`

返回：

```json
{
  "running": false,
  "mode": "mock",
  "bundle_id": "com.tencent.xin",
  "dependencies": {
    "apple_mobile_device_service": "running",
    "pyidevice": "missing",
    "tidevice": "missing"
  },
  "warnings": []
}
```

### `POST /api/sessions/start`

请求：

```json
{
  "bundle_id": "com.tencent.xin",
  "mode": "mock"
}
```

返回当前会话状态。

### `GET /api/sessions/current/samples`

返回：

```json
{
  "samples": [
    {
      "timestamp": "2026-06-30T15:30:00+08:00",
      "elapsed_sec": 0.0,
      "fps": 58.7,
      "jank": 0,
      "big_jank": 0,
      "memory_mb": 812.4,
      "source": "mock",
      "note": "模拟数据"
    }
  ]
}
```

### `GET /api/sessions/current/export.csv`

CSV 字段：

```text
timestamp,elapsed_sec,fps,jank,big_jank,memory_mb,source,note
```

## UI 验收要点

- 第一屏是操作台，不做营销落地页。
- 顶部展示连接/模式/目标 bundle/采集时长。
- 左侧或上方提供开始、停止、清空、导出。
- FPS 图显示 FPS、Jank、BigJank 三条曲线或柱线。
- Memory 图单独显示内存 MB 曲线。
- 模拟模式必须醒目标识，不能让用户误以为已经接入真实 iOS 设备。
- 依赖缺失提示要包含下一步建议，例如安装 `py-ios-device` 或 `tidevice`。

## 确认记录

用户已在 2026-06-30 回复“确认”，编码门禁已解除。
