using GameTuner.Core.Models;

namespace GameTuner.Core.Analysis;

public static class ManualRecommendationPlanner
{
    public static ManualRecommendation Recommend(
        PerformanceMetrics metrics, TuningGoal goal, ManualQualityTier currentTier)
    {
        goal.Validate();
        var meetsAverage = metrics.AverageFps >= goal.TargetFps;
        var meetsLow = metrics.LowFps > 0 && metrics.LowFps >= goal.MinimumLowFps;
        var usableLow = metrics.Precision is TelemetryPrecision.FrameAccurate or TelemetryPrecision.NvidiaAggregate;
        if (meetsAverage && (!usableLow || metrics.LowFps <= 0 || metrics.Duration < TimeSpan.FromSeconds(5)))
            return new("平均帧率已够，先补充有效低帧日志",
                "这份日志的低帧口径或有效采集时长不足，不能认定目标已完全达成，也没有证据要求继续整体降档。",
                null,
                [new("整体画质", "先保持当前设置", "记录有代表性的 NVIDIA 性能 CSV 或逐帧日志，再确认 1% Low。")]);
        if (meetsAverage && meetsLow)
            return new("保持当前画质", "平均帧率和低帧指标均达到目标，不需要继续降画质。",
                currentTier == ManualQualityTier.Unknown ? null : currentTier,
                [new("当前画质", "保持不变", "如果想提升画质，每次只提高一项，然后重新记录日志比较。")]);

        var evidence = DescribeBottleneck(metrics);
        if (metrics.Bottleneck == BottleneckKind.Cpu)
            return new("先减轻 CPU 负担，不整套降画质",
                evidence + "降低纹理和分辨率通常不是本次的第一步；建议是待复测的试验，不是已验证结论。",
                null,
                [new("物理 / 模拟 / 人群密度", "从当前档位降低 1 档", "只调整游戏实际提供的选项；已经最低就保持。"),
                 new("纹理 / 分辨率", "暂时保持", "优先验证 CPU 侧调整，避免白白损失画质。")]);

        if (metrics.Bottleneck == BottleneckKind.Unknown)
            return new("先做单项试调，暂不指定整套档位",
                evidence + "仅凭帧率无法确定瓶颈或当前画质，不能直接认定应从高档降到中档。",
                null,
                [new("体积雾 / 体积效果", "如高于最低档，先降低 1 档再复测", "缺少硬件利用率时只做保守试验；游戏没有该项可跳过。"),
                 new("当前画质档位", "按录制日志时的实际设置填写", "档位由玩家提供，不从文件名、帧率或注册表猜测。")]);

        if (meetsAverage && !meetsLow)
            return new("平均帧率已够，先改善低帧",
                evidence + "不建议为低帧问题直接大幅降低整套画质。一次只改一项并比较相似场景。",
                null,
                [new(metrics.Bottleneck == BottleneckKind.Vram ? "纹理质量" : "体积雾 / 阴影",
                     "从当前档位降低 1 档", "这是试调方向；低帧也可能受加载、后台任务和场景影响。"),
                 new("整体画质", "先保持", "避免把平均 FPS 达标误认为低帧也达标。")]);

        var steps = goal.Mode == TuningMode.FrameRate && metrics.AverageFps < goal.TargetFps * 0.7 ? 2 : 1;
        ManualQualityTier? suggested = currentTier == ManualQualityTier.Unknown
            ? null : (ManualQualityTier)Math.Max((int)ManualQualityTier.Low, (int)currentTier - steps);
        var title = suggested is not null
            ? suggested == currentTier ? "当前已是低档，不再继续整体降档"
                : $"当前{TierLabel(currentTier)} → 建议先试{TierLabel(suggested.Value)}"
            : $"建议从当前画质降低 {steps} 档试调";
        var reduction = $"从当前档位降低 {steps} 档，最低档不再下调";
        var settings = metrics.Bottleneck == BottleneckKind.Vram
            ? new ManualSettingAdvice[]
            {
                new("纹理质量", "从当前档位降低 1 档", "日志中的显存占用接近容量上限，优先试减纹理。"),
                new("纹理过滤 / 分辨率", "暂时保持", "先验证纹理降档，不要同时更改所有选项。")
            }
            : new ManualSettingAdvice[]
            {
                new("体积雾 / 体积效果", reduction, "优先试减高开销效果；游戏没有该项可跳过。"),
                new("反射 / 阴影", "若复测仍不达标，再降低 1 档", "不要和第一项一起改，便于判断调整是否有效。"),
                new("纹理 / 纹理过滤", "暂时保持", "没有显存压力证据时，不优先牺牲纹理清晰度。")
            };
        return new(title,
            evidence + "整体预设和逐项调整是两种试验方式，选一种即可；档位名称以游戏菜单为准。"
            + "建议不代表一定达到目标，修改后请重新记录同类场景。", suggested, settings);
    }

    public static string TierLabel(ManualQualityTier tier) => tier switch
    {
        ManualQualityTier.Low => "低档",
        ManualQualityTier.Medium => "中档",
        ManualQualityTier.High => "高档",
        ManualQualityTier.Ultra => "极高档",
        _ => "未知 / 自定义"
    };

    public static string DescribeBottleneck(PerformanceMetrics metrics) => metrics.Bottleneck switch
    {
        BottleneckKind.Gpu => $"GPU 平均利用率 {metrics.GpuUtilization:0}%：疑似 GPU 受限。",
        BottleneckKind.Cpu => $"CPU 平均利用率 {metrics.CpuUtilization:0}%、GPU {metrics.GpuUtilization:0}%：疑似 CPU 受限。",
        BottleneckKind.Vram => $"显存峰值占容量 {metrics.VramRatio:P0}：存在显存压力。",
        BottleneckKind.None => "利用率数据未显示明确的单项瓶颈，降档只作为待验证的试验。",
        _ => "日志缺少足够的硬件利用率数据，暂时无法判断瓶颈。"
    };
}
