namespace GameTuner.Core.Adapters.Rdr2;

public enum Rdr2SettingKind
{
    QualityLevel,
    Boolean,
    Integer,
    Decimal
}

public sealed record Rdr2SettingDefinition(
    string Key,
    string Label,
    Rdr2SettingKind Kind,
    double PerformanceCost,
    double VisualValue,
    string Group,
    IReadOnlyList<string> AllowedValues,
    string? Minimum,
    string? Maximum);

public static class Rdr2SettingsCatalog
{
    public static IReadOnlyList<Rdr2SettingDefinition> Definitions { get; } =
    [
        Quality("tessellation", "曲面细分", 2.4, 1.8, "geometry"),
        Quality("shadowQuality", "阴影质量", 3.0, 3.0, "shadows"),
        Quality("farShadowQuality", "远距离阴影", 2.6, 2.2, "shadows"),
        Quality("reflectionQuality", "反射质量", 2.4, 2.6, "reflections"),
        Quality("mirrorQuality", "镜面质量", 2.0, 2.0, "reflections"),
        Quality("ssao", "环境光遮蔽", 2.4, 2.6, "ambient"),
        Quality("textureQuality", "纹理质量", 2.3, 3.5, "textures"),
        Quality("particleQuality", "粒子质量", 2.0, 1.8, "effects"),
        Quality("waterQuality", "水面质量", 1.9, 1.8, "water"),
        Quality("volumetricsQuality", "体积效果", 3.2, 2.8, "volumetrics"),
        Quality("lightingQuality", "光照质量", 2.7, 3.0, "lighting"),
        Quality("ambientLightingQuality", "环境光质量", 2.0, 2.0, "lighting"),
        Quality("taa", "TAA 抗锯齿", 1.0, 1.5, "antiAliasing"),
        Integer("msaa", "MSAA", 2.4, 2.2, "antiAliasing", "0", "8"),
        Boolean("fxaaEnabled", "FXAA", 0.4, 0.8, "antiAliasing"),
        Quality("shadowSoftShadows", "柔和阴影", 1.5, 1.5, "shadows"),
        Quality("particleLightingQuality", "粒子光照", 1.4, 1.5, "effects"),
        Quality("waterRefractionQuality", "水面折射", 1.1, 1.2, "water"),
        Quality("waterReflectionQuality", "水面反射", 1.6, 1.9, "water"),
        Quality("waterLightingQuality", "水面光照", 1.2, 1.3, "water"),
        Quality("furDisplayQuality", "毛发质量", 1.3, 1.4, "textures"),
        Quality("shadowGrassShadows", "草地阴影", 1.4, 1.2, "shadows"),
        Quality("scatteringVolumeQuality", "体积散射", 2.4, 2.2, "volumetrics"),
        Quality("volumetricsRaymarchQuality", "体积光线步进", 2.8, 2.3, "volumetrics"),
        Quality("volumetricsLightingQuality", "体积光照", 1.8, 2.0, "volumetrics"),
        Quality("terrainShadowQuality", "地形阴影", 1.5, 1.4, "shadows"),
        Quality("decalQuality", "贴花质量", 1.2, 1.5, "effects"),
        Quality("ssdoResolution", "SSDO 分辨率", 2.0, 1.8, "ambient"),
        Quality("treeQuality", "树木质量", 2.2, 2.0, "geometry"),
        Quality("deepsurfaceQuality", "深层表面质量", 1.2, 1.5, "geometry"),
        Boolean("asyncComputeEnabled", "异步计算", 0.7, 0.3, "advanced"),
        Boolean("transferQueuesEnabled", "传输队列", 0.4, 0.2, "advanced"),
        Boolean("waterReflectionSSR", "水面屏幕空间反射", 1.8, 1.9, "water"),
        Boolean("shadowParticleShadows", "粒子阴影", 1.5, 1.2, "shadows"),
        Boolean("shadowLongShadows", "长阴影", 1.3, 1.2, "shadows"),
        Boolean("ssaoFullScreenEnabled", "全屏 SSAO", 1.3, 1.2, "ambient"),
        Boolean("treeTessellationEnabled", "树木曲面细分", 1.8, 1.6, "geometry"),
        Integer("anisotropicFiltering", "各向异性过滤", 0.6, 2.2, "textures", "1", "16"),
        Integer("waterSimulationQuality", "水面模拟", 1.0, 1.1, "water", "0", "4"),
        Integer("reflectionMSAA", "反射 MSAA", 1.6, 1.5, "reflections", "0", "8"),
        Decimal("lodScale", "全局 LOD", 3.0, 3.0, "geometry", "0.5", "2.0"),
        Decimal("grassLod", "草地 LOD", 2.4, 2.0, "geometry", "0.5", "10.0"),
        Decimal("pedLodBias", "人物 LOD", 1.8, 1.8, "geometry", "-1.0", "2.0"),
        Decimal("vehicleLodBias", "载具 LOD", 1.6, 1.5, "geometry", "-1.0", "2.0")
    ];

    public static Rdr2SettingDefinition? Find(string key) =>
        Definitions.FirstOrDefault(
            definition => string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase));

    private static Rdr2SettingDefinition Quality(
        string key,
        string label,
        double performanceCost,
        double visualValue,
        string group) =>
        new(
            key,
            label,
            Rdr2SettingKind.QualityLevel,
            performanceCost,
            visualValue,
            group,
            ["kSettingLevel_Low", "kSettingLevel_Medium", "kSettingLevel_High", "kSettingLevel_Ultra"],
            null,
            null);

    private static Rdr2SettingDefinition Boolean(
        string key,
        string label,
        double performanceCost,
        double visualValue,
        string group) =>
        new(
            key,
            label,
            Rdr2SettingKind.Boolean,
            performanceCost,
            visualValue,
            group,
            ["false", "true"],
            null,
            null);

    private static Rdr2SettingDefinition Integer(
        string key,
        string label,
        double performanceCost,
        double visualValue,
        string group,
        string minimum,
        string maximum) =>
        new(
            key,
            label,
            Rdr2SettingKind.Integer,
            performanceCost,
            visualValue,
            group,
            [],
            minimum,
            maximum);

    private static Rdr2SettingDefinition Decimal(
        string key,
        string label,
        double performanceCost,
        double visualValue,
        string group,
        string minimum,
        string maximum) =>
        new(
            key,
            label,
            Rdr2SettingKind.Decimal,
            performanceCost,
            visualValue,
            group,
            [],
            minimum,
            maximum);
}
