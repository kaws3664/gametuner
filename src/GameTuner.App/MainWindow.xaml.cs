using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using GameTuner.App.ViewModels;

namespace GameTuner.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public void AllowClose() { }
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnBrowseLog(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择本次游戏性能日志",
            Filter = "性能日志 (*.csv;*.txt)|*.csv;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) ViewModel.LogPath = dialog.FileName;
    }

    private void OnGameChoice(object sender, RoutedEventArgs e) => ViewModel.Game = (string)((Button)sender).Tag;
    private void OnFpsChoice(object sender, RoutedEventArgs e) => ViewModel.TargetFps =
        double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
    private void OnRoundChoice(object sender, RoutedEventArgs e) => ViewModel.SelectedRound = (AnalysisRoundViewModel)((Button)sender).DataContext;
    private void OnShowShare(object sender, RoutedEventArgs e) => ViewModel.IsShareVisible = true;
    private void OnHideShare(object sender, RoutedEventArgs e) => ViewModel.IsShareVisible = false;
    private void OnFillSelected(object sender, RoutedEventArgs e) => ViewModel.FillSelectedInputs();
    private async void OnLoadPlan(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HasRounds && MessageBox.Show(this, "载入方案会替换当前未保存的对比记录。继续吗？",
                "载入方案", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var dialog = new OpenFileDialog
        {
            Title = "载入已保存的 FrameSmith 方案", Filter = "方案记录 (*.json)|*.json",
            InitialDirectory = ViewModel.PlansDirectory, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await ViewModel.LoadPlanAsync(dialog.FileName);
    }
    private void OnNewGoal(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HasRounds && MessageBox.Show(this, "开始新对比会清空当前未保存的记录。已保存的方案文件不受影响。继续吗？",
                "新建目标", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ViewModel.NewGoal();
    }
    private void OnExportShare(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存配置分享卡", Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"FrameSmith-{DateTime.Now:yyyyMMdd-HHmmss}.png", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ShareCard.ExportPng(ViewModel, dialog.FileName);
            ViewModel.ReportExport(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "图片保存失败：" + exception.Message, "FrameSmith", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MetricsGrid is not null)
        {
            MetricsGrid.Columns = ActualWidth < 1100 ? 2 : 4;
        }
    }

}
