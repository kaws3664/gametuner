using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameTuner.App;
using GameTuner.App.Desktop;
using GameTuner.App.Processes;
using GameTuner.App.ViewModels;
using GameTuner.Core.Analysis;
using GameTuner.Core.Infrastructure;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace GameTuner.UiTests;

public sealed class DesktopWindowTests
{
    [Fact]
    public void Theme_switch_changes_real_window_resources_and_persists_across_app_restarts() => Sta.Run(() =>
    {
        var capture = new FakeCaptureService(CreatePaths(out var paths));
        using var session = CreateSession(paths, capture);
        var window = new MainWindow(session, paths: paths);
        var light = Assert.IsType<SolidColorBrush>(window.FindResource("PanelBrush")).Color;

        window.ThemeCommand.Execute(null);

        var dark = Assert.IsType<SolidColorBrush>(window.FindResource("PanelBrush")).Color;
        Assert.NotEqual(light, dark);
        Assert.Equal("Dark", File.ReadAllText(paths.SettingsFile).Trim());
        window.Close();

        var app = Assert.IsType<GameTuner.App.App>(System.Windows.Application.Current);
        app.ApplyTheme(paths.SettingsFile);
        Assert.Equal(dark, Assert.IsType<SolidColorBrush>(app.Resources["PanelBrush"]).Color);
    });
    [Fact]
    public void MainWindow_contains_the_native_workspace_and_required_dimensions() => Sta.Run(() =>
    {
        var capture = new FakeCaptureService(CreatePaths(out var paths));
        using var session = CreateSession(paths, capture);
        var window = new MainWindow(session);

        Assert.Equal("GameTuner · 离线性能工作台", window.Title);
        Assert.NotNull(window.FindName("ProcessButton"));
        Assert.NotNull(window.FindName("CaptureButton"));
        Assert.NotNull(window.FindName("RoundsList"));
        Assert.NotNull(window.FindName("ExportButton"));
        Assert.NotNull(window.FindName("ThemeButton"));
        Assert.NotNull(window.Icon);
        Assert.Equal(960, window.MinWidth);
        Assert.Equal(680, window.MinHeight);
        Assert.Equal(1180, window.Width);
        Assert.Equal(780, window.Height);
        Assert.IsType<ScrollViewer>(window.FindName("WorkspaceScroll"));
    });

    [Fact]
    public void Closing_main_window_disposes_capture_once_without_starting_http_or_browser() => Sta.Run(() =>
    {
        var capture = new FakeCaptureService(CreatePaths(out var paths));
        var session = CreateSession(paths, capture);
        var window = new MainWindow(session);

        // Production composition constructs only MainWindow/session; no listener/browser is started.
        window.Close();
        session.Dispose();

        Assert.Equal(1, capture.DisposeCallCount);
        Assert.Empty(capture.StartedPaths);
        Assert.False(capture.Snapshot().IsRecording);
    });

    [Fact]
    public void Process_picker_supports_search_select_double_click_enter_and_escape() => Sta.Run(() =>
    {
        var picker = new ProcessPickerViewModel(() => [new(42, "mygame", "My Game")]);
        var window = new ProcessPickerWindow(picker);
        window.Show();
        var search = Assert.IsType<TextBox>(window.FindName("SearchBox"));
        var list = Assert.IsType<ListBox>(window.FindName("ProcessList"));
        var select = Assert.IsType<Button>(window.FindName("SelectButton"));
        Assert.NotNull(window.FindName("RefreshButton"));
        Assert.NotNull(window.FindName("CancelButton"));

        search.Text = "42";
        list.SelectedIndex = 0;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
        Assert.Equal(42, window.SelectedProcess?.Id);
        Assert.True(select.IsEnabled);
        window.Close();
    });

    [Fact]
    public void Process_picker_modal_enter_double_click_and_escape_return_real_results() => Sta.Run(() =>
    {
        var enterPicker = new ProcessPickerViewModel(() => [new(42, "mygame", "My Game"), new(77, "other", "Other")]);
        var enterWindow = new ProcessPickerWindow(enterPicker);
        object? enterResult = null;
        enterWindow.Loaded += (_, _) => enterWindow.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var search = (TextBox)enterWindow.FindName("SearchBox");
                var list = (ListBox)enterWindow.FindName("ProcessList");
                Assert.True(search.IsKeyboardFocused);
                search.Text = "42";
                list.SelectedIndex = 0;
                search.RaiseEvent(KeyEvent(search, Key.Enter, preview: true));
                enterResult = enterWindow.DialogResult;
            }
            catch (Exception exception)
            {
                enterResult = exception;
                enterWindow.Close();
            }
        });
        enterWindow.ShowDialog();
        Assert.True(enterResult is true, $"Unexpected modal Enter result: {enterResult}");
        Assert.Equal(42, enterPicker.SelectedProcess?.Id);

        var doubleClickPicker = new ProcessPickerViewModel(() => [new(42, "mygame", "My Game")]);
        var doubleClickWindow = new ProcessPickerWindow(doubleClickPicker);
        object? doubleClickResult = null;
        doubleClickWindow.Loaded += (_, _) => doubleClickWindow.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var list = (ListBox)doubleClickWindow.FindName("ProcessList");
                list.SelectedIndex = 0;
                var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = Control.MouseDoubleClickEvent
                };
                typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!
                    .SetValue(doubleClick, 2);
                list.RaiseEvent(doubleClick);
                doubleClickResult = doubleClickWindow.DialogResult;
            }
            catch (Exception exception)
            {
                doubleClickResult = exception;
                doubleClickWindow.Close();
            }
        });
        doubleClickWindow.ShowDialog();
        Assert.True(doubleClickResult is true, $"Unexpected modal double-click result: {doubleClickResult}");
        Assert.Equal(42, doubleClickPicker.SelectedProcess?.Id);

        var escapePicker = new ProcessPickerViewModel(() => [new(42, "mygame", "My Game")]);
        var escapeWindow = new ProcessPickerWindow(escapePicker);
        object? escapeResult = true;
        escapeWindow.Loaded += (_, _) => escapeWindow.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var search = (TextBox)escapeWindow.FindName("SearchBox");
                search.RaiseEvent(KeyEvent(search, Key.Escape, preview: true));
                escapeResult = escapeWindow.DialogResult;
            }
            catch (Exception exception)
            {
                escapeResult = exception;
                escapeWindow.Close();
            }
        });
        Assert.False(escapeWindow.ShowDialog());
        Assert.True(escapeResult is false, $"Unexpected modal Escape result: {escapeResult}");
    });

    [Fact]
    public void Native_async_ui_execution_reports_delegate_and_dialog_failures() => Sta.Run(async () =>
    {
        Exception? reported = null;
        var command = new NativeAsyncCommand(() => Task.FromException(new InvalidDataException("delegate failed")),
            errorHandler: exception => reported = exception);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        command.CanExecuteChanged += (_, _) => { if (command.CanExecute(null)) completed.TrySetResult(); };

        ((ICommand)command).Execute(null);
        await completed.Task;

        var failure = Assert.IsType<InvalidDataException>(reported);
        Assert.Equal("delegate failed", failure.Message);

        var capture = new FakeCaptureService(CreatePaths(out var paths));
        var settings = Path.Combine(paths.Root, "broken-settings.json");
        File.WriteAllText(settings, "{\"graphics\":{\"nested\":true}}");
        using var session = CreateSession(paths, capture);
        var window = new MainWindow(session, new FakeNativeDialogs { Settings = settings });
        completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ImportSettingsCommand.CanExecuteChanged += (_, _) =>
        {
            if (window.ImportSettingsCommand.CanExecute(null)) completed.TrySetResult();
        };

        ((ICommand)window.ImportSettingsCommand).Execute(null);
        await completed.Task;

        Assert.Contains("操作未完成", session.OperationStatus);
        Assert.Contains("不是标量", session.OperationStatus);
        Assert.False(session.IsBusy);
        window.Close();
    });

    [Fact]
    public void CtrlO_imports_and_CtrlS_saves_through_the_actual_key_bindings() => Sta.Run(async () =>
    {
        var capture = new FakeCaptureService(CreatePaths(out var paths));
        var log = Path.Combine(paths.Logs, "keyboard.csv");
        File.WriteAllText(log, "Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low\n0,77,50,40\n300,77,50,40\n");
        var dialogs = new FakeNativeDialogs { Log = log };
        using var session = CreateSession(paths, capture);
        var window = new MainWindow(session, dialogs);
        var importBinding = Assert.Single(window.InputBindings.Cast<KeyBinding>(), binding => binding.Key == Key.O);
        var saveBinding = Assert.Single(window.InputBindings.Cast<KeyBinding>(), binding => binding.Key == Key.S);
        Assert.Equal(ModifierKeys.Control, importBinding.Modifiers);
        Assert.Equal(ModifierKeys.Control, saveBinding.Modifiers);
        Assert.Same(window.ImportLogCommand, importBinding.Command);
        Assert.Same(session.ViewModel.SaveCommand, saveBinding.Command);
        window.Show();
        var import = Assert.IsType<Button>(window.FindName("ImportButton"));
        import.Focus();
        Assert.True(import.IsKeyboardFocused);

        import.RaiseEvent(KeyEvent(import, Key.O, ModifierKeys.Control));
        await PumpAsync(() => session.ViewModel.Rounds.Count == 1 && !session.IsBusy);

        var save = Assert.IsType<Button>(window.FindName("SaveButton"));
        save.Focus();
        Assert.True(save.IsKeyboardFocused);
        save.RaiseEvent(KeyEvent(save, Key.S, ModifierKeys.Control));
        await PumpAsync(() => Directory.Exists(session.ViewModel.PlansDirectory)
            && Directory.GetFiles(session.ViewModel.PlansDirectory, "*.json").Length == 1);

        Assert.Single(session.ViewModel.Rounds);
        Assert.Single(Directory.GetFiles(session.ViewModel.PlansDirectory, "*.json"));
        window.Close();
    });

    [Fact]
    public void Native_capture_and_external_import_commands_reach_the_real_session() => Sta.Run(async () =>
    {
        var capture = new FakeCaptureService(CreatePaths(out var paths));
        using var session = CreateSession(paths, capture);
        var window = new MainWindow(session);
        session.SelectProcess(new GameProcessInfo(Environment.ProcessId, Process.GetCurrentProcess().ProcessName, "Fixture"));

        await window.StartCaptureCommand.ExecuteAsync();
        Assert.Single(capture.StartedPaths);
        Assert.True(capture.Snapshot().IsRecording);
        await window.StopAndAnalyzeCommand.ExecuteAsync();
        Assert.True(capture.StopCallCount > 0, $"Stop not called; status={session.OperationStatus}");
        Assert.DoesNotContain("操作未完成", session.OperationStatus);
        Assert.True(session.ViewModel.Rounds.Count == 1, session.OperationStatus);
        var log = Path.Combine(paths.Logs, "external.csv");
        File.WriteAllText(log, FormattableString.Invariant(
            $"Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low,CPU Utilization(%),GPU1 Utilization(%)\n0,{Environment.ProcessId},66,55,55,97\n300,{Environment.ProcessId},66,55,55,97\n"));
        await session.ImportExternalLogAsync(log);

        Assert.Single(capture.StartedProcessIds);
        Assert.Equal(Environment.ProcessId, capture.StartedProcessIds[0]);
        Assert.Equal(2, session.ViewModel.Rounds.Count);
        Assert.Equal(log, session.ViewModel.SelectedRound?.Round.Report.SourcePath);
        window.Close();
    });

    [Fact]
    public void Native_dialog_commands_import_save_load_preview_export_and_start_new() => Sta.Run(async () =>
    {
        var capture = new FakeCaptureService(CreatePaths(out var paths));
        var log = Path.Combine(paths.Logs, "native-import.csv");
        File.WriteAllText(log, "Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low\n0,77,50,40\n300,77,50,40\n");
        var settings = Path.Combine(paths.Root, "settings.json");
        File.WriteAllText(settings, "{\"rayTracing\":true}");
        var png = Path.Combine(paths.Root, "share.png");
        var dialogs = new FakeNativeDialogs { Log = log, Settings = settings, Png = png };
        using var session = CreateSession(paths, capture);
        var window = new MainWindow(session, dialogs) { Width = 1180, Height = 780 };

        await window.ImportLogCommand.ExecuteAsync();
        Assert.Single(session.ViewModel.Rounds);
        await window.ImportSettingsCommand.ExecuteAsync();
        Assert.Equal("rayTracing：true", session.ViewModel.GraphicsSettings);
        await session.ViewModel.UpdateDetailsAsync();
        session.ViewModel.SaveCommand.Execute(null);
        await PumpAsync(() => !session.ViewModel.IsBusy);
        var saved = Assert.Single(Directory.GetFiles(session.ViewModel.PlansDirectory, "*.json"));
        dialogs.Plan = saved;

        window.PreviewCommand.Execute(null);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("SharePreview")).Visibility);
        await window.ExportCommand.ExecuteAsync();
        Assert.True(new FileInfo(png).Length > 1000);
        await window.LoadCommand.ExecuteAsync();
        Assert.Single(session.ViewModel.Rounds);
        window.NewCommand.Execute(null);
        Assert.Empty(session.ViewModel.Rounds);
        Assert.Empty(capture.StartedPaths);
        window.Close();
        Directory.Delete(paths.Root, recursive: true);
    });

    [Fact]
    public void Native_window_renders_at_required_sizes_and_process_picker_handles_escape() => Sta.Run(() =>
    {
        var output = Environment.GetEnvironmentVariable("GAMETUNER_UI_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "GameTuner-NativeScreens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var capture = new FakeCaptureService(CreatePaths(out var paths));
        using var session = CreateSession(paths, capture);
        var window = new MainWindow(session, paths: paths);
        RenderWindow(window, Path.Combine(output, "workspace-light-1180x780.png"));
        window.Width = 960;
        window.Height = 680;
        RenderWindow(window, Path.Combine(output, "workspace-light-960x680.png"));
        window.ThemeCommand.Execute(null);
        window.Width = 1180;
        window.Height = 780;
        RenderWindow(window, Path.Combine(output, "workspace-dark-1180x780.png"));
        window.Width = 960;
        window.Height = 680;
        RenderWindow(window, Path.Combine(output, "workspace-dark-960x680.png"));
        var picker = new ProcessPickerWindow(new ProcessPickerViewModel(() => [new(42, "mygame", "My Game")]));
        RenderWindow(picker, Path.Combine(output, "process-picker-dark.png"));
        picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
        window.Close();        Assert.False(picker.IsVisible);
        Assert.Null(picker.SelectedProcess);
        Assert.True(File.Exists(Path.Combine(output, "workspace-light-1180x780.png")));
        Assert.True(File.Exists(Path.Combine(output, "workspace-dark-960x680.png")));
        Directory.Delete(paths.Root, recursive: true);
    });

    private static AppPaths CreatePaths(out AppPaths paths)
    {
        paths = new AppPaths(Path.Combine(Path.GetTempPath(), "GameTuner-WindowTests", Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        return paths;
    }

    private static KeyEventArgs KeyEvent(
        Visual source, Key key, ModifierKeys modifiers = ModifierKeys.None, bool preview = false)
    {
        return new KeyEventArgs(new TestKeyboardDevice(modifiers), PresentationSource.FromVisual(source), 0, key)
        {
            RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent
        };
    }

    private sealed class TestKeyboardDevice(ModifierKeys modifiers) : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) =>
            modifiers != ModifierKeys.None && key is Key.LeftCtrl or Key.RightCtrl
                ? KeyStates.Down : KeyStates.None;
    }

    private static DesktopSessionController CreateSession(AppPaths paths, FakeCaptureService capture) =>
        new(new MainViewModel(new LogAnalysisService(), new JsonFileStore(), paths),
            new FakeConfigurationProvider(), capture);

    private static async Task PumpAsync(Func<bool> completed)
    {
        while (!completed()) await Task.Delay(10);
    }

    private static void RenderWindow(Window window, string path)
    {
        window.Show();
        window.UpdateLayout();
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class FakeNativeDialogs : INativeDialogService
    {
        public string Log { get; init; } = "";
        public string Settings { get; init; } = "";
        public string Plan { get; set; } = "";
        public string Png { get; init; } = "";
        public GameProcessInfo? PickProcess() => null;
        public string? OpenLog() => Log;
        public string? OpenSettings() => Settings;
        public string? OpenPlan(string initialDirectory) => Plan;
        public string? SavePng() => Png;
        public bool ConfirmNewComparison() => true;
    }

}

internal static class Sta
{
    private static readonly object ApplicationGate = new();
    private static bool _applicationCreated;

    public static void Run(Action action) => RunAsync(() => { action(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    public static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    lock (ApplicationGate)
                    {
                        if (!_applicationCreated)
                        {
                            var app = new GameTuner.App.App();
                            app.InitializeComponent();
                            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                            _applicationCreated = true;
                        }
                    }
                }
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var context = new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher);
                SynchronizationContext.SetSynchronizationContext(context);
                _ = action().ContinueWith(completed =>
                {
                    if (completed.Exception is not null) completion.SetException(completed.Exception.InnerExceptions);
                    else completion.SetResult();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                }, TaskScheduler.FromCurrentSynchronizationContext());
                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true, Name = "GameTuner native window test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
