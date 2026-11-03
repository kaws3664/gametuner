using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace GameTuner.App.Settings;

public sealed class StructuredSettingsReader
{
    private const int MaximumBytes = 1024 * 1024;

    public async Task<string> ReadAsync(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".ini", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("仅支持 .json、.xml 或 .ini 配置文件。");
        }
        if (new FileInfo(path).Length > MaximumBytes)
            throw new InvalidDataException("配置文件不能超过 1 MB。");

        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        try
        {
            return extension.ToLowerInvariant() switch
            {
                ".json" => ReadJson(text),
                ".xml" => ReadXml(text),
                _ => ReadIni(text)
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("JSON 配置格式无效：" + exception.Message, exception);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("XML 配置格式无效：" + exception.Message, exception);
        }
    }

    private static string ReadJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("JSON 仅支持根对象中的标量属性；不接受嵌套对象或数组。");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new InvalidDataException($"JSON 设置名 '{property.Name}' 重复，无法确定实际值。");
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null =>
                    property.Value.GetRawText(),
                _ => throw new InvalidDataException(
                    $"JSON 设置 '{property.Name}' 不是标量；不接受嵌套对象或数组。")
            };
            lines.Add($"{property.Name}：{value}");
        }
        return Finish(lines);
    }

    private static string ReadXml(string text)
    {
        var document = XDocument.Parse(text);
        var root = document.Root ?? throw new InvalidDataException("XML 配置缺少根元素。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
                AddUnique(lines, names, OriginalName(attribute.Name, element), attribute.Value);
            if (!element.Elements().Any())
                AddUnique(lines, names, OriginalName(element.Name, element), element.Value);
        }
        return Finish(lines);
    }

    private static string ReadIni(string text)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        var section = "";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                if (line.Length < 3 || !line.EndsWith(']'))
                    throw new InvalidDataException($"INI 节格式无效：'{line}'。");
                section = line[1..^1].Trim();
                if (section.Length == 0) throw new InvalidDataException("INI 节名不能为空。");
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0) throw new InvalidDataException($"INI 设置格式无效：'{line}'。");
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0) throw new InvalidDataException("INI 设置名不能为空。");
            var identity = section + "\u001f" + key;
            if (entries.TryGetValue(identity, out var previous))
            {
                if (!previous.Equals(value, StringComparison.Ordinal))
                    throw new InvalidDataException($"INI 键 '{key}' 在节 '[{section}]' 中存在冲突值，无法确定实际设置。");
                continue;
            }
            entries.Add(identity, value);
            var displayName = section.Length == 0 ? key : $"[{section}] {key}";
            lines.Add($"{displayName}：{value}");
        }
        return Finish(lines);
    }

    private static void AddUnique(
        List<string> lines, HashSet<string> names, string name, string value)
    {
        if (!names.Add(name))
            throw new InvalidDataException($"XML 设置名 '{name}' 重复，无法确定实际值。");
        lines.Add($"{name}：{value}");
    }

    private static string OriginalName(XName name, XElement context)
    {
        var prefix = context.GetPrefixOfNamespace(name.Namespace);
        return string.IsNullOrEmpty(prefix) ? name.LocalName : $"{prefix}:{name.LocalName}";
    }

    private static string Finish(List<string> lines) => lines.Count == 0
        ? throw new InvalidDataException("配置文件中没有可导入的标量设置。")
        : string.Join(Environment.NewLine, lines);
}
