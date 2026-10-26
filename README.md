# FrameSmith

面向 007 First Light 的 Windows 个性化画质调优工具。不是 NVIDIA 官方预设切换器。

每轮采集可选 3 / 5 / 10 / 15 分钟，默认 5 分钟。3 分钟适合快速比较，10/15 分钟更适合场景变化大的游戏；不建议低于 3 分钟，以免把偶然卡顿误判为配置差异。

## 工作方式

1. 读取当前自定义图形设置并备份，不先套官方预设。
2. 通过 NVIDIA MCP 启动游戏，无限等待 `007FirstLight.exe`；游戏启动慢不会超时。
3. 用户使用 NVIDIA 浮层的“记录统计数据”功能，实际游玩约 5 分钟，无需手动停止记录。
4. 工具持续读取本轮在 `%LOCALAPPDATA%\nvlog` 新生成的 CSV/TXT，即使文件仍在写入也可读取；满 300 秒立即锁定该轮数据并结合 GPU、显存、CPU 采样分析瓶颈。
5. 到 5 分钟时自动分析并提醒；退出游戏由用户手动完成，工具不结束进程。退出后由用户确认是否应用建议。
6. 最多三轮比较，第三轮复测当前最佳候选；最终应用前再次询问。

## 个性化规则

- NVIDIA 能提供的启动、支持信息和预设能力优先；预设不是最终结果。
- NVIDIA 无法逐项控制时，修改 `HKCU\Software\IO Interactive\007 First Light` 的图形 DWORD 白名单。
- 显存压力优先纹理；GPU 压力优先体积效果与反射；CPU 瓶颈优先物理/模拟。
- 纹理过滤等低耗高观感设置优先保留。达标后可以恢复单项画质，不是一键最低画质。
- `DLSSQuality` 暂不参与写入，因为本机数字到实际档位的映射尚未验证。
- 不修改存档、不注入游戏、不写安装目录。原始备份保存在 `%LOCALAPPDATA%\GameTuner\backups`。

## 日志要求

默认目录：`C:\Users\Lycoris\AppData\Local\nvlog`。已按本机 `NVIDIA_App_Performance_Log_*.csv` 实测样本支持 `FPS` 与 `FPS 1(%) Low`；空白的 `NVIDIA_App_Latency_Log_*.csv` 会被忽略。若存在 `MsBetweenPresents` 逐帧数据，则计算平均 FPS、P99 帧耗时及最慢 1% 帧的平均 FPS。

NVIDIA 当前日志是约 0.5 秒一条的分段统计，不是逐帧记录。分段 FPS 按时间加权求平均，1% Low 为 NVIDIA 各分段上报值的时间加权平均，界面明确标为“分段统计”，不冒充逐帧精确值。一次有效轮次必须覆盖至少 300 秒，不足时会要求重新采集。

平均 FPS 必须达到目标，且 1% Low 不低于目标的 90%。菜单和加载画面不应计入有代表性的游玩采集。暂无真实日志时不会伪造帧率或给出已验证结论。

## 运行

需要 Windows、.NET 9、NVIDIA App 和已启用的游戏内浮层。先运行一次 007 并保存图形设置。

```powershell
dotnet restore GameTuner.sln --configfile NuGet.Config
dotnet run --project src/GameTuner.App/GameTuner.App.csproj
```

界面中的“应用个性化建议”和“跳过 / 保留当前”在每轮退出后才会启用。恢复原始设置同样需要用户先退出游戏。

## 验证

```powershell
dotnet restore GameTuner.sln --configfile NuGet.Config
dotnet build GameTuner.sln --no-restore
dotnet test GameTuner.sln --no-build --no-restore
dotnet build GameTuner.sln -c Release --no-restore
```

测试包括日志解析、未知键/越界拒绝、瓶颈到参数组的映射、旧 RDR2 单元测试与 NVIDIA 真实集成检查。真实 5 分钟三轮验收需要用户手动录制日志和退出游戏。

## 当前限制

- 当前仅适配 007 First Light；旧 RDR2 文件保留作参考，不进入主界面运行链路。
- NVIDIA MCP 不提供“开始/停止性能日志”接口，所以记录操作由用户在浮层完成。
- 已用本机 NVIDIA 性能日志确认格式；本机首份样本为 248.039 秒，少于完整轮次要求的 300 秒。
- 没有自动 UI 点击、自动关游戏、自动应用设置。
