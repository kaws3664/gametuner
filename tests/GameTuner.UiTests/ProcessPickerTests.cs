using System.Diagnostics;
using System.IO;
using GameTuner.App.Desktop;
using GameTuner.App.Processes;
using GameTuner.App.ViewModels;
using GameTuner.Core.Analysis;
using GameTuner.Core.Infrastructure;

namespace GameTuner.UiTests;

/// <summary>Stands in for the live process table so startup detection is deterministic.</summary>
internal sealed class FakeGameProcessService : GameProcessService
{
    private static readonly GameWindowCandidate[] Snapshot =
    [
        new(12, "steam", "Steam", 0.75),
        new(15, "SomeGame", "Some Game - Main Menu", 1.0)
    ];

    public override IReadOnlyList<GameWindowCandidate> BuildCandidates(
        IReadOnlyList<GameProcessInfo> processes) => Snapshot;

    public override GameProcessInfo? DetectRunningGame() => DetectRunningGame(Snapshot);
}

internal sealed class SnapshotGameProcessService : GameProcessService
{
    public IReadOnlyList<GameWindowCandidate> Candidates { get; set; } = [];

    public override IReadOnlyList<GameWindowCandidate> BuildCandidates(
        IReadOnlyList<GameProcessInfo> processes) => Candidates;

    public override GameProcessInfo? DetectRunningGame() => DetectRunningGame(Candidates);
}

public sealed class ProcessPickerTests
{
    [Theory]
    [InlineData("MirrorsEdge")]
    [InlineData("SteamWorldDig")]
    [InlineData("OperaGame")]
    public void Game_names_containing_browser_or_store_words_are_not_excluded(string processName)
    {
        var service = new GameProcessService();

        var detected = service.DetectRunningGame(
            [new GameWindowCandidate(42, processName, "Game Window", 1)]);

        Assert.NotNull(detected);
        Assert.Equal(42, detected.Id);
        Assert.Equal(processName, detected.ProcessName);
    }

    [Theory]
    [InlineData("steamwebhelper")]
    [InlineData("RiotClientServices")]
    [InlineData("ExampleLauncher")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("Tabbit Browser")]
    public void Store_helpers_launchers_and_browsers_remain_excluded(string processName)
    {
        var service = new GameProcessService();

        Assert.Null(service.DetectRunningGame(
            [new GameWindowCandidate(42, processName, "Game Window", 1)]));
    }

    [Fact]
    public void Detection_picks_the_fullscreen_game_and_skips_utilities_launchers_and_browsers()
    {
        var service = new GameProcessService();
        // Coverage comes from real window bounds, so the full-screen game must win over
        // every other titled window: shell, our own app, launcher, browser and overlay.
        var candidates = new GameWindowCandidate[]
        {
            new(10, "explorer", "Program Manager", 0.99),
            new(11, "GameTuner.App", "GameTuner · 离线性能工作台", 0.85),
            new(12, "steam", "Steam", 0.80),
            new(13, "steamwebhelper", "Steam 好友列表", 0.35),
            new(14, "Tabbit Browser", "影之诗 … 直播平台", 1.00),
            new(16, "NVIDIA Overlay", "NVIDIA GeForce Overlay", 1.00),
            new(17, "magpie", "magpie", 1.00),
            new(18, "SomeGame", "Some Game - Main Menu", 1.00)
        };

        var detected = service.DetectRunningGame(candidates);

        Assert.NotNull(detected);
        Assert.Equal(18, detected!.Id);
        Assert.Equal("SomeGame", detected.ProcessName);
        // the browser and overlay report full coverage too: they must be excluded by name,
        // not by size, or the player would be shown a web page as their game.
        Assert.DoesNotContain(detected.ProcessName, new[] { "Tabbit Browser", "NVIDIA Overlay", "magpie" });
    }

    [Fact]
    public void Ambiguous_windowed_programs_require_player_selection_and_are_explained()
    {
        var service = new GameProcessService();
        var candidates = new GameWindowCandidate[]
        {
            new(30, "SomeWindowedGame", "Some Game - Windowed", 0.35),
            new(31, "AnotherProgram", "Another Window", 0.4)
        };

        Assert.Null(service.DetectRunningGame(candidates));
        var message = service.DescribeDetection(candidates);
        Assert.Contains("窗口", message);
        Assert.Contains("选择游戏进程", message);
    }

    [Fact]
    public async Task Startup_detection_fills_the_game_and_process_without_player_input()
    {
        // The window used to advertise "正在检测游戏" while doing no detection at all.
        // This proves InitializeAsync really resolves a running game on its own.
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "GameTuner-Detect", Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        try
        {
            var capture = new FakeCaptureService(paths);
            using var session = new DesktopSessionController(
                new MainViewModel(new LogAnalysisService(), new JsonFileStore(), paths),
                new FakeConfigurationProvider(), capture, new FakeGameProcessService());

            Assert.Equal("", session.ViewModel.Game);
            Assert.Contains("选择", session.ProcessStatus);

            await session.InitializeAsync();

            Assert.Equal("SomeGame", session.ViewModel.Game);
            Assert.Contains("SomeGame", session.ProcessStatus);
            Assert.Contains("检测到", session.ProcessStatus);
        }
        finally { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true); }
    }

    [Fact]
    public async Task Startup_detection_does_not_overwrite_an_existing_comparison()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "GameTuner-DetectKeep", Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        try
        {
            var capture = new FakeCaptureService(paths);
            using var session = new DesktopSessionController(
                new MainViewModel(new LogAnalysisService(), new JsonFileStore(), paths),
                new FakeConfigurationProvider(), capture, new FakeGameProcessService());
            // A real round makes the session non-empty; capture needs a live process.
            using var real = Process.GetCurrentProcess();
            session.SelectProcess(new GameProcessInfo(real.Id, "PlayerChosenGame", "Player Chosen"));
            session.StartCapture();
            await session.StopAndAnalyzeAsync();

            await session.InitializeAsync();

            Assert.Equal("PlayerChosenGame", session.ViewModel.Game);
        }
        finally { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true); }
    }

    [Fact]
    public void Detection_reports_why_nothing_was_detected_instead_of_guessing()
    {
        var service = new GameProcessService();
        var onlyChrome = new GameWindowCandidate[] { new(20, "chrome", "新标签页", 1.0) };
        Assert.Null(service.DetectRunningGame(onlyChrome));

        var launcherOnly = new GameWindowCandidate[] { new(21, "steam", "Steam", 0.9) };
        Assert.Null(service.DetectRunningGame(launcherOnly));
        // The message must name the launcher case rather than claim a game was found.
        Assert.Contains("启动器", service.DescribeDetection(launcherOnly));

        var empty = Array.Empty<GameWindowCandidate>();
        Assert.Null(service.DetectRunningGame(empty));
        Assert.Contains("没有检测到", service.DescribeDetection(empty));
    }
    [Fact]
    public void Filter_matches_process_pid_and_window_title_without_losing_selection_capability()
    {
        var picker = new ProcessPickerViewModel(() =>
        [
            new(42, "mygame", "My Game"),
            new(77, "other", "Editor")
        ]);

        picker.Filter = "mygame";
        Assert.Contains(picker.Processes, item => item.Id == 42);
        picker.Filter = "77";
        var selected = Assert.Single(picker.Processes);
        Assert.Equal(77, selected.Id);
        picker.SelectedProcess = selected;
        Assert.Same(selected, picker.SelectedProcess);
        picker.Filter = "editor";
        Assert.DoesNotContain(picker.Processes, item => item.Id == 42);
        Assert.Contains(picker.Processes, item => item.Id == 77);
    }

    [Fact]
    public void Processes_put_visible_windows_first_then_sort_by_process_name()
    {
        var picker = new ProcessPickerViewModel(() =>
        [
            new(9, "zeta", ""),
            new(8, "beta", "Editor"),
            new(7, "alpha", ""),
            new(6, "Alpha", "Game")
        ]);

        Assert.Equal([6, 8, 7, 9], picker.Processes.Select(process => process.Id));
    }

    [Fact]
    public void Refresh_replaces_the_unfiltered_snapshot_and_reapplies_the_filter()
    {
        var snapshot = (IReadOnlyList<GameProcessInfo>)
        [
            new(42, "mygame", "My Game"),
            new(77, "other", "Editor")
        ];
        var picker = new ProcessPickerViewModel(() => snapshot)
        {
            Filter = "game"
        };
        Assert.Equal([42], picker.Processes.Select(process => process.Id));

        snapshot =
        [
            new(88, "other", "Editor"),
            new(99, "replacement", "Replacement Game")
        ];
        picker.Refresh();

        Assert.Equal([99], picker.Processes.Select(process => process.Id));
        picker.Filter = "";
        Assert.Equal([88, 99], picker.Processes.Select(process => process.Id));
    }

    [Fact]
    public void Filter_matching_is_case_insensitive_for_process_name_and_window_title()
    {
        var picker = new ProcessPickerViewModel(() =>
        [
            new(42, "MyGame", "Launch Window"),
            new(77, "other", "Editor")
        ]);

        picker.Filter = "mYgAmE";
        Assert.Equal([42], picker.Processes.Select(process => process.Id));
        picker.Filter = "lAuNcH";
        Assert.Equal([42], picker.Processes.Select(process => process.Id));
    }

    [Fact]
    public void Filter_clears_selection_when_the_selected_process_is_hidden()
    {
        var picker = new ProcessPickerViewModel(() =>
        [
            new(42, "mygame", "My Game"),
            new(77, "other", "Editor")
        ]);
        picker.SelectedProcess = picker.Processes.Single(process => process.Id == 42);

        picker.Filter = "editor";

        Assert.Null(picker.SelectedProcess);
    }

    [Fact]
    public void Refresh_remaps_selection_to_the_visible_record_with_the_same_pid()
    {
        var snapshot = (IReadOnlyList<GameProcessInfo>)
        [
            new(42, "mygame", "Old Window"),
            new(77, "other", "Editor")
        ];
        var picker = new ProcessPickerViewModel(() => snapshot)
        {
            Filter = "game"
        };
        var original = picker.Processes.Single();
        picker.SelectedProcess = original;
        var replacement = new GameProcessInfo(42, "mygame", "New Window");
        snapshot = [replacement, new GameProcessInfo(77, "other", "Editor")];

        picker.Refresh();

        Assert.NotSame(original, picker.SelectedProcess);
        Assert.Same(replacement, picker.SelectedProcess);
        Assert.Same(replacement, Assert.Single(picker.Processes));
    }

    [Fact]
    public void Refresh_clears_selection_when_the_selected_pid_is_removed()
    {
        var snapshot = (IReadOnlyList<GameProcessInfo>)
        [
            new(42, "mygame", "My Game"),
            new(77, "other", "Editor")
        ];
        var picker = new ProcessPickerViewModel(() => snapshot);
        picker.SelectedProcess = picker.Processes.Single(process => process.Id == 42);
        snapshot = [new GameProcessInfo(77, "other", "Editor")];

        picker.Refresh();

        Assert.Null(picker.SelectedProcess);
    }

    [Fact]
    public void Refresh_clears_selection_when_the_same_pid_is_hidden_by_the_filter()
    {
        var snapshot = (IReadOnlyList<GameProcessInfo>)
        [
            new(42, "mygame", "My Game"),
            new(77, "other", "Editor")
        ];
        var picker = new ProcessPickerViewModel(() => snapshot)
        {
            Filter = "game"
        };
        picker.SelectedProcess = picker.Processes.Single(process => process.Id == 42);
        snapshot =
        [
            new GameProcessInfo(42, "other", "Editor"),
            new GameProcessInfo(99, "replacement", "Replacement Game")
        ];

        picker.Refresh();

        Assert.Equal([99], picker.Processes.Select(process => process.Id));
        Assert.Null(picker.SelectedProcess);
    }

    [Fact]
    public void Process_service_returns_a_sorted_snapshot_containing_the_current_process()
    {
        var processes = new GameProcessService().GetProcesses();

        Assert.Contains(processes, process => process.Id == Environment.ProcessId);
        for (var index = 1; index < processes.Count; index++)
        {
            var previous = processes[index - 1];
            var current = processes[index];
            if (string.IsNullOrEmpty(previous.WindowTitle) != string.IsNullOrEmpty(current.WindowTitle))
            {
                Assert.False(string.IsNullOrEmpty(previous.WindowTitle));
            }
            else if (string.IsNullOrEmpty(previous.WindowTitle) == string.IsNullOrEmpty(current.WindowTitle))
            {
                Assert.True(string.Compare(previous.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase) <= 0);
            }
        }
    }
}
