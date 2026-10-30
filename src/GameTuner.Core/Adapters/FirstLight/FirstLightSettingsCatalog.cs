namespace GameTuner.Core.Adapters.FirstLight;

public sealed record FirstLightSettingDefinition(
    string Key,
    string Label,
    string Group,
    int Minimum,
    int Maximum,
    int PerformanceCost,
    int VisualImportance);

public static class FirstLightSettingsCatalog
{
    public static readonly IReadOnlyList<FirstLightSettingDefinition> Definitions =
    [
        new("QualityProfile", "自定义画质", "system", 5, 5, 0, 0),
        new("ShadowQuality", "阴影", "shadows", 0, 2, 8, 7),
        new("TextureResolution", "纹理分辨率", "textures", 0, 2, 4, 10),
        new("TextureFilter", "纹理过滤", "textures", 0, 4, 1, 5),
        new("MirrorQuality", "镜面", "reflections", 0, 2, 5, 5),
        new("LevelOfDetail", "细节层次", "geometry", 0, 1, 5, 6),
        new("SimulationQuality", "物理与模拟", "cpu", 0, 1, 7, 5),
        new("ReflectionQuality", "反射", "reflections", 0, 1, 8, 7),
        new("AtmosphericQuality", "大气效果", "volumetrics", 0, 1, 7, 6),
        new("SSRQuality", "屏幕空间反射", "reflections", 0, 1, 7, 6),
        new("TerrainQuality", "地形", "geometry", 0, 1, 5, 5),
        new("VolumetricFogQuality", "体积雾", "volumetrics", 0, 1, 8, 6),
        new("VolumetricEffectsQuality", "体积特效", "volumetrics", 0, 1, 8, 6),
        new("GlobalIlluminationQuality", "全局光照", "lighting", 0, 1, 9, 8),
        new("SuperSampling", "分辨率比例", "scaling", 1000, 2000, 10, 9)
    ];

    public static FirstLightSettingDefinition? Find(string key) =>
        Definitions.FirstOrDefault(item => item.Key == key);

    public static void Validate(IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            var definition = Find(pair.Key)
                ?? throw new ArgumentException($"007 设置 {pair.Key} 不在白名单中。");
            if (!uint.TryParse(pair.Value, out var value) ||
                value < definition.Minimum || value > definition.Maximum)
            {
                throw new ArgumentException($"{pair.Key} 的值 {pair.Value} 超出允许范围。");
            }
        }
    }
}
