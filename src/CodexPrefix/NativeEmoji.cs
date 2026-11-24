using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;

namespace CodexPrefix;

internal static class NativeEmoji
{
    static readonly Dictionary<(string, double, bool), BitmapSource> cache = [];
    internal static int CachedCount { get { lock (cache) return cache.Count; } }
    internal static BitmapSource RenderFitted(string text, double dpiScale)
        => Cached(text, dpiScale, true);
    internal static BitmapSource Render(string text, double dpiScale) => Cached(text, dpiScale, false);
    static BitmapSource Cached(string text, double dpiScale, bool fit)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0 || dpiScale > 8) throw new ArgumentOutOfRangeException(nameof(dpiScale));
        var key = (text, Math.Round(dpiScale, 4), fit);
        lock (cache) if (cache.TryGetValue(key, out var saved)) return saved;
        var original = RenderMeasured(text, dpiScale);
        int w = original.PixelWidth, h = original.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h]; original.CopyPixels(pixels, stride, 0);
        int left = w, top = h, right = -1, bottom = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (pixels[y * stride + x * 4 + 3] > 4)
        { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (right < left) throw new InvalidOperationException("系统字形没有可见像素。");
        var cropped = new CroppedBitmap(original, new System.Windows.Int32Rect(left, top, right - left + 1, bottom - top + 1));
        double slot = fit ? 24 : 28;
        double ratio = (fit ? 22d : 23d) / Math.Max(cropped.Width, cropped.Height), rw = cropped.Width * ratio, rh = cropped.Height * ratio;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawImage(cropped, new System.Windows.Rect((slot - rw) / 2, (slot - rh) / 2, rw, rh));
        var fitted = new RenderTargetBitmap((int)Math.Ceiling(slot * dpiScale), (int)Math.Ceiling(slot * dpiScale), 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
        fitted.Render(visual); fitted.Freeze();
        lock (cache) { if (cache.Count >= 512) cache.Remove(cache.Keys.First()); cache[key] = fitted; }
        return fitted;
    }
    // 通过系统 DirectWrite 彩色字形管线渲染，不替换用户实际写入的 Unicode 字符。
    static unsafe BitmapSource RenderMeasured(string text, double dpiScale)
    {
        using var write = DWrite.DWriteCreateFactory<IDWriteFactory>();
        using var format = write.CreateTextFormat("Segoe UI Emoji", null, Vortice.DirectWrite.FontWeight.Normal,
            Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, (float)(21 * dpiScale), "zh-CN");
        format.WordWrapping = WordWrapping.NoWrap;
        using var layout = write.CreateTextLayout(text, format, 8192, 1024);
        var metrics = layout.Metrics;
        layout.MaxWidth = Math.Max(1, metrics.WidthIncludingTrailingWhitespace); layout.MaxHeight = Math.Max(1, metrics.Height);
        var overhang = layout.OverhangMetrics;
        uint width = (uint)Math.Ceiling(layout.MaxWidth + Math.Max(0, overhang.Left) + Math.Max(0, overhang.Right) + 8);
        uint height = (uint)Math.Ceiling(layout.MaxHeight + Math.Max(0, overhang.Top) + Math.Max(0, overhang.Bottom) + 8);
        if (width > 8192 || height > 2048) throw new InvalidOperationException("字形测量超出绘制预算。");
        using var imaging = new IWICImagingFactory();
        using var bitmap = imaging.CreateBitmap(width, height, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory>();
        using var target = factory.CreateWicBitmapRenderTarget(bitmap,
            new RenderTargetProperties(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        using var brush = target.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        target.BeginDraw();
        target.Clear(new Color4(0, 0, 0, 0));
        target.DrawTextLayout(new System.Numerics.Vector2(4 + Math.Max(0, overhang.Left), 4 + Math.Max(0, overhang.Top)), layout, brush, DrawTextOptions.EnableColorFont);
        target.EndDraw().CheckError();
        var pixels = new byte[width * height * 4];
        fixed (byte* buffer = pixels) bitmap.CopyPixels(new RectI(0, 0, (int)width, (int)height), width * 4, (uint)pixels.Length, (nint)buffer);
        var source = BitmapSource.Create((int)width, (int)height, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32, null, pixels, (int)width * 4);
        source.Freeze();
        return source;
    }
}
