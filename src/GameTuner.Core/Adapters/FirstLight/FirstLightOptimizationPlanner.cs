using GameTuner.Core.Models;

namespace GameTuner.Core.Adapters.FirstLight;

public static class FirstLightOptimizationPlanner
{
    public static TuningCandidate Baseline(IReadOnlyDictionary<string, string> current) =>
        Build(current, "volumetrics", "自定义基线", "保留纹理与过滤，先试探体积效果的性能余量。", 1);

    public static TuningCandidate NextCandidate(
        IReadOnlyDictionary<string, string> current,
        PerformanceMetrics metrics,
        TuningGoal goal)
    {
        var meets = metrics.AverageFps >= goal.TargetFps && metrics.LowFps >= goal.MinimumLowFps;
        if (meets && goal.Mode != TuningMode.FrameRate)
        {
            return Upgrade(current, goal.Mode == TuningMode.Quality ? "textures" : "reflections");
        }
        var group = metrics.Bottleneck switch
        {
            BottleneckKind.Vram => "textures",
            BottleneckKind.Cpu => "cpu",
            _ => metrics.VramRatio >= 0.9 ? "textures" : "volumetrics"
        };
        var label = group switch
        {
            "textures" => "8GB 显存减压",
            "cpu" => "CPU 模拟减压",
            _ => "GPU 体积效果优化"
        };
        var reason = group switch
        {
            "textures" => $"显存峰值达到 {metrics.VramRatio:P0}，优先降低纹理占用，而不是盲目降低所有画质。",
            "cpu" => $"GPU 未吃满且 CPU 单核峰值 {metrics.MaxCpuCoreUtilization:0}%，优先降低物理与模拟负担。",
            _ => $"GPU 利用率 {metrics.GpuUtilization:0}%，降低体积效果以改善帧率，保留纹理和过滤。"
        };
        var candidate = Build(current, group, label, reason, goal.Mode == TuningMode.FrameRate ? 2 : 1);
        if (candidate.SettingOverrides.Count > 0) return candidate;
        return Build(current, "reflections", "反射精细微调", "体积效果已到保守档，继续试探反射开销。", 1);
    }

    private static TuningCandidate Upgrade(IReadOnlyDictionary<string, string> current, string group)
    {
        var definition = new[] { group, "textures", "reflections", "volumetrics", "geometry" }
            .SelectMany(name => FirstLightSettingsCatalog.Definitions
                .Where(item => item.Group == name && current.ContainsKey(item.Key)))
            .OrderBy(item => item.VisualImportance)
            .FirstOrDefault(item => int.TryParse(current[item.Key], out var value) && value < item.Maximum);
        if (definition is null || !int.TryParse(current[definition.Key], out var value))
            return new TuningCandidate { Label = "保持达标配置", Rationale = "已达到帧率目标，不再盲目降低画质。" };
        return new TuningCandidate
        {
            Label = $"帧率有富余：{definition.Label} {value}→{value + 1}",
            SettingOverrides = new Dictionary<string, string> { [definition.Key] = (value + 1).ToString() },
            Rationale = "平均帧与 1% Low 均达标，尝试恢复一项画质，而不是继续降档。"
        };
    }

    private static TuningCandidate Build(
        IReadOnlyDictionary<string, string> current,
        string group,
        string label,
        string rationale,
        int count)
    {
        var changes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QualityProfile"] = "5"
        };
        var names = new List<string>();
        foreach (var definition in FirstLightSettingsCatalog.Definitions
                     .Where(item => item.Group == group && current.ContainsKey(item.Key))
                     .OrderByDescending(item => item.PerformanceCost)
                     .ThenBy(item => item.VisualImportance)
                     .Take(count))
        {
            if (!int.TryParse(current[definition.Key], out var value)) continue;
            var step = definition.Key == "SuperSampling" ? 100 : 1;
            var lowered = Math.Max(definition.Minimum, value - step);
            if (lowered == value) continue;
            changes[definition.Key] = lowered.ToString();
            names.Add($"{definition.Label} {value}→{lowered}");
        }
        if (changes.Count == 1) changes.Clear();
        return new TuningCandidate
        {
            Preset = null,
            Label = names.Count == 0 ? label : $"{label}：{string.Join("、", names)}",
            SettingOverrides = changes,
            Rationale = rationale
        };
    }
}
