# iOS 27 电池温度与 PerfDog 只读研究

日期：2026-10-20。样本：当前连接的 iPad mini 6 Wi-Fi（iPad14,1），系统 27.0，Build 24A5430a。该结论不能推广为所有 iOS 27 设备都无法读取温度。

## 结论

1. **本轮没有验证到这台设备可用的实时摄氏温度。** 电池诊断连接正常，返回电量、电压、电流等信息，但没有实时 `Temperature` 字段。
2. **已定位的本地 PerfDog iOS 电池温度路径与 MoTuPerf 同属 IORegistry 查询。** 其实际查询参数是 `IOPMPowerSource`，读取 `Temperature` 并除以 100；字段缺失时返回无效哨兵值，不分发有效温度。
3. **没有发现这条 PerfDog 路径能绕过本设备缺少温度字段的问题。** 本轮未启动 PerfDog 做同机采集，因此不能断言它的所有版本和其他路径均无法读取。
4. Thermal State 为系统热状态等级，和实时摄氏温度是两个指标。不能据热状态 0 推算温度，也不能将历史统计中的 0 当作当前温度。

## 现有实现及日志

- `csharp_perf_monitor/tools/pid_perf_runner.py` 的 `_ios_temperature_session` 每至少五秒调用 `DiagnosticsService.get_battery()`。
- 当前 Python 环境为 pymobiledevice3 9.33.1，`get_battery()` 实际执行 `ioregistry(ioclass="IOPMPowerSource")`。
- `temperature_metrics.py` 只解析顶层 `Temperature` 并除以 100；值缺失时返回 None。
- 最新本地日志 `.outputs/build-v0461/bin/MoTuPerf.Desktop/release/data/logs/collector-20261020-141433-416.log` 显示 collect_temperature=true，而 capture_health 的 temperature 持续为 null；同期 FPS、CPU、内存、热状态继续更新。
- 已确认一个提示缺口：接口成功但缺少字段不会触发现有异常提示，UI 可能一直显示“等待温度数据”。这不等于还在加载，也不等于设备为 0℃。

## 真机只读查询

| 查询 | 结果 |
|---|---|
| IORegistry / IOPMPowerSource | 返回电池字典；无实时 Temperature |
| IORegistry / AppleSmartBattery（EntryClass 及 EntryName） | 同样返回电池字典；无实时 Temperature |
| IORegistry / AppleARMPMU、AppleEmbeddedTempSensor | 未返回可用条目 |
| Diagnostics / GasGauge、All | 请求有响应，但没有读到可用温度值 |
| lockdown / com.apple.mobile.battery | 有电量和充电状态；没有温度 |
| IORegistry / IOService 设备树 | 能枚举温度相关节点；节点存在不表示可从主机读取温度 |
| AppleARMPMUTempSensor、AppleEmbeddedNVMeTemperatureSensor | 有 HID 服务配置；未返回实时摄氏温度 |
| AppleSMCKeysEndpoint、AppleSMCInterface、AppleSMCPMU、AppleSMCCharger | 返回设备/驱动配置；未读到实时温度 |
| AppleTemperatureStatistics、AppleSMCSensorDispatcherUserClient | 本次按类查询没有可用返回 |
| AppleSMCSensorDispatcher、AppleSmartBatteryPack/Bank/Cell | 返回驱动/电池结构；没有建立可用实时温度读取路径 |

电池对象中出现 `DeadBatteryBootData.GeneralPayload.AverageBattSkinTemp` 和 `AverageBattVirtualTemp`，本次均为 0，属于启动历史载荷，**不得当作当前设备温度**。本轮未将不明格式的二进制 BatteryData 解析为温度。

## PerfDog 本地逆向证据

样本：`E:/download/perf/PerfDog.jar`（400,928,329 字节）。SHA-256：`3C384161F7C349712DAA12A07DE7BCF89DD040CC9896195A730451F16B8A30CA`。`PerfDog.exe` 是启动入口，本次追到实际 JAR 和本地库边界。

- 采用已有 CFR 0.152 反编译选定 class；没有运行 PerfDog UI、没有调用竞品设备服务。
- `com.perfdog.core.ios.o6` 初始化 15000ms 的轮询周期，创建 `qe`。
- `qe` 调用 `Diagnostics.U`，由 `nativeQueryIORegistryEntry` 查询注册表。其解析顺序为 Status=Success → Diagnostics → IORegistry。
- 已解出该调用的字符串常量：查询参数 `IOPMPowerSource`，`UpdateTime`、`Voltage`、`InstantAmperage`、`Temperature`。
- `qe.B(Object[])` 的温度读取分支：不存在该整数字段时返回 -1；存在时进行整数除法 `/100`。后续仅在值非负时通知温度消费者。
- `Diagnostics` JNI 包装层的 `nativeMake` 第二参数为客户端标记 `perfdog`，不能把它误写成 Apple 诊断服务名称。
- 为还原混淆常量，只在隔离 classpath 执行提取的纯常量混合辅助类 `pf/i5/p7`，再用 Python DES 还原目标类字符串。未执行业务类初始化、未修改或绕过授权逻辑。
- 未证明 Native 层的所有实现细节，也未穷尽整个产品的其他温度路径；本结论仅针对已追到的 iOS 电池温度采集链路。

可复核中间产物在 `.outputs/ios27-temperature-research/`：`device-probe.json`、`device-probe-alternatives.json`、`registry-probe.json`、`registry-probe-statistics.json`、`perfdog-temperature-constants.json` 及选定类的反编译结果。设备标识和原始电池序列号未写入本报告。

## 建议及边界

1. 优先完善能力诊断：区分“等待首个结果”“接口不通”“接口成功但缺少温度字段”；缺少字段给出明确原因，保留 Thermal State。
2. 按实际能力决定是否折叠无数据温度图，不依据 iOS 版本号永久隐藏，保留低频复查和旧设备支持。
3. 若要进一步确认 PerfDog，停止当前采集后用同一台设备、同一连接方式检查其 BTemp 是否实际有值；若有值，再追查那条不同的来源。不能因为产品列出 BTemp 指标就认定这台设备必然支持。
4. 可继续研究受支持的开发者诊断通道，但目前没有实测成功的替代实时温度源；不承诺启用开发者模式、升级依赖即可读到，不使用估算值代替实测温度。

本轮未改产品代码、未提升产品版本、未打包或发布。所有设备操作均为有超时的只读查询，未停止正在进行的 MoTuPerf 采集。
