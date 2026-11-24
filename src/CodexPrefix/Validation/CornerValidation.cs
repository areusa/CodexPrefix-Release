using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexPrefix;

// 离屏测试本程序底框；不抓取桌面，不依赖任何外部窗口。
internal static class CornerValidation
{
    static (BitmapSource Image, int Difference, int SoftPixels, bool ClearCorners) Sample(Border border, double width, double height, double radius, double scale)
    {
        int w = (int)Math.Round(width * scale), h = (int)Math.Round(height * scale);
        VisualTreeHelper.SetRootDpi(border, new DpiScale(scale, scale));
        border.UseLayoutRounding = false;
        border.Measure(new Size(w / scale, h / scale));
        border.Arrange(new Rect(0, 0, w / scale, h / scale));
        var image = new RenderTargetBitmap(w, h, scale * 96, scale * 96, PixelFormats.Pbgra32);
        image.Render(border); image.Freeze();
        var pixels = new byte[w * h * 4]; image.CopyPixels(pixels, w * 4, 0);
        int difference = 0, soft = 0, n = (int)Math.Ceiling(radius * scale) + 1;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            int p = (y * w + x) * 4;
            if (pixels[p + 3] > 0 && pixels[p + 3] < 255) soft++;
            foreach (int q in new[] { (y * w + w - 1 - x) * 4, ((h - 1 - y) * w + x) * 4, ((h - 1 - y) * w + w - 1 - x) * 4 })
                for (int c = 0; c < 4; c++) difference = Math.Max(difference, Math.Abs(pixels[p + c] - pixels[q + c]));
        }
        bool clear = new[] { 0, w - 1, (h - 1) * w, w * h - 1 }.All(p => pixels[p * 4 + 3] == 0);
        return (image, difference, soft, clear);
    }
    internal static void Run(Action<bool, string> check)
    {
        var report = new List<object>();
        var results = new List<(bool Pass, string Text)>();
        foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2 })
        foreach (var shape in new[] { (Name: "小条外框", W: 344d, H: 40d, R: 9d, T: 1d), (Name: "交互底框", W: 30d, H: 30d, R: 8d, T: 0d), (Name: "完整窗口", W: 382d, H: 166d, R: 14d, T: 1d) })
        {
            Border Make(bool smooth)
            {
                Border b = smooth ? new SmoothBorder() : new Border { SnapsToDevicePixels = true };
                b.Background = new SolidColorBrush(Color.FromRgb(20, 20, 22));
                b.BorderBrush = new SolidColorBrush(Color.FromRgb(52, 52, 58));
                b.BorderThickness = new Thickness(shape.T); b.CornerRadius = new CornerRadius(shape.R);
                return b;
            }
            var before = Sample(Make(false), shape.W, shape.H, shape.R, scale);
            var after = Sample(Make(true), shape.W, shape.H, shape.R, scale);
            report.Add(new { shape = shape.Name, dpi = scale * 96, before = new { before.Difference, before.SoftPixels }, after = new { after.Difference, after.SoftPixels, after.ClearCorners } });
            results.Add((after.ClearCorners, $"{shape.Name} {scale * 96} DPI 的四个极角必须透明"));
            results.Add((after.SoftPixels > 0, $"{shape.Name} {scale * 96} DPI 的曲线边缘必须保留部分透明像素"));
            results.Add((after.Difference <= 4, $"{shape.Name} {scale * 96} DPI 四角镜像的通道差不超过 4/255（实际 {after.Difference}）"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(after.Image));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, $"corner-{shape.Name}-{scale * 96}.png")); encoder.Save(file);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "corner-validation.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        foreach (var result in results) check(result.Pass, result.Text);
    }
}
