using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameTuner.App.Processes;

namespace GameTuner.App;

public partial class ProcessPickerWindow : Window
{
    private readonly ProcessPickerViewModel _picker;
    private bool _modal;
    public ProcessPickerWindow(ProcessPickerViewModel picker)
    {
        _picker = picker;
        DataContext = picker;
        InitializeComponent();
        Loaded += (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); };
    }

    public GameProcessInfo? SelectedProcess => _picker.SelectedProcess;
    public new bool? ShowDialog()
    {
        _modal = true;
        return base.ShowDialog();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => _picker.Refresh();
    private void ProcessList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Choose();
    private void Select_Click(object sender, RoutedEventArgs e) => Choose();

    private void Choose()
    {
        if (_picker.SelectedProcess is null) return;
        if (_modal) DialogResult = true;
        else Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _picker.SelectedProcess is not null)
        {
            Choose();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_modal) DialogResult = false;
            else Close();
            e.Handled = true;
        }
    }
}
