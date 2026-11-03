using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using GameTuner.App.Processes;
using GameTuner.Core.Adapters.FirstLight;
using GameTuner.Core.Adapters.Rdr2;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Telemetry;
using Microsoft.Win32;

namespace GameTuner.App.Web;

public sealed record LocalConfigurationSnapshot
{
    public string DetectedGame { get; init; } = "";
    public int ProcessId { get; init; }
    public string GameProcessStatus { get; init; } = "尚未选择游戏进程。";
    public string DiscoveredLogPath { get; init; } = "";
    public LogDiscoveryStatus LogStatus { get; init; } = LogDiscoveryStatus.Searching;
    public string Cpu { get; init; } = "";
    public string Gpu { get; init; } = "";
    public string Memory { get; init; } = "";
    public string Resolution { get; init; } = "";
    public string GraphicsSettings { get; init; } = "";
    public string Status { get; init; } = "";
    public string Source { get; init; } = "";
    public bool HasGameSettings { get; init; }
}

public interface ILocalConfigurationProvider
{
    Task<LocalConfigurationSnapshot> ReadAsync(
        GameProcessInfo? process, string game, bool includeDesktopResolution, string logDirectory);
}

public enum LogDiscoveryStatus
{
    Searching,
    Found,
    Waiting
}

/// <summary>Reads machine and supported game settings without starting games or writing their files.</summary>
public sealed class WindowsLocalConfigurationProvider(AppPaths paths) : ILocalConfigurationProvider
{
    private readonly Lazy<Task<MachineHardware>> _hardware = new(ReadHardwareAsync);

    public async Task<LocalConfigurationSnapshot> ReadAsync(
        GameProcessInfo? process, string game, bool includeDesktopResolution, string logDirectory)
    {
        var hardware = await _hardware.Value;
        var effectiveGame = string.IsNullOrWhiteSpace(game) ? process?.ProcessName ?? "" : game;
        var desktopResolution = includeDesktopResolution ? PrimaryDisplayResolution() : "";
        var discoveredLog = DiscoverLog(effectiveGame, logDirectory);
        LocalConfigurationSnapshot Finish(LocalConfigurationSnapshot settings) => Merge(hardware, settings) with
        {
            DetectedGame = process?.ProcessName ?? "",
            ProcessId = process?.Id ?? 0,
            GameProcessStatus = process is null ? "尚未选择游戏进程。" : $"已选择游戏进程：{process.ProcessName}.exe (PID {process.Id})",
            DiscoveredLogPath = discoveredLog?.FullName ?? "",
            LogStatus = discoveredLog is null ? LogDiscoveryStatus.Waiting : LogDiscoveryStatus.Found
        };
        if (IsRdr2(effectiveGame) && File.Exists(paths.Rdr2ConfigFile))
        {
            try
            {
                var settings = await ReadRdr2Async();
                return Finish(settings with { Resolution = settings.Resolution });
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return Finish(new LocalConfigurationSnapshot
                {
                    Resolution = desktopResolution,
                    Status = "硬件已自动获取；RDR2 配置读取失败，请检查 system.xml 后重试。"
                });
            }
        }
        if (IsFirstLight(effectiveGame))
        {
            try
            {
                var values = new RegistryFirstLightSettingsStore().Read();
                if (values.Count > 0)
                {
                    var lines = FirstLightSettingsCatalog.Definitions
                        .Where(item => values.ContainsKey(item.Key))
                        .Select(item => $"{item.Label}：{FormatFirstLightValue(item, values[item.Key])}");
                    return Finish(new LocalConfigurationSnapshot
                    {
                        Resolution = desktopResolution,
                        GraphicsSettings = string.Join(Environment.NewLine, lines),
                        HasGameSettings = true,
                        Source = "007 本机图形设置",
                        Status = "已从本机注册表只读获取 007 画质；硬件由 Windows 自动获取。"
                    });
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A game that has never saved settings has no registry values yet.
            }
        }
        return Finish(new LocalConfigurationSnapshot
        {
            Resolution = desktopResolution,
            Status = "硬件已自动获取。当前游戏没有可用的只读画质适配器，可手动补充实际设置。",
            Source = "Windows 本机信息"
        });
    }

    private static LocalConfigurationSnapshot Merge(MachineHardware hardware, LocalConfigurationSnapshot settings) =>
        settings with
        {
            Cpu = hardware.Cpu,
            Gpu = hardware.Gpu,
            Memory = FormatMemory(hardware)
        };

    private static string FormatMemory(MachineHardware hardware)
    {
        var parts = new List<string>(2);
        if (hardware.RamGb > 0) parts.Add($"{hardware.RamGb:0.#} GB RAM");
        if (hardware.VramGb > 0) parts.Add($"{hardware.VramGb:0.#} GB VRAM");
        return string.Join(" / ", parts);
    }

    private async Task<LocalConfigurationSnapshot> ReadRdr2Async()
    {
        var content = await File.ReadAllTextAsync(paths.Rdr2ConfigFile, Encoding.UTF8);
        var document = XDocument.Parse(content);
        if (document.Root?.Name.LocalName != "rage__fwuiSystemSettingsCollection")
            throw new InvalidDataException("RDR2 system.xml 根节点不匹配。");
        var lines = Rdr2SettingsCatalog.Definitions
            .Select(item => (item, element: Find(document, item.Key)))
            .Where(pair => pair.element is not null)
            .Select(pair => $"{pair.item.Label}：{FormatRdr2Value(pair.item, Value(pair.element!))}")
            .ToList();
        var preset = Find(document, "graphicsQualityPreset")?.Attribute("value")?.Value;
        if (double.TryParse(preset, NumberStyles.Float, CultureInfo.InvariantCulture, out var presetValue))
            lines.Insert(0, $"画质预设原始值：{presetValue:0.###}");
        var width = Value(Find(document, "screenWidth"));
        var height = Value(Find(document, "screenHeight"));
        var resolution = int.TryParse(width, out var w) && int.TryParse(height, out var h) && w > 0 && h > 0
            ? $"{w} × {h}" : "";
        return new LocalConfigurationSnapshot
        {
            Resolution = resolution,
            GraphicsSettings = string.Join(Environment.NewLine, lines),
            HasGameSettings = lines.Count > 0,
            Source = "RDR2 system.xml",
            Status = "已从 RDR2 system.xml 只读获取实际分辨率与画质；硬件由 Windows 自动获取。"
        };
    }

    private static XElement? Find(XDocument document, string name) =>
        document.Descendants().FirstOrDefault(element => element.Name.LocalName == name);

    private static string Value(XElement? element) =>
        element?.Attribute("value")?.Value ?? element?.Value.Trim() ?? "";

    private static string FormatRdr2Value(Rdr2SettingDefinition definition, string value) => value switch
    {
        "kSettingLevel_Low" => "低",
        "kSettingLevel_Medium" => "中",
        "kSettingLevel_High" => "高",
        "kSettingLevel_Ultra" => "极高",
        "kSettingLevel_Custom" => "自定义",
        "true" => "开启",
        "false" => "关闭",
        _ => value
    };

    private static string FormatFirstLightValue(FirstLightSettingDefinition definition, string value) =>
        definition.Key == "SuperSampling" && double.TryParse(value, out var scale)
            ? $"{scale / 10:0.#}%" : value;

    private static bool IsRdr2(string game) =>
        game.Contains("red dead redemption 2", StringComparison.OrdinalIgnoreCase)
        || game.Contains("荒野大镖客", StringComparison.OrdinalIgnoreCase)
        || game.Equals("rdr2", StringComparison.OrdinalIgnoreCase);

    private static bool IsFirstLight(string game) =>
        game.Contains("007", StringComparison.OrdinalIgnoreCase)
        || game.Contains("first light", StringComparison.OrdinalIgnoreCase);

    private static FileInfo? DiscoverLog(string game, string logDirectory)
    {
        var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        AddPerformanceLogs(files, logDirectory);
        AddPerformanceLogs(files, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nvlog"));
        if (IsRdr2(game))
        {
            var benchmarks = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Rockstar Games", "Red Dead Redemption 2", "Benchmarks");
            if (Directory.Exists(benchmarks))
            {
                foreach (var file in new DirectoryInfo(benchmarks).EnumerateFiles("Benchmark-*", SearchOption.TopDirectoryOnly))
                    if (file.Length > 0) files[file.FullName] = file;
            }
        }
        return files.Values.OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
    }

    private static void AddPerformanceLogs(Dictionary<string, FileInfo> files, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            if ((file.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                || file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                && !file.Name.Contains("Latency_Log", StringComparison.OrdinalIgnoreCase) && file.Length > 0)
                files[file.FullName] = file;
        }
    }

    private static string PrimaryDisplayResolution()
    {
        if (!OperatingSystem.IsWindows()) return "";
        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);
        return width > 0 && height > 0 ? $"{width} × {height}" : "";
    }

    private static async Task<MachineHardware> ReadHardwareAsync()
    {
        var cpu = "";
        var gpu = "";
        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$cpu=Get-CimInstance Win32_Processor|Select-Object -First 1 -ExpandProperty Name;$gpu=Get-CimInstance Win32_VideoController|Where-Object {$_.Name -notmatch 'Virtual|Parsec|GameViewer|Microsoft Basic'}|Select-Object -First 1 -ExpandProperty Name;[pscustomobject]@{Cpu=[string]$cpu;Gpu=[string]$gpu}|ConvertTo-Json -Compress");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动本机硬件查询。");
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                var result = JsonSerializer.Deserialize<HardwareQuery>(output);
                cpu = result?.Cpu.Trim() ?? "";
                gpu = result?.Gpu.Trim() ?? "";
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            // Registry and memory APIs below still provide a useful partial result.
        }
        if (cpu.Length == 0 && OperatingSystem.IsWindows())
            cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string ?? "";
        var ramGb = OperatingSystem.IsWindows() && GlobalMemoryStatusEx(out var memory) ? memory / 1073741824d : 0;
        var vramGb = 0d;
        if (gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await new NvidiaSmiReader().ReadAsync();
            if (snapshot is not null) vramGb = snapshot.MemoryTotalMb / 1024d;
        }
        return new MachineHardware(cpu.Trim(), gpu.Trim(), Math.Ceiling(ramGb), vramGb);
    }

    private sealed record MachineHardware(string Cpu, string Gpu, double RamGb, double VramGb);
    private sealed record HardwareQuery
    {
        public string Cpu { get; init; } = "";
        public string Gpu { get; init; } = "";
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static bool GlobalMemoryStatusEx(out double totalPhysical)
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var succeeded = GlobalMemoryStatusEx(ref status);
        totalPhysical = status.TotalPhysical;
        return succeeded;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
