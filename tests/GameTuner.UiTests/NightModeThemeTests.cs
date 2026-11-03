using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTuner.App;
using GameTuner.App.Processes;

namespace GameTuner.UiTests;

// Application theme resources are shared with the other native WPF tests.
[CollectionDefinition(nameof(NightModeThemeTests), DisableParallelization = true)]
public sealed class NightModeThemeCollection;

[Collection(nameof(NightModeThemeTests))]
public sealed class NightModeThemeTests
{
    [Theory]
    [InlineData(1180, 780)]
    [InlineData(960, 680)]
    public void Switching_to_night_mode_removes_white_gutters_and_updates_window_and_list_text(
        int width, int height) => Sta.Run(() =>
    {
        using var workspace = new Workspace();
        workspace.Model.Game = "NightModeTestData";
        var window = new MainWindow(workspace.Session, paths: workspace.Paths)
        {
            Width = width,
            Height = height
        };
        var picker = new ProcessPickerWindow(new ProcessPickerViewModel(
            () => [new(77, "NightModeTestData", "Theme regression test data")]));
        var app = Assert.IsType<GameTuner.App.App>(Application.Current);
        var output = Path.Combine(
            Environment.GetEnvironmentVariable("GAMETUNER_UI_ARTIFACTS")
                ?? Path.Combine(Path.GetTempPath(), "GameTuner-NativeScreens", Guid.NewGuid().ToString("N")),
            $"night-mode-{width}x{height}");
        Directory.CreateDirectory(output);

        try
        {
            Assert.Empty(workspace.Model.Rounds);
            Assert.False(app.IsDarkTheme);
            RenderWindow(window, Path.Combine(output, "native-workspace-light-test-data.png"));
            RenderWindow(picker, Path.Combine(output, "native-process-picker-light-test-data.png"));

            window.ThemeCommand.Execute(null);

            Assert.True(app.IsDarkTheme);
            var dark = RenderWindow(window, Path.Combine(output, "native-workspace-dark-test-data.png"));
            RenderWindow(picker, Path.Combine(output, "native-process-picker-dark-test-data.png"));
            // These gutters contained solid white in the real 49b6375 native renders.
            AssertNoNearWhitePixels(dark, new Int32Rect(0, 75, 384, 18));
            AssertNoNearWhitePixels(dark, new Int32Rect(0, 120, 16, 280));
            AssertThemedWindowAndListText(app, window, picker);

            window.ThemeCommand.Execute(null);

            Assert.False(app.IsDarkTheme);
            RenderWindow(window, Path.Combine(output, "native-workspace-restored-light-test-data.png"));
            RenderWindow(picker, Path.Combine(output, "native-process-picker-restored-light-test-data.png"));
            AssertThemedWindowAndListText(app, window, picker);
            Assert.Empty(workspace.Capture.StartedPaths);
        }
        finally
        {
            picker.Close();
            window.Close();
            app.SetTheme(false);
        }
    });

    private static void AssertThemedWindowAndListText(
        GameTuner.App.App app, MainWindow window, ProcessPickerWindow picker)
    {
        var text = Assert.IsType<SolidColorBrush>(app.Resources["TextBrush"]);
        Assert.Same(text, window.Foreground);
        Assert.Same(text, picker.Foreground);
        Assert.Same(app.Resources["BackdropBrush"], window.Background);
        Assert.Same(app.Resources["BackdropBrush"], picker.Background);
        Assert.Same(text, Assert.Single(VisualDescendants<TextBlock>(window),
            block => block.Text == "GAMETUNER").Foreground);
        Assert.Same(text, Assert.IsType<ListBox>(window.FindName("RoundsList")).Foreground);
        AssertListText(Assert.IsType<ListBox>(picker.FindName("ProcessList")), text);
    }

    private static void AssertListText(ListBox list, Brush text)
    {
        Assert.Same(text, list.Foreground);
        var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Same(text, item.Foreground);
        var label = Assert.Single(VisualDescendants<TextBlock>(item),
            block => block.FontWeight == FontWeights.SemiBold);
        Assert.False(string.IsNullOrWhiteSpace(label.Text));
        Assert.Same(text, label.Foreground);
    }

    private static void AssertNoNearWhitePixels(BitmapSource bitmap, Int32Rect region)
    {
        var stride = region.Width * 4;
        var pixels = new byte[stride * region.Height];
        bitmap.CopyPixels(region, pixels, stride, 0);
        var nearWhite = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] >= 240 && pixels[offset + 1] >= 240 && pixels[offset + 2] >= 240)
                nearWhite++;
        }

        Assert.True(nearWhite == 0,
            $"Native dark render {bitmap.PixelWidth}x{bitmap.PixelHeight}, region " +
            $"({region.X},{region.Y},{region.Width},{region.Height}): " +
            $"{nearWhite} near-white pixels (min(R,G,B) >= 240); expected 0.");
    }

    private static RenderTargetBitmap RenderWindow(Window window, string path)
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
        return bitmap;
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }
}
