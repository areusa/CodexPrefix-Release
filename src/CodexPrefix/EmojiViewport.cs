using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexPrefix;

// 只有列表内容平移；可见区与固定功能区各自命中。没有溢出时保留原 mini 的弹性外沿。
public sealed class EmojiViewport : Decorator
{
    internal bool Horizontal { get; init; }
    internal event Action? UserScrolling;
    internal event Action? OffsetChanged;
    internal double Extent { get; private set; }
    internal double Viewport => Horizontal ? ActualWidth : ActualHeight;
    internal double Maximum => Math.Max(0, Extent - Viewport);
    internal double Offset => (double)GetValue(OffsetProperty);
    double destination;
    readonly TranslateTransform shift = new();
    static readonly DependencyProperty OffsetProperty = DependencyProperty.Register("AnimatedOffset", typeof(double), typeof(EmojiViewport), new PropertyMetadata(0d, (d, _) => ((EmojiViewport)d).ApplyOffset()));
    public EmojiViewport()
    {
        BackgroundInput();
        PreviewMouseWheel += (_, e) => { ScrollBy(-e.Delta / 120d * 48); e.Handled = true; };
        PreviewMouseDown += (_, _) => Stop();
        AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) =>
        {
            if (e.TargetObject is FrameworkElement target && Child is not null)
            {
                var r = target.TransformToAncestor(Child).TransformBounds(new Rect(target.RenderSize));
                Reveal(Horizontal ? r.Left : r.Top, Horizontal ? r.Right : r.Bottom); e.Handled = true;
            }
        }));
    }
    void BackgroundInput() { Focusable = false; }
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) => new Rect(RenderSize).Contains(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null;
    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(Horizontal ? new Size(double.PositiveInfinity, constraint.Height) : new Size(constraint.Width, double.PositiveInfinity));
        var size = Child?.DesiredSize ?? new Size();
        return new Size(double.IsFinite(constraint.Width) ? Math.Min(size.Width, constraint.Width) : size.Width,
            double.IsFinite(constraint.Height) ? Math.Min(size.Height, constraint.Height) : size.Height);
    }
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is null) return arrangeSize;
        var desired = Child.DesiredSize;
        Extent = Horizontal ? desired.Width : desired.Height;
        Child.Arrange(new Rect(0, 0, Horizontal ? Math.Max(arrangeSize.Width, desired.Width) : arrangeSize.Width, Horizontal ? arrangeSize.Height : Math.Max(arrangeSize.Height, desired.Height)));
        Child.RenderTransform = shift;
        double view = Horizontal ? arrangeSize.Width : arrangeSize.Height;
        Clip = Extent > view + .01 ? new RectangleGeometry(new Rect(arrangeSize)) : null;
        destination = Math.Clamp(destination, 0, Math.Max(0, Extent - view));
        if (Offset > Math.Max(0, Extent - view)) { BeginAnimation(OffsetProperty, null); SetValue(OffsetProperty, destination); }
        ApplyOffset(); return arrangeSize;
    }
    void ApplyOffset()
    {
        if (Horizontal) shift.X = -Offset; else shift.Y = -Offset;
        OffsetChanged?.Invoke();
    }
    internal void Stop()
    {
        double at = Offset; BeginAnimation(OffsetProperty, null); SetValue(OffsetProperty, at); destination = at;
    }
    internal void ScrollBy(double delta, bool user = true)
    {
        if (user) UserScrolling?.Invoke();
        ScrollTo(destination + delta);
    }
    internal void ScrollTo(double offset, bool animated = true)
    {
        destination = Math.Clamp(offset, 0, Maximum);
        if (!animated || !SystemParameters.ClientAreaAnimation) { BeginAnimation(OffsetProperty, null); SetValue(OffsetProperty, destination); return; }
        BeginAnimation(OffsetProperty, new DoubleAnimation(Offset, destination, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd });
    }
    internal void Reveal(double start, double end)
    {
        if (start < destination) ScrollTo(start); else if (end > destination + Viewport) ScrollTo(end - Viewport);
    }
    internal void Clamp() => ScrollTo(Offset, false);
}

internal static class EmojiLayout
{
    internal const double FullCell = 35, RowGap = 4, FullListWidth = 230;
    internal static double MiniViewport(int count) => count == 0 ? 0 : count <= 8 ? count * 30 + (count - 1) * 2 : 271;
    internal static double FullViewport(int count) => count <= 6 ? 35 : count <= 12 ? 74 : count <= 18 ? 113 : 134.5;
    internal static double MiniWidth(int count, bool attached) => 152 + (count == 0 ? 0 : MiniViewport(count) + 2) + (attached ? 0 : 32);
}
