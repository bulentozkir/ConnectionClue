using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConnectionClue.App;

/// <summary>Renders a window's content to PNG in software, so it works even when nothing is shown on screen.</summary>
internal static class Snapshot
{
    public static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement root || root.ActualWidth < 1) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        var size = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        // The window paints its own background (theme or backdrop); the content root is transparent, so paint it first.
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(window.TryFindResource("ApplicationBackgroundBrush") as Brush ?? window.Background ?? Brushes.White, null, size);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
