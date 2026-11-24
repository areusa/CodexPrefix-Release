using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace CodexPrefix;

// 两种模式始终使用同一个 HWND、同一外壳和原来的控件实例；不截屏、不复制视觉。
internal sealed class LivePanelHost
{
    readonly Window window;
    readonly Canvas canvas = new(), contents = new();
    internal SmoothBorder Shell { get; }
    internal FrameworkElement FullContent { get; }
    internal Border MiniContent { get; }
    internal Canvas Root => canvas;
    internal Rect Pose { get; private set; }
    internal double FullOpacity => FullContent.Opacity;
    internal double MiniOpacity => MiniContent.Opacity;
    internal Size FullSize => new(382, FullContent.Height + 22);
    internal bool InitialEnvelopeReserved { get; private set; }
    internal int NativeMoveCount { get; private set; }
    internal Action<string>? Trace;
    int relocationVersion;
    TrackingNative.Rect? overlayBounds;
    internal bool Relocating { get; private set; }
    TaskCompletionSource? placementCompletion;
    internal Task PlacementCompletion => placementCompletion?.Task ?? Task.CompletedTask;
    internal event Action? PlacementCompleted;
    internal LivePanelHost(Window window, SmoothBorder full, CompactBar bar)
    {
        this.window = window;
        FullContent = (FrameworkElement)full.Child; full.Child = null;
        FullContent.Width = 352; FullContent.Height = 166;
        MiniContent = bar.Frame;
        ((Grid)bar.Content).Children.Remove(MiniContent);
        MiniContent.Margin = new Thickness(0); MiniContent.Background = null; MiniContent.BorderBrush = null;
        Shell = full; Shell.Padding = new Thickness(0); Shell.Margin = new Thickness(0);
        Shell.HorizontalAlignment = HorizontalAlignment.Left; Shell.VerticalAlignment = VerticalAlignment.Top;
        Shell.MotionDrawing = true; Shell.RenderTransformOrigin = new Point(.5, 0);
        Shell.Child = contents;
        contents.Children.Add(FullContent); contents.Children.Add(MiniContent);
        window.Content = null;
        canvas.Children.Add(Shell); window.Content = canvas;
        Shell.Width = 382; Shell.Height = 188; contents.Width = 380; contents.Height = 186;
        Canvas.SetLeft(Shell, 0); Canvas.SetTop(Shell, 0);
        // Shell 的边框占一 DIP，子控件坐标从边框内侧起算。
        Canvas.SetLeft(FullContent, 14); Canvas.SetTop(FullContent, 10);
        Canvas.SetLeft(MiniContent, 18); Canvas.SetTop(MiniContent, -1);
        MiniContent.Opacity = 0; MiniContent.IsHitTestVisible = false;
        bar.BindHost(window, Shell, () => PhysicalBounds);
    }
    internal TrackingNative.Rect PhysicalBounds
    {
        get
        {
            var p = window.PointToScreen(new Point(Canvas.GetLeft(Shell), Canvas.GetTop(Shell)));
            double scale = Scale;
            return new((int)Math.Round(p.X), (int)Math.Round(p.Y), (int)Math.Round(p.X + Shell.Width * scale), (int)Math.Round(p.Y + Shell.Height * scale));
        }
    }
    internal double Scale => Math.Max(96, TrackingNative.GetDpiForWindow(new WindowInteropHelper(window).Handle)) / 96d;
    internal static Rect Dip(TrackingNative.Rect rect, double scale) => new(rect.Left / scale, rect.Top / scale, (rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
    internal bool Prepare(TrackingNative.Rect destination)
    {
        if (Relocating) { Trace?.Invoke("前次安全重定位尚未提交，直接接续最新落点"); return false; }
        var current = PhysicalBounds;
        double scale = Scale;
        var needed = Rect.Union(new Rect(current.Left, current.Top, current.Right - current.Left, current.Bottom - current.Top),
            new Rect(destination.Left, destination.Top, destination.Right - destination.Left, destination.Bottom - destination.Top));
        needed.Inflate(CompactBar.MotionPadX * scale, CompactBar.MotionPadY * scale);
        if (!WithinBudget(needed.Width, needed.Height)) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        TrackingNative.GetWindowRect(hwnd, out var existing);
        var oldEnvelope = new Rect(existing.Left, existing.Top, existing.Right - existing.Left, existing.Bottom - existing.Top);
        Trace?.Invoke(oldEnvelope.Contains(needed) ? "运动空间复用；不移动 HWND" : "需要安全重定位");
        return oldEnvelope.Contains(needed);
    }
    static bool WithinBudget(double width, double height) => width <= 8192 && height <= 8192 && width * height <= 16777216;
    internal void ReserveInitialEnvelope()
    {
        if (InitialEnvelopeReserved) return;
        var current = PhysicalBounds;
        var work = TrackingNative.WorkArea(new WindowInteropHelper(window).Handle);
        double scale = Scale;
        var area = new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        area.Union(new Rect(current.Left, current.Top, current.Right - current.Left, current.Bottom - current.Top));
        area.Inflate(CompactBar.MotionPadX * scale, CompactBar.MotionPadY * scale);
        if (!WithinBudget(area.Width, area.Height)) { Trace?.Invoke("初始空间超出像素预算"); return; }
        var target = new TrackingNative.Rect((int)Math.Floor(area.Left), (int)Math.Floor(area.Top), (int)Math.Ceiling(area.Right), (int)Math.Ceiling(area.Bottom));
        // Loaded 首个内容帧前预留本显示器运动空间。普通首切不再先把旧表面搬到目的地。
        Apply(Dip(current, scale), FullOpacity, MiniOpacity, Shell.CornerRadius.TopLeft, Shell.RenderTransform, true, target);
        MoveNative(target); canvas.UpdateLayout(); InitialEnvelopeReserved = true;
        Trace?.Invoke("首帧前预留运动空间");
    }
    void MoveNative(TrackingNative.Rect rect)
    {
        bool ok = TrackingNative.SetWindowPos(new WindowInteropHelper(window).Handle, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x14);
        NativeMoveCount++; Trace?.Invoke("原生位置提交；成功=" + ok + ";错误=" + (ok ? 0 : Marshal.GetLastWin32Error()));
    }
    internal void Snap(TrackingNative.Rect destination, bool mini)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        double scale = Scale;
        int px = (int)Math.Round(CompactBar.MotionPadX * scale), py = (int)Math.Round(CompactBar.MotionPadY * scale);
        TrackingNative.GetWindowRect(hwnd, out var existing);
        var needed = new TrackingNative.Rect(destination.Left - px, destination.Top - py, destination.Right + px, destination.Bottom + py);
        if (overlayBounds is { } overlay)
            needed = new(Math.Min(needed.Left, overlay.Left), Math.Min(needed.Top, overlay.Top), Math.Max(needed.Right, overlay.Right), Math.Max(needed.Bottom, overlay.Bottom));
        bool contained = existing.Left <= needed.Left && existing.Top <= needed.Top && existing.Right >= needed.Right && existing.Bottom >= needed.Bottom;
        if (contained && !Relocating)
        {
            relocationVersion++;
            Apply(Dip(destination, Scale), mini ? 0 : 1, mini ? 1 : 0, mini ? 9 : 14, Transform.Identity, true); canvas.UpdateLayout();
            window.Opacity = 1; return;
        }
        // 跨显示器等异常路径先提交透明帧，再搬原生窗口；不把完整面板旧表面暴露在目标位置。
        int version = ++relocationVersion;
        if (!Relocating) placementCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Relocating = true;
        var nativeTarget = contained ? existing : needed;
        if (!contained)
        {
            var work = TrackingNative.WorkArea(destination);
            var envelope = new Rect(work.Left - px, work.Top - py, work.Right - work.Left + px * 2, work.Bottom - work.Top + py * 2);
            envelope.Union(new Rect(needed.Left, needed.Top, needed.Right - needed.Left, needed.Bottom - needed.Top));
            if (WithinBudget(envelope.Width, envelope.Height)) nativeTarget = new((int)Math.Floor(envelope.Left), (int)Math.Floor(envelope.Top), (int)Math.Ceiling(envelope.Right), (int)Math.Ceiling(envelope.Bottom));
        }
        window.Opacity = 0; Trace?.Invoke("安全重定位：隐藏内容提交");
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (version != relocationVersion || !window.IsVisible) return;
            DwmFlush();
            Apply(Dip(destination, scale), mini ? 0 : 1, mini ? 1 : 0, mini ? 9 : 14, Transform.Identity, true, nativeTarget);
            if (!contained) MoveNative(nativeTarget);
            Apply(Dip(destination, Scale), mini ? 0 : 1, mini ? 1 : 0, mini ? 9 : 14, Transform.Identity, true); canvas.UpdateLayout();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            { if (version != relocationVersion || !window.IsVisible) return; DwmFlush(); window.Opacity = 1; Relocating = false; placementCompletion?.TrySetResult(); Trace?.Invoke("安全重定位：恢复目标内容"); PlacementCompleted?.Invoke(); });
        });
    }
    internal void ResizeFullContent(double height, bool visible)
    {
        FullContent.Height = height;
        if (!visible) return;
        var old = PhysicalBounds; var work = TrackingNative.WorkArea(new WindowInteropHelper(window).Handle); double scale = Scale;
        int h = (int)Math.Round((height + 22) * scale);
        var dest = new TrackingNative.Rect(old.Left, Math.Max(work.Top, Math.Min(old.Top, work.Bottom - h)), old.Left + (int)Math.Round(382 * scale), Math.Max(work.Top, Math.Min(old.Top, work.Bottom - h)) + h);
        Snap(dest, false);
    }
    internal void ReserveOverlayBounds(TrackingNative.Rect? bounds)
    {
        overlayBounds = bounds;
        if (bounds is not { } overlay || Relocating) return;
        TrackingNative.GetWindowRect(new WindowInteropHelper(window).Handle, out var current);
        if (current.Left > overlay.Left || current.Top > overlay.Top || current.Right < overlay.Right || current.Bottom < overlay.Bottom)
            Snap(PhysicalBounds, false);
    }
    internal void Apply(Rect pose, double fullOpacity, double miniOpacity, double radius, Transform transform, bool interactive, TrackingNative.Rect? coordinateOrigin = null)
    {
        Pose = pose;
        var hwnd = new WindowInteropHelper(window).Handle;
        TrackingNative.GetWindowRect(hwnd, out var rect);
        if (coordinateOrigin is { } origin) rect = origin;
        double scale = Scale;
        Canvas.SetLeft(Shell, pose.X - rect.Left / scale); Canvas.SetTop(Shell, pose.Y - rect.Top / scale);
        Shell.Width = pose.Width; Shell.Height = pose.Height; Shell.CornerRadius = new CornerRadius(radius);
        Shell.RenderTransform = transform; Shell.MotionDrawing = true; Shell.InvalidateVisual();
        contents.Width = Math.Max(0, pose.Width - 2); contents.Height = Math.Max(0, pose.Height - 2);
        Canvas.SetLeft(FullContent, (pose.Width - FullContent.Width) / 2 - 1); Canvas.SetTop(FullContent, 10);
        Canvas.SetLeft(MiniContent, (pose.Width - MiniContent.Width) / 2 - 1); Canvas.SetTop(MiniContent, -1);
        // mini 出现时连续展开裁切，给原按钮的残余弹性留空间；不能在尾帧才解除。
        double px = CompactBar.MotionPadX * miniOpacity, py = CompactBar.MotionPadY * miniOpacity;
        contents.Clip = miniOpacity == 1 ? null : new RectangleGeometry(new Rect(-px, -py, Math.Max(0, pose.Width - 2) + px * 2, Math.Max(0, pose.Height - 2) + py * 2), Math.Max(0, radius - 1), Math.Max(0, radius - 1));
        FullContent.Opacity = fullOpacity; MiniContent.Opacity = miniOpacity;
        FullContent.IsHitTestVisible = interactive && fullOpacity == 1;
        MiniContent.IsHitTestVisible = interactive && miniOpacity == 1;
    }
    internal void SetInteractive(bool mini)
    {
        FullContent.Opacity = mini ? 0 : 1; MiniContent.Opacity = mini ? 1 : 0;
        FullContent.IsHitTestVisible = !mini; MiniContent.IsHitTestVisible = mini;
        contents.Clip = mini ? null : new RectangleGeometry(new Rect(0, 0, contents.Width, contents.Height), 13, 13);
    }
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
}
