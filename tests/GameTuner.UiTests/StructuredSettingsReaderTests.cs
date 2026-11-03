using System.IO;
using System.Text;
using GameTuner.App.Settings;

namespace GameTuner.UiTests;

public sealed class StructuredSettingsReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GameTuner-SettingsTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("settings.json", "{\"rayTracing\":true,\"textureQuality\":\"high\"}", "textureQuality：high")]
    [InlineData("settings.ini", "[graphics]\nrayTracing=on", "rayTracing：on")]
    [InlineData("settings.xml", "<settings><rayTracing>on</rayTracing></settings>", "rayTracing：on")]
    public async Task Reads_scalar_settings_without_inventing_quality_labels(
        string name, string content, string expected)
    {
        var path = Write(name, content);

        var text = await new StructuredSettingsReader().ReadAsync(path);

        Assert.Contains(expected, text);
    }

    [Fact]
    public async Task Json_preserves_original_names_and_scalar_values()
    {
        var path = Write("settings.json",
            "{\"rayTracing\":true,\"textureQuality\":\"high\",\"renderScale\":1.2500,\"vsync\":false,\"optional\":null}");

        var text = await new StructuredSettingsReader().ReadAsync(path);

        Assert.Equal(string.Join(Environment.NewLine,
            "rayTracing：true", "textureQuality：high", "renderScale：1.2500", "vsync：false", "optional：null"), text);
    }

    [Fact]
    public async Task Ini_preserves_section_context_and_raw_scalar_values()
    {
        var path = Write("settings.ini",
            "rayTracing=on\n[graphics]\ntextureQuality = high\nrenderScale=1.2500\n");

        var text = await new StructuredSettingsReader().ReadAsync(path);

        Assert.Equal(string.Join(Environment.NewLine,
            "rayTracing：on", "[graphics] textureQuality：high", "[graphics] renderScale：1.2500"), text);
    }

    [Fact]
    public async Task Xml_reads_attributes_and_leaf_elements_with_original_names()
    {
        var path = Write("settings.xml",
            "<settings textureQuality=\"high\"><rayTracing>on</rayTracing><renderScale>1.2500</renderScale></settings>");

        var text = await new StructuredSettingsReader().ReadAsync(path);

        Assert.Equal(string.Join(Environment.NewLine,
            "textureQuality：high", "rayTracing：on", "renderScale：1.2500"), text);
    }

    [Theory]
    [InlineData("<settings xmlns=\"urn:game\"><rayTracing>on</rayTracing></settings>", "rayTracing：on")]
    [InlineData("<g:settings xmlns:g=\"urn:game\"><g:rayTracing>on</g:rayTracing></g:settings>", "g:rayTracing：on")]
    public async Task Xml_ignores_default_and_prefixed_namespace_declarations(string content, string expected)
    {
        var path = Write("namespaced.xml", content);

        var text = await new StructuredSettingsReader().ReadAsync(path);

        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("{\"graphics\":{\"rayTracing\":true}}")]
    [InlineData("{\"quality\":[\"high\"]}")]
    [InlineData("[{\"rayTracing\":true}]")]
    public async Task Json_rejects_nested_or_non_object_roots(string content)
    {
        var path = Write("settings.json", content);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new StructuredSettingsReader().ReadAsync(path));

        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("标量", exception.Message);
    }

    [Fact]
    public async Task Json_rejects_malformed_content_and_duplicate_names()
    {
        var malformed = Write("malformed.json", "{\"rayTracing\":");
        var duplicate = Write("duplicate.json", "{\"rayTracing\":true,\"rayTracing\":false}");

        var malformedException = await Assert.ThrowsAsync<InvalidDataException>(
            () => new StructuredSettingsReader().ReadAsync(malformed));
        var duplicateException = await Assert.ThrowsAsync<InvalidDataException>(
            () => new StructuredSettingsReader().ReadAsync(duplicate));

        Assert.Contains("JSON", malformedException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("重复", duplicateException.Message);
    }

    [Fact]
    public async Task Ini_rejects_conflicting_duplicate_keys_and_malformed_lines()
    {
        var conflicting = Write("conflicting.ini", "[graphics]\nrayTracing=on\nrayTracing=off");
        var malformed = Write("malformed.ini", "[graphics]\nrayTracing:on");

        var conflictException = await Assert.ThrowsAsync<InvalidDataException>(
            () => new StructuredSettingsReader().ReadAsync(conflicting));
        var malformedException = await Assert.ThrowsAsync<InvalidDataException>(
            () => new StructuredSettingsReader().ReadAsync(malformed));

        Assert.Contains("冲突", conflictException.Message);
        Assert.Contains("rayTracing", conflictException.Message);
        Assert.Contains("格式", malformedException.Message);
    }

    [Fact]
    public async Task Xml_rejects_malformed_content_and_duplicate_setting_names()
    {
        var malformed = Write("malformed.xml", "<settings><rayTracing>on</settings>");
        var duplicate = Write("duplicate.xml",
            "<settings><rayTracing>on</rayTracing><rayTracing>off</rayTracing></settings>");

        var malformedException = await Assert.ThrowsAsync<InvalidDataException>(
            () => new StructuredSettingsReader().ReadAsync(malformed));
        var duplicateException = await Assert.ThrowsAsync<InvalidDataException>(
            () => new StructuredSettingsReader().ReadAsync(duplicate));

        Assert.Contains("XML", malformedException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("重复", duplicateException.Message);
    }

    [Fact]
    public async Task Reader_rejects_files_over_one_mebibyte_before_parsing()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "oversize.json");
        await File.WriteAllBytesAsync(path, new byte[(1024 * 1024) + 1]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new StructuredSettingsReader().ReadAsync(path));

        Assert.Contains("1 MB", exception.Message);
    }

    [Fact]
    public async Task Reader_rejects_unsupported_extensions()
    {
        var path = Write("settings.txt", "rayTracing=on");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new StructuredSettingsReader().ReadAsync(path));

        Assert.Contains(".json", exception.Message);
        Assert.Contains(".xml", exception.Message);
        Assert.Contains(".ini", exception.Message);
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
