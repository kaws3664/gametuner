using System.IO;
using System.Xml.Linq;
using GameTuner.App;

namespace GameTuner.UiTests;

public sealed class PackagingTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task Fresh_build_does_not_include_browser_assets_and_retains_presentMon()
    {
        var project = Path.Combine(Root, "src", "GameTuner.App", "GameTuner.App.csproj");
        var document = XDocument.Load(project);
        Assert.DoesNotContain(document.Descendants("Content"),
            element => ((string?)element.Attribute("Include"))?.StartsWith("WebApp\\", StringComparison.Ordinal) == true);
        var output = Path.Combine(Path.GetTempPath(), "GameTuner-PackagingTests", Guid.NewGuid().ToString("N"));
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("publish");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(output);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        try
        {
            Assert.True(process.ExitCode == 0, $"dotnet publish failed ({process.ExitCode}): {await stdout}\n{await stderr}");
            Assert.False(Directory.Exists(Path.Combine(output, "WebApp")),
                "A fresh native desktop build must not carry the obsolete WebApp directory.");
            Assert.True(File.Exists(Path.Combine(output, "Tools", "PresentMon", "PresentMon.exe")));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Elevation_is_requested_so_presentMon_can_start_a_trace_session()
    {
        // PresentMon refuses to start its ETW trace session without administrative
        // rights: a plain run exits with "access denied" and writes NO log at all,
        // which the UI can only report as a missing frame. Verified by running the
        // bundled PresentMon 2.3.1 both ways: elevated produced a 546-row capture,
        // unelevated produced no file. Keep the manifest at requireAdministrator.
        var manifest = XDocument.Load(Path.Combine(Root, "src", "GameTuner.App", "app.manifest"));
        var level = manifest.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "requestedExecutionLevel");

        Assert.NotNull(level);
        Assert.Equal("requireAdministrator", (string?)level.Attribute("level"));
    }

    [Fact]
    public void Theme_brushes_are_never_referenced_statically_so_dark_mode_has_no_leak()
    {
        // A StaticResource brush is frozen at parse time, so it keeps its LIGHT
        // colour after the theme switches. That leaked a bright #DFE4DF popup
        // border and a light arrow into dark mode. Every brush App.xaml swaps at
        // runtime must therefore be reached through DynamicResource.
        var source = File.ReadAllText(Path.Combine(Root, "src", "GameTuner.App", "App.xaml.cs"));
        var keys = System.Text.RegularExpressions.Regex
            .Matches(source, @"\(""([A-Za-z]+Brush)"",\s*""")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();
        Assert.NotEmpty(keys);
        var xaml = File.ReadAllText(Path.Combine(Root, "src", "GameTuner.App", "App.xaml"));
        var leaked = keys
            .Where(key => xaml.Contains("{StaticResource " + key + "}", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(leaked);
    }

    [Fact]
    public void Readme_does_not_instruct_players_to_open_a_loopback_url()
    {
        var readme = File.ReadAllText(Path.Combine(Root, "README.md"));

        Assert.DoesNotContain("启动后会自动打开系统默认浏览器", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("本地 `HttpListener` 服务只绑定 `127.0.0.1`", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_composition_has_no_browser_transport_or_url_key()
    {
        var app = typeof(Program).Assembly;

        Assert.DoesNotContain(app.GetTypes(), type =>
            type.Namespace == "GameTuner.App.Web" && type.Name is
                "LocalWebServer" or "WorkspaceApi" or "WorkspaceRequests" or "WorkspaceInput" or
                "RoundRequest" or "PathRequest" or "WorkspaceState" or "DesktopDialogs");
        Assert.DoesNotContain(app.GetReferencedAssemblies(), reference =>
            reference.Name is "System.Net.HttpListener" or "Microsoft.AspNetCore.App");
    }
}
