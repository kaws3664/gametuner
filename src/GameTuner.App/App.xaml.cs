using System.IO;
using System.Windows;
using System.Windows.Media;

namespace GameTuner.App;

public partial class App
{
    private static readonly (string Key, string Light, string Dark)[] ThemeColors =
    [
        ("InkBrush", "#F5F5F2", "#18201E"),
        ("PanelBrush", "#FFFFFF", "#202825"),
        ("PanelRaisedBrush", "#E8EFEB", "#29342F"),
        ("LineBrush", "#D9DEDA", "#3B4640"),
        ("TextBrush", "#1E2B26", "#EDF2EC"),
        ("MutedBrush", "#5F6E66", "#A9B7AD"),
        ("LimeBrush", "#176B57", "#8BCBB3"),
        ("OrangeBrush", "#91541F", "#E1AE70"),
        ("DangerBrush", "#A13E35", "#F09B8E"),
        ("AccentSoftBrush", "#E4F0E8", "#2C4136"),
        ("ControlBrush", "#FFFFFF", "#18201E"),
        ("OnAccentBrush", "#FFFFFF", "#12342A"),
        ("DisabledBrush", "#E9ECE8", "#29312D"),
        ("DisabledTextBrush", "#77827A", "#7D8B81")
    ];

    public bool IsDarkTheme { get; private set; }

    public void ApplyTheme(string settingsFile)
    {
        var dark = File.Exists(settingsFile) &&
            string.Equals(File.ReadAllText(settingsFile).Trim(), "Dark", StringComparison.OrdinalIgnoreCase);
        SetTheme(dark);
    }

    public void SetTheme(bool dark)
    {
        IsDarkTheme = dark;
        foreach (var (key, light, night) in ThemeColors)
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
        Resources["BackdropBrush"] = dark
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#121916"))
            : new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#F2F1EC"),
                (Color)ColorConverter.ConvertFromString("#E7ECE6"), 35);
    }
}