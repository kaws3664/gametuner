using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using GameTuner.App.Desktop;
using GameTuner.App.Processes;
using GameTuner.App.ViewModels;
using GameTuner.Core.Infrastructure;

namespace GameTuner.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DesktopSessionController _session;
    private readonly INativeDialogService _dialogs;
    private readonly string _themeSettings;
    private readonly DispatcherTimer _refreshTimer;
    private bool _closed;

    public MainWindow(DesktopSessionController session, INativeDialogService? dialogs = null, AppPaths? paths = null)
    {
        _session = session;
        Model = session.ViewModel;
        _themeSettings = (paths ?? new AppPaths()).SettingsFile;
        if (System.Windows.Application.Current is App app) app.ApplyTheme(_themeSettings);
        _dialogs = dialogs ?? new NativeDialogService(this);
        ThemeCommand = new DelegateCommand(ToggleTheme);
        ProcessCommand = new DelegateCommand(() => { if (_dialogs.PickProcess() is { } selected) _session.SelectProcess(selected); });
        ImportLogCommand = new NativeAsyncCommand(ImportLog, () => !_session.IsBusy, ReportError);
        ImportSettingsCommand = new NativeAsyncCommand(ImportSettings, () => !_session.IsBusy, ReportError);
        LoadCommand = new NativeAsyncCommand(Load, () => !_session.IsBusy, ReportError);
        NewCommand = new DelegateCommand(New, () => !_session.IsBusy);
        FillCommand = new DelegateCommand(Model.FillSelectedInputs);
        PreviewCommand = new DelegateCommand(Preview, () => Model.SelectedRound is not null);
        ExportCommand = new NativeAsyncCommand(Export, () => Model.SelectedRound is not null && !_session.IsBusy, ReportError);
        StartCaptureCommand = new NativeAsyncCommand(() => _session.StartCaptureAsync(),
            () => !_session.IsBusy && !_session.IsRecording && Model.CanImport, ReportError);
        StopAndAnalyzeCommand = new NativeAsyncCommand(() => _session.StopAndAnalyzeAsync(),
            () => !_session.IsBusy && _session.HasPendingCapture, ReportError);
        DataContext = this;
        InitializeComponent();
        _session.PropertyChanged += (_, _) => RefreshCommands();
        Model.PropertyChanged += (_, _) => RefreshCommands();
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            try { await _session.RefreshAsync(); }
            catch (Exception exception) { ReportError(exception); }
        };
        Loaded += (_, _) => _refreshTimer.Start();
        Closed += (_, _) =>
        {
            if (_closed) return;
            _closed = true;
            _refreshTimer.Stop();
            _session.Dispose();
        };
    }

    public MainViewModel Model { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string ProcessStatus => _session.ProcessStatus;
    public string CaptureStatus => _session.CaptureStatus;
    public string ConfigurationStatus => _session.ConfigurationStatus;
    public string ConfigurationSource => _session.ConfigurationSource;
    public bool GraphicsReadOnly => _session.GraphicsReadOnly;
    public string OperationStatus => _session.OperationStatus;
    public DelegateCommand ThemeCommand { get; }
    public DelegateCommand ProcessCommand { get; }
    public NativeAsyncCommand ImportLogCommand { get; }
    public NativeAsyncCommand ImportSettingsCommand { get; }
    public NativeAsyncCommand LoadCommand { get; }
    public DelegateCommand NewCommand { get; }
    public DelegateCommand FillCommand { get; }
    public DelegateCommand PreviewCommand { get; }
    public NativeAsyncCommand ExportCommand { get; }
    public NativeAsyncCommand StartCaptureCommand { get; }
    public NativeAsyncCommand StopAndAnalyzeCommand { get; }

    private void ToggleTheme()
    {
        if (System.Windows.Application.Current is not App app) throw new InvalidOperationException("主题服务尚未初始化。");
        app.SetTheme(!app.IsDarkTheme);
        Directory.CreateDirectory(Path.GetDirectoryName(_themeSettings)!);
        File.WriteAllText(_themeSettings, app.IsDarkTheme ? "Dark" : "Light");
    }

    private async Task ImportLog()
    {
        if (_dialogs.OpenLog() is { } path) await _session.ImportExternalLogAsync(path);
    }

    private async Task ImportSettings()
    {
        if (_dialogs.OpenSettings() is { } path) await _session.ImportGraphicsSettingsAsync(path);
    }

    private async Task Load()
    {
        if (_dialogs.OpenPlan(Model.PlansDirectory) is { } path)
            await _session.RunActionAsync(async () => { await Model.LoadPlanAsync(path); _session.OperationStatus = Model.Status; });
    }

    private void New()
    {
        if (Model.HasRounds && !_dialogs.ConfirmNewComparison()) return;
        Model.NewGoal();
        SharePreview.Visibility = Visibility.Collapsed;
    }

    private void Preview()
    {
        SharePreview.Visibility = SharePreview.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        SharePreview.GetBindingExpression(DataContextProperty)?.UpdateTarget();
    }

    private async Task Export()
    {
        if (_dialogs.SavePng() is not { } path) return;
        await _session.RunActionAsync(() =>
        {
            try { ShareCard.ExportPng(Model, path); Model.ReportExport(path); }
            catch (Exception exception) { _session.OperationStatus = "分享图导出失败：" + exception.Message; }
            return Task.CompletedTask;
        });
    }

    private void RefreshCommands()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshCommands);
            return;
        }
        foreach (var command in new DelegateCommand[] { ProcessCommand, NewCommand, FillCommand, PreviewCommand }) command.RaiseCanExecuteChanged();
        ImportLogCommand.RaiseCanExecuteChanged();
        ImportSettingsCommand.RaiseCanExecuteChanged();
        LoadCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        StartCaptureCommand.RaiseCanExecuteChanged();
        StopAndAnalyzeCommand.RaiseCanExecuteChanged();
        foreach (var name in new[] { nameof(ProcessStatus), nameof(CaptureStatus), nameof(ConfigurationStatus),
            nameof(ConfigurationSource), nameof(GraphicsReadOnly), nameof(OperationStatus) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void ReportError(Exception exception) =>
        _session.OperationStatus = "操作未完成：" + exception.Message;
}
