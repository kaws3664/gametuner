using System.Windows;
using GameTuner.App.Processes;
using Microsoft.Win32;

namespace GameTuner.App.Desktop;

public sealed class NativeDialogService(Window owner) : INativeDialogService
{
    public GameProcessInfo? PickProcess()
    {
        var dialog = new ProcessPickerWindow(new ProcessPickerViewModel(new GameProcessService().GetProcesses)) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.SelectedProcess : null;
    }

    public string? OpenLog() => Open("选择性能日志", "性能日志 (*.csv;*.txt)|*.csv;*.txt|所有文件 (*.*)|*.*");
    public string? OpenSettings() => Open("只读导入结构化设置", "结构化设置 (*.json;*.xml;*.ini)|*.json;*.xml;*.ini");
    public string? OpenPlan(string initialDirectory) => Open("载入已保存的方案", "方案记录 (*.json)|*.json", initialDirectory);
    public string? SavePng()
    {
        var dialog = new SaveFileDialog { Title = "保存配置分享卡", Filter = "PNG 图片 (*.png)|*.png", AddExtension = true,
            FileName = $"GameTuner-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public bool ConfirmNewComparison() => MessageBox.Show(owner,
        "开始新对比？已保存的旧方案会保留，未保存的当前记录将被清除。", "新对比",
        MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private string? Open(string title, string filter, string initialDirectory = "")
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, InitialDirectory = initialDirectory };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
