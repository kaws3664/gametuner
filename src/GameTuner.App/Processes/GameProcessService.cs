using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameTuner.App.Processes;

public class GameProcessService
{
    /// <summary>A window covering at least this much of the screen counts as full-screen play.</summary>
    private const double FullScreenCoverage = 0.6;

    /// <summary>Process names that belong to this app, the graphics stack or the desktop shell.</summary>
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "GameTuner.App", "PresentMon", "nvidia-smi", "nvcontainer", "nvcplui", "nvsphelper64",
        "nvtoppsvc", "nvidia overlay", "nvsphelper", "magpie", "losslessscaling", "alma",
        "explorer", "dwm", "conhost", "cmd", "powershell", "pwsh", "windowsterminal", "wt",
        "taskmgr", "systemsettings", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "applicationframehost", "textinputhost", "runtimebroker", "sihost", "ctfmon",
        "devenv", "code", "codium", "rider64", "dotnet", "msbuild", "vctip", "msedgewebview2",
        "discord", "slack", "teams", "ms-teams", "zoom", "obs64", "obs32", "spotify",
        "winrar", "7zfm", "notepad", "notepad++", "mspaint", "snippingtool", "calculator",
        "taskhostw", "widgets", "phoneexperiencehost", "yourphone"
    };

    /// <summary>Launchers and stores that own a game's window but are not the game itself.</summary>
    private static readonly HashSet<string> LauncherNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "epicgameslauncher", "galaxyclient", "battle.net", "origin", "eadesktop",
        "ubisoftconnect", "upc", "uplay", "riotclient", "wegame", "goggalaxy", "playnite",
        "steamwebhelper", "riotclientservices", "riotclientux", "launcher", "updater",
        "patcher", "anticheat", "easyanticheat", "easyanticheat_eos", "beservice", "vgtray"
    };

    /// <summary>Browsers and players report whatever page they show, so their titles never identify a game.</summary>
    private static readonly HashSet<string> BrowserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "browser", "chrome", "chromium", "msedge", "edge", "firefox", "brave", "opera",
        "vivaldi", "safari", "webkit", "electron", "tabbit"
    };

    public virtual IReadOnlyList<GameProcessInfo> GetProcesses()
    {
        var processes = new List<GameProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    processes.Add(new GameProcessInfo(process.Id, process.ProcessName, process.MainWindowTitle));
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // A process can exit or deny access while the snapshot is being read.
                    continue;
                }
            }
        }

        return processes
            .OrderByDescending(process => !string.IsNullOrEmpty(process.WindowTitle))
            .ThenBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Reads live window bounds and delegates to the verifiable scoring rule.</summary>
    public virtual GameProcessInfo? DetectRunningGame() =>
        DetectRunningGame(BuildCandidates(GetProcesses()));

    public virtual GameProcessInfo? DetectRunningGame(string game) =>
        DetectRunningGame(BuildCandidates(GetProcesses()), game);

    /// <summary>
    /// Picks the most likely running game, or returns null when the guess would not be
    /// trustworthy, so that the player is asked instead of shown a wrong game name.
    /// </summary>
    public GameProcessInfo? DetectRunningGame(IReadOnlyList<GameWindowCandidate> candidates, string game = "")
    {
        var eligible = candidates.Where(IsEligible).ToArray();
        GameWindowCandidate? winner;
        if (!string.IsNullOrWhiteSpace(game))
        {
            var name = NormalizeName(game);
            var matches = eligible.Where(candidate => NormalizeName(candidate.ProcessName) == name
                || NormalizeName(candidate.WindowTitle) == name).ToArray();
            winner = name.Length > 0 && matches.Length == 1 ? matches[0] : null;
        }
        else
        {
            // Coverage only ranks candidates; it must not gate detection. A single
            // windowed game is unambiguous, so take it even below the threshold
            // instead of telling the player nothing was found.
            winner = eligible.Where(candidate => candidate.ScreenCoverage >= FullScreenCoverage)
                .OrderByDescending(candidate => candidate.ScreenCoverage)
                .ThenByDescending(candidate => candidate.WindowTitle.Length)
                .FirstOrDefault()
                ?? (eligible.Length == 1 ? eligible[0] : null);
        }
        return winner is null ? null : new GameProcessInfo(winner.Id, winner.ProcessName, winner.WindowTitle);
    }

    /// <summary>Explains the outcome, including the near-misses a player would expect to work.</summary>
    public string DescribeDetection(IReadOnlyList<GameWindowCandidate> candidates, bool forDetectedGame = false)
    {
        var detected = DetectRunningGame(candidates);
        if (detected is not null)
            return $"已检测到游戏：{detected.ProcessName}.exe · PID {detected.Id}"
                + (detected.WindowTitle.Length == 0 ? "" : $"「{detected.WindowTitle}」");

        var windowed = candidates
            .Where(candidate => !IgnoredNames.Contains(candidate.ProcessName)
                && !IsLauncher(candidate.ProcessName)
                && !IsBrowser(candidate.ProcessName)
                && candidate.WindowTitle.Length > 0)
            .ToArray();
        if (windowed.Length > 0)
            return "检测到有窗口的程序，但没有处于全屏状态的游戏窗口（可能是窗口化的游戏或桌面程序）；"
                + "请进入游戏画面后重新打开，或点击“选择游戏进程…”手动指定。";
        if (candidates.Any(candidate => IsLauncher(candidate.ProcessName)))
            return "检测到游戏启动器正在运行，但它不是游戏本体；请从游戏内进入实际画面后手动选择进程。";
        return "没有检测到正在运行的游戏；请先启动游戏，或点击“选择游戏进程…”手动指定。";
    }

    /// <summary>Maps the live process table onto candidates with real window coverage.</summary>
    public virtual IReadOnlyList<GameWindowCandidate> BuildCandidates(IReadOnlyList<GameProcessInfo> processes)
    {
        var candidates = new List<GameWindowCandidate>(processes.Count);
        foreach (var process in processes)
        {
            if (string.IsNullOrWhiteSpace(process.ProcessName)) continue;
            if (process.WindowTitle.Length == 0) continue;
            var coverage = WindowCoverage(process.Id);
            candidates.Add(new GameWindowCandidate(process.Id, process.ProcessName, process.WindowTitle, coverage));
        }
        return candidates;
    }

    private static bool IsEligible(GameWindowCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.ProcessName)) return false;
        if (candidate.WindowTitle.Length == 0) return false;
        if (IgnoredNames.Contains(candidate.ProcessName)) return false;
        if (IsLauncher(candidate.ProcessName)) return false;
        if (IsBrowser(candidate.ProcessName)) return false;
        return true;
    }

    private static bool IsLauncher(string processName) =>
        LauncherNames.Contains(processName)
        || processName.EndsWith("launcher", StringComparison.OrdinalIgnoreCase)
        || processName.EndsWith("anticheat", StringComparison.OrdinalIgnoreCase);

    private static bool IsBrowser(string processName) =>
        BrowserNames.Contains(processName)
        || processName.EndsWith("browser", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string value)
    {
        var name = value.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }

    private static double WindowCoverage(int processId)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            using var process = Process.GetProcessById(processId);
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) return 0;
            if (!GetWindowRect(handle, out var rect)) return 0;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return 0;
            var placement = new WindowPlacement { Size = Marshal.SizeOf<WindowPlacement>() };
            if (IsIconic(handle) && GetWindowPlacement(handle, ref placement))
            {
                // Alt-tabbing to the capture window can minimize an exclusive-fullscreen game.
                rect = (placement.Flags & 2) != 0 ? monitor.Work : placement.NormalPosition;
            }
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            var screen = (double)(monitor.Monitor.Right - monitor.Monitor.Left)
                * (monitor.Monitor.Bottom - monitor.Monitor.Top);
            return width > 0 && height > 0 && screen > 0 ? Math.Clamp(width * (double)height / screen, 0, 1) : 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr handle, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Size;
        public uint Flags;
        public uint ShowCommand;
        public WindowPoint MinPosition;
        public WindowPoint MaxPosition;
        public WindowRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WindowRect Monitor;
        public WindowRect Work;
        public uint Flags;
    }
}
