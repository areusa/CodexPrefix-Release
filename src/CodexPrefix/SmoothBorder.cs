using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexPrefix;

// 四角共用同一覆盖率计算并镜像；避免 WPF 曲线路径方向造成的栅格差异。
// 仅在尺寸、DPI 或颜色变化时重建位图，布局仍遵循 Border 的标准规则。
public sealed class SmoothBorder : Border
{
    // 高频形变时直接画矢量；稳态及原有组件继续沿用对称覆盖率缓存。
    public bool MotionDrawing { get; set; }
    BitmapSource? cached;
    (int W, int H, double Scale, double Radius, double Thickness, Color Background, Color Stroke) key;
    public SmoothBorder() { SnapsToDevicePixels = false; RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified); }
    protected override void OnRender(DrawingContext dc)
    {
        if (MotionDrawing)
        {
            double t = BorderThickness.Left;
            dc.DrawRoundedRectangle(Background, BorderBrush is null ? null : new Pen(BorderBrush, t),
                new Rect(t / 2, t / 2, Math.Max(0, ActualWidth - t), Math.Max(0, ActualHeight - t)),
                Math.Max(0, CornerRadius.TopLeft - t / 2), Math.Max(0, CornerRadius.TopLeft - t / 2));
            return;
        }
        var r = CornerRadius;
        var b = BorderThickness;
        if (r.TopLeft != r.TopRight || r.TopLeft != r.BottomLeft || r.TopLeft != r.BottomRight || b.Left != b.Right || b.Left != b.Top || b.Left != b.Bottom || Background is not (null or SolidColorBrush) || BorderBrush is not (null or SolidColorBrush))
        { base.OnRender(dc); return; }
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Round(ActualWidth * scale), h = (int)Math.Round(ActualHeight * scale);
        if (w <= 0 || h <= 0) return;
        var bg = (Background as SolidColorBrush)?.Color ?? Colors.Transparent;
        var stroke = (BorderBrush as SolidColorBrush)?.Color ?? Colors.Transparent;
        var next = (w, h, scale, r.TopLeft, b.Left, bg, stroke);
        if (cached is null || key != next)
        {
            key = next;
            double radius = Math.Clamp(r.TopLeft * scale, 0, Math.Min(w, h) / 2d), t = b.Left * scale;
            var pixels = new byte[w * h * 4];
            double Distance(double x, double y, double inset, double rad)
            {
                double qx = Math.Abs(x - w / 2d) - (w / 2d - inset - rad);
                double qy = Math.Abs(y - h / 2d) - (h / 2d - inset - rad);
                return Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) + Math.Min(Math.Max(qx, qy), 0) - rad;
            }
            double Coverage(int x, int y, double inset, double rad)
            {
                if (inset * 2 >= Math.Min(w, h)) return 0;
                double distance = Distance(x + .5, y + .5, inset, rad);
                if (distance <= -.708) return 1;
                if (distance >= .708) return 0;
                int hits = 0;
                for (int sy = 0; sy < 16; sy++) for (int sx = 0; sx < 16; sx++)
                    if (Distance(x + (sx + .5) / 16, y + (sy + .5) / 16, inset, rad) <= 0) hits++;
                return hits / 256d;
            }
            // 一次计算后写入镜像像素，保证四角在最终设备像素上完全一致。
            for (int y = 0; y < (h + 1) / 2; y++) for (int x = 0; x < (w + 1) / 2; x++)
            {
                double outer = Coverage(x, y, 0, radius);
                double inner = t > 0 ? Coverage(x, y, t, Math.Max(0, radius - t)) : outer;
                double edgeAlpha = (outer - inner) * stroke.A / 255d;
                double fillAlpha = (outer - edgeAlpha) * bg.A / 255d;
                byte a = (byte)Math.Clamp(Math.Round((edgeAlpha + fillAlpha) * 255), 0, 255);
                byte red = (byte)Math.Clamp(Math.Round(stroke.R * edgeAlpha + bg.R * fillAlpha), 0, 255);
                byte green = (byte)Math.Clamp(Math.Round(stroke.G * edgeAlpha + bg.G * fillAlpha), 0, 255);
                byte blue = (byte)Math.Clamp(Math.Round(stroke.B * edgeAlpha + bg.B * fillAlpha), 0, 255);
                foreach (int p in new[] { (y * w + x) * 4, (y * w + w - 1 - x) * 4, ((h - 1 - y) * w + x) * 4, ((h - 1 - y) * w + w - 1 - x) * 4 })
                { pixels[p] = blue; pixels[p + 1] = green; pixels[p + 2] = red; pixels[p + 3] = a; }
            }
            cached = BitmapSource.Create(w, h, scale * 96, scale * 96, PixelFormats.Pbgra32, null, pixels, w * 4); cached.Freeze();
        }
        dc.DrawImage(cached, new Rect(0, 0, w / scale, h / scale));
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); InvalidateVisual(); }
}
