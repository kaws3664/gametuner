namespace GameTuner.Core.Models;

public sealed class TuningCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public QualityPreset? Preset { get; set; }
    public Dictionary<string, string> SettingOverrides { get; set; } = new(StringComparer.Ordinal);
    public string Rationale { get; set; } = "";

    public TuningCandidate Clone() =>
        new()
        {
            Id = Id,
            Label = Label,
            Preset = Preset,
            SettingOverrides = new Dictionary<string, string>(SettingOverrides, StringComparer.Ordinal),
            Rationale = Rationale
        };

    public string CacheKey
    {
        get
        {
            var preset = Preset?.ToString() ?? "None";
            var overrides = string.Join(
                ";",
                SettingOverrides.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
            return $"{preset}|{overrides}";
        }
    }
}
