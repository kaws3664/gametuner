using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Models;
using GameTuner.Core.Profiles;

namespace GameTuner.Core.Adapters.Rdr2;

public sealed record Rdr2ConfigSnapshot(
    string FullPath,
    string RawContent,
    string Hash,
    IReadOnlyDictionary<string, string> Settings);

public interface IRdr2ConfigAdapter
{
    Task<Rdr2ConfigSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    Task<string> BackupAsync(string label, CancellationToken cancellationToken = default);
    Task<Rdr2ConfigSnapshot> ApplyOverridesAsync(
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken cancellationToken = default);
    Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
    TuningCandidate CreateFallbackCandidate(
        IReadOnlyDictionary<string, string> currentSettings,
        PerformanceMetrics metrics,
        TuningGoal goal);
}

public sealed class Rdr2ConfigAdapter(
    AppPaths paths,
    IProcessGuard processGuard) : IRdr2ConfigAdapter, IGameProfileAdapter
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<Rdr2ConfigSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var content = await File.ReadAllTextAsync(paths.Rdr2ConfigFile, Encoding.UTF8, cancellationToken);
        var document = LoadXml(content);
        return new Rdr2ConfigSnapshot(
            paths.Rdr2ConfigFile,
            content,
            HashUtility.Sha256Text(content),
            ReadSettings(document));
    }

    public async Task<string> BackupAsync(string label, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var source = paths.Rdr2ConfigFile;
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("未找到 RDR2 的 system.xml。请至少启动一次游戏。", source);
        }

        var safeLabel = string.Concat(label.Where(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'));
        var target = Path.Combine(
            paths.Backups,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeLabel}-{HashUtility.Sha256File(source)[..8]}.xml");
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            target,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        return target;
    }

    public async Task<Rdr2ConfigSnapshot> ApplyOverridesAsync(
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken cancellationToken = default)
    {
        if (overrides.Count == 0)
        {
            return await ReadAsync(cancellationToken);
        }

        ValidateOverrides(overrides);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (processGuard.IsRdr2Running())
            {
                throw new InvalidOperationException("RDR2 正在运行，禁止修改 system.xml。请先退出游戏。");
            }

            var original = await File.ReadAllTextAsync(
                paths.Rdr2ConfigFile,
                Encoding.UTF8,
                cancellationToken);
            LoadXml(original);
            var rewritten = original;
            foreach (var pair in overrides)
            {
                rewritten = SetValueInRaw(rewritten, pair.Key, pair.Value);
            }

            await WriteAtomicAsync(paths.Rdr2ConfigFile, rewritten, cancellationToken);
            var verify = LoadXml(rewritten);
            var settings = ReadSettings(verify);
            foreach (var pair in overrides)
            {
                if (!settings.TryGetValue(pair.Key, out var actual) || actual != pair.Value)
                {
                    throw new InvalidOperationException($"写入后校验失败：{pair.Key} 期望 {pair.Value}，实际 {actual}。");
                }
            }

            return new Rdr2ConfigSnapshot(
                paths.Rdr2ConfigFile,
                rewritten,
                HashUtility.Sha256Text(rewritten),
                settings);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (processGuard.IsRdr2Running())
            {
                throw new InvalidOperationException("RDR2 正在运行，禁止恢复配置。请先退出游戏。");
            }

            if (!File.Exists(backupPath))
            {
                throw new FileNotFoundException("备份文件不存在。", backupPath);
            }

            var content = await File.ReadAllTextAsync(backupPath, Encoding.UTF8, cancellationToken);
            ValidateDocument(LoadXml(content));
            await WriteAtomicAsync(paths.Rdr2ConfigFile, content, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public TuningCandidate CreateFallbackCandidate(
        IReadOnlyDictionary<string, string> currentSettings,
        PerformanceMetrics metrics,
        TuningGoal goal)
    {
        var targetGroup = SelectTargetGroup(metrics);
        var selected = Rdr2SettingsCatalog.Definitions
            .Where(definition =>
                string.Equals(definition.Group, targetGroup, StringComparison.Ordinal) &&
                currentSettings.ContainsKey(definition.Key))
            .OrderByDescending(definition => definition.PerformanceCost)
            .ThenBy(definition => definition.VisualValue)
            .Take(4)
            .ToArray();

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in selected)
        {
            if (TryDowngrade(definition, currentSettings[definition.Key], out var lowered))
            {
                overrides[definition.Key] = lowered;
            }
        }

        if (overrides.Count == 0)
        {
            var fallback = Rdr2SettingsCatalog.Definitions
                .Where(definition => currentSettings.ContainsKey(definition.Key))
                .OrderByDescending(definition => definition.PerformanceCost)
                .FirstOrDefault();
            if (fallback is not null &&
                TryDowngrade(fallback, currentSettings[fallback.Key], out var lowered))
            {
                overrides[fallback.Key] = lowered;
            }
        }

        return new TuningCandidate
        {
            Label = overrides.Count == 0
                ? "RDR2 精细微调"
                : $"RDR2 精细微调：{string.Join("、", overrides.Keys.Select(
                    key => Rdr2SettingsCatalog.Find(key)?.Label ?? key))}",
            SettingOverrides = overrides,
            Rationale = BuildFallbackRationale(targetGroup, metrics, goal, overrides.Count)
        };
    }

    GameProfile IGameProfileAdapter.Profile => GameProfiles.RedDeadRedemption2;

    async Task<GameConfigSnapshot> IGameProfileAdapter.ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadAsync(cancellationToken);
        return new GameConfigSnapshot(snapshot.FullPath, snapshot.RawContent, snapshot.Hash, snapshot.Settings);
    }

    async Task<GameConfigSnapshot> IGameProfileAdapter.ApplyOverridesAsync(
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken cancellationToken)
    {
        var snapshot = await ApplyOverridesAsync(overrides, cancellationToken);
        return new GameConfigSnapshot(snapshot.FullPath, snapshot.RawContent, snapshot.Hash, snapshot.Settings);
    }

    private static string SelectTargetGroup(PerformanceMetrics metrics)
    {
        if (metrics.VramRatio >= 0.9)
        {
            return "textures";
        }

        if (metrics.GpuUtilization >= 90)
        {
            return "volumetrics";
        }

        return "shadows";
    }

    private static bool TryDowngrade(
        Rdr2SettingDefinition definition,
        string current,
        out string lowered)
    {
        lowered = current;
        switch (definition.Kind)
        {
            case Rdr2SettingKind.QualityLevel:
            {
                var index = definition.AllowedValues
                    .Select((value, position) => (value, position))
                    .FirstOrDefault(pair =>
                        string.Equals(pair.value, current, StringComparison.OrdinalIgnoreCase))
                    .position;
                if (index <= 0)
                {
                    return false;
                }

                lowered = definition.AllowedValues[index - 1];
                return true;
            }
            case Rdr2SettingKind.Boolean:
                if (!string.Equals(current, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                lowered = "false";
                return true;
            case Rdr2SettingKind.Integer:
                if (!int.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    return false;
                }

                var intLowered = Math.Max(
                    int.Parse(definition.Minimum!, CultureInfo.InvariantCulture),
                    intValue - 1);
                if (intLowered == intValue)
                {
                    return false;
                }

                lowered = intLowered.ToString(CultureInfo.InvariantCulture);
                return true;
            case Rdr2SettingKind.Decimal:
                if (!double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
                {
                    return false;
                }

                var minimum = double.Parse(definition.Minimum!, CultureInfo.InvariantCulture);
                var decimalLowered = Math.Max(minimum, Math.Round(decimalValue - 0.1, 2));
                if (Math.Abs(decimalLowered - decimalValue) < 0.0001)
                {
                    return false;
                }

                lowered = decimalLowered.ToString("0.00", CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    private static string BuildFallbackRationale(
        string targetGroup,
        PerformanceMetrics metrics,
        TuningGoal goal,
        int count)
    {
        var reason = targetGroup switch
        {
            "textures" when metrics.VramRatio >= 0.9 => $"显存占用达到 {metrics.VramRatio:P0}",
            "volumetrics" when metrics.GpuUtilization >= 90 => $"GPU 利用率达到 {metrics.GpuUtilization:0}%",
            _ when metrics.Bottleneck == BottleneckKind.Cpu => "CPU 存在瓶颈",
            _ => "NVIDIA 预设已无法提供更细的档位"
        };
        var target = goal.Mode switch
        {
            TuningMode.FrameRate => "优先帧率",
            TuningMode.Quality => "在守住帧率底线的前提下保留画质",
            _ => "平衡帧率和画质"
        };
        return $"{reason}，因此将 {count} 项高开销设置下调一档；目标是 {target}，目标帧率 {goal.TargetFps:0}。";
    }

    private static XDocument LoadXml(string content)
    {
        var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        ValidateDocument(document);
        return document;
    }

    private static void ValidateDocument(XDocument document)
    {
        if (document.Root?.Name.LocalName != "rage__fwuiSystemSettingsCollection")
        {
            throw new InvalidDataException("system.xml 根节点不匹配，可能是未知或损坏的配置。");
        }

        var version = document.Root.Element("version")?.Attribute("value")?.Value;
        if (!string.IsNullOrEmpty(version) &&
            !string.Equals(version, "37", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"RDR2 配置结构版本为 {version}，当前仅验证了版本 37。请先更新适配器。");
        }
    }

    private static Dictionary<string, string> ReadSettings(XDocument document)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in Rdr2SettingsCatalog.Definitions)
        {
            var element = FindElement(document, definition.Key);
            if (element is null)
            {
                continue;
            }

            settings[definition.Key] = ReadElementValue(element);
        }

        return settings;
    }

    private static XElement? FindElement(XDocument document, string key) =>
        document.Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, key, StringComparison.Ordinal));

    private static string ReadElementValue(XElement element) =>
        element.Attribute("value")?.Value ?? element.Value.Trim();

    private static string SetValueInRaw(string content, string key, string value)
    {
        var escapedKey = Regex.Escape(key);
        var escapedValue = System.Security.SecurityElement.Escape(value)
            ?? throw new InvalidDataException($"设置值无法写入 XML：{value}");
        var attributePattern = new Regex(
            $"(?<prefix><\\s*{escapedKey}\\b[^>]*?\\bvalue\\s*=\\s*[\"'])(?<value>[^\"']*)(?<suffix>[\"'][^>]*?/?>)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        var attributeMatches = attributePattern.Matches(content);
        if (attributeMatches.Count > 1)
        {
            throw new InvalidDataException($"RDR2 配置中存在重复设置项：{key}");
        }

        if (attributeMatches.Count == 1)
        {
            return attributePattern.Replace(
                content,
                match =>
                    match.Groups["prefix"].Value +
                    escapedValue +
                    match.Groups["suffix"].Value,
                1);
        }

        var elementPattern = new Regex(
            $"(?<prefix><\\s*{escapedKey}\\b[^>]*>)(?<value>.*?)(?<suffix></\\s*{escapedKey}\\s*>)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        var elementMatches = elementPattern.Matches(content);
        if (elementMatches.Count > 1)
        {
            throw new InvalidDataException($"RDR2 配置中存在重复设置项：{key}");
        }

        if (elementMatches.Count == 1)
        {
            return elementPattern.Replace(
                content,
                match =>
                    match.Groups["prefix"].Value +
                    escapedValue +
                    match.Groups["suffix"].Value,
                1);
        }

        throw new InvalidDataException($"RDR2 配置中不存在设置项：{key}");
    }

    private static async Task WriteAtomicAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        var temp = Path.Combine(directory, "." + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var backup = Path.Combine(directory, "." + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".bak");
        try
        {
            await using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(targetPath))
            {
                try
                {
                    File.Replace(temp, targetPath, backup, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temp, targetPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, targetPath);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
        }
    }

    private static void ValidateOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var pair in overrides)
        {
            var definition = Rdr2SettingsCatalog.Find(pair.Key)
                ?? throw new ArgumentException($"设置项 {pair.Key} 不在 RDR2 白名单中。");
            switch (definition.Kind)
            {
                case Rdr2SettingKind.QualityLevel:
                    if (!definition.AllowedValues.Contains(pair.Value, StringComparer.Ordinal))
                    {
                        throw new ArgumentException($"{pair.Key} 的值 {pair.Value} 不在允许档位中。");
                    }

                    break;
                case Rdr2SettingKind.Boolean:
                    if (!bool.TryParse(pair.Value, out _))
                    {
                        throw new ArgumentException($"{pair.Key} 必须是 true 或 false。");
                    }

                    break;
                case Rdr2SettingKind.Integer:
                    if (!int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue) ||
                        intValue < int.Parse(definition.Minimum!, CultureInfo.InvariantCulture) ||
                        intValue > int.Parse(definition.Maximum!, CultureInfo.InvariantCulture))
                    {
                        throw new ArgumentException($"{pair.Key} 超出允许范围。");
                    }

                    break;
                case Rdr2SettingKind.Decimal:
                    if (!double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue) ||
                        decimalValue < double.Parse(definition.Minimum!, CultureInfo.InvariantCulture) ||
                        decimalValue > double.Parse(definition.Maximum!, CultureInfo.InvariantCulture))
                    {
                        throw new ArgumentException($"{pair.Key} 超出允许范围。");
                    }

                    break;
            }
        }
    }
}
