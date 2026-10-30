using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTuner.App.ViewModels;

namespace GameTuner.App;

public partial class ShareCard : UserControl
{
    public ShareCard() => InitializeComponent();

    public static void ExportPng(MainViewModel viewModel, string path)
    {
        if (viewModel.SelectedRound is null) throw new InvalidOperationException("请先导入日志，再保存分享图片。");
        var card = new ShareCard { DataContext = viewModel };
        card.Measure(new Size(880, double.PositiveInfinity));
        card.Arrange(new Rect(new Point(), card.DesiredSize));
        card.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1760, (int)Math.Ceiling(card.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(card);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }
}
