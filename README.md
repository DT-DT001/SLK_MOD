# Secret Flasher: Manaka 设备联动 Mod

这是针对 `Game/SecretFlasherManaka.exe`（Unity 2022.3.62f2 / IL2CPP）的 BepInEx 6 Mod。最终可分发目录是 `mod_out`；把其中的全部内容覆盖到游戏根目录即可使用。

## 功能

- Windows BLE 多设备扫描与并行连接。
- 役次元电击器一代、二代，役次元飞机杯/跳蛋，郊狼 3.0 电击器的独立协议适配。
- 20 项基于本游戏实际 IL2CPP 状态的可配置事件，包括生命惩罚与最终失败、场景切换、游戏道具状态/挡位、衣物露出、NPC 警戒/发现、心率、快感、精神，以及普通、随机和无人机任务。
- 每个事件可选择多台已连接设备；每台设备分别保存强度、动态强度上限、叠加量、时间、通道/马达与波形。
- 多个游戏道具按独立来源聚合，关闭一个道具不会停止其他仍在运行的道具。
- 所有强度输入在配置层、输出计算层和协议封包层三重限制：役次元电击器 0–180、郊狼 3.0 0–200、役次元飞机杯/跳蛋 0–20。
- 启动时递归加载游戏根目录 `waveforms`：郊狼使用 DG-LAB 3.x 的 `.pulse`，役次元和玩具使用 JSON。
- 内置郊狼“呼吸”和“潮汐”逐帧匹配 `协议/郊狼example.md`；测试会直接读取示例中的 `bytes` 验证输出。
- 兼容带全局头和直接从小节开始的两类官方 `.pulse`；随包附带的 5 个官方可用样本全部由实际解析器回归测试。
- F8 打开配置窗口，F12 全设备急停。窗口关闭时仅保留右上角 350 × 36 状态条。

## 设备识别

- 役次元一代：名称 `YYC-DJ`，服务 `FF30`，写入 `FF31`，通知 `FF32`
- 役次元二代：名称 `YYC-DJ-V2`，服务/特性同上
- 郊狼 3.0：名称 `47L121000`，服务 `180C`，写入 `150A`，通知 `150B`
- 役次元飞机杯/跳蛋：服务 `FF40`，写入 `FF41`，通知 `FF42`

点击“连接设备”后执行一次 10 秒主动扫描，按名称或服务 UUID 识别并连接发现的全部支持设备。游戏启动时不会自动扫描。

## 构建与测试

要求 Windows、.NET SDK，以及已通过一次游戏启动生成的 `Game/BepInEx/interop`。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建脚本会运行协议/事件逻辑自测，编译插件，并把插件、Windows BLE 运行时、波形和说明同步到 `Game` 与 `mod_out`。

## 源码结构

- `BleManager.cs` / `DeviceSession.cs`：BLE 扫描、多连接、GATT 通信、聚合与 100 ms 输出调度
- `ProtocolPackets.cs`：四类设备协议封包与强度安全边界
- `GameEventMonitor.cs`：基于本游戏真实 IL2CPP 类型的 5 Hz 状态采集
- `EventRouter.cs`：每事件、每设备、每来源的请求路由与短租约故障保护
- `ModBehaviour.cs`：低遮挡状态条、设备连接、事件配置与诊断界面
- `InputBlockPatches.cs`：F8 窗口打开时隔离玩家、相机和交互输入
- `CoyotePulseFile.cs` / `WaveformLibrary.cs`：解析 `.pulse` 小节并展开为 25 ms 郊狼采样，递归加载和校验全部波形

玩家说明见 `mod_out/SLKDeviceLink-README.zh-CN.md`。
