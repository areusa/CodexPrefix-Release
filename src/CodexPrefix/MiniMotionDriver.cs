using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexPrefix;

// 固定受力区 / 跟随整体形变的命中区 / 按钮自身缩放，三者独立。
internal sealed class MiniMotionDriver : IDisposable
{
    readonly CompactBar bar;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly DispatcherTimer sampling;
    readonly List<(Button Button, TranslateTransform Shift, ScaleTransform Scale)> cells = [];
    readonly MatrixTransform body = new();
    readonly Dictionary<Button, (MouseButtonEventHandler Down, MouseButtonEventHandler Up, MouseEventHandler Lost, RoutedEventHandler Click)> subscriptions = [];
    MiniMotion physics = new(11);
    Point? lastPoint;
    double lastSample, lastFrame, suppressUntil;
    bool disposed, started, needsExit, freshNeeded, dragging, wasActive = true;
    int epoch;
    int releaseIndex = -1;
    (double At, int Epoch, Action Action)? pending;
    TimeSpan lastCompositionTime = TimeSpan.MinValue;
    string sampleSource = "";
    internal MotionInputFixture? InputFixture { get; set; }
    double Now => InputFixture?.Seconds ?? clock.Elapsed.TotalSeconds;
    internal double ElapsedSeconds => Now;
    bool Visible => InputFixture?.Visible ?? (bar.IsVisible && bar.Opacity != 0);
    internal long SampleCount { get; private set; }
    internal long SwipeCount { get; private set; }
    internal bool Transitioning { get; set; }
    internal Func<bool>? HostActive { get; set; }
    internal MiniMotion Physics => physics;
    internal MotionCapture? Capture { get; set; }
    internal void RecordExternal(string reason, double milliseconds) => Record("外部耗时", reason, milliseconds / 1000);
    void Record(string kind, string reason, double dt = 0, Point? pointer = null, Point? previous = null)
    {
        if (Capture is not { Active: true } capture) return;
        capture.Record(Now, kind, reason, dt, epoch, pointer ?? lastPoint, previous,
            bar.StableInputBounds, body.Matrix, IsGuarded, needsExit, freshNeeded, Transitioning, dragging,
            suppressUntil, rendering, pending is not null, physics, sampleSource);
    }
    internal string LastInterrupt { get; private set; } = "";
    internal int Epoch => epoch;
    internal event Action<string>? Interrupted;
    internal bool IsGuarded => dragging || Transitioning || Now < suppressUntil;
    internal MiniMotionDriver(CompactBar bar)
    {
        this.bar = bar;
        bar.MotionSurface.RenderTransformOrigin = new Point(.5, 0);
        bar.MotionSurface.RenderTransform = body;
        sampling = new DispatcherTimer(DispatcherPriority.Input, bar.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        sampling.Tick += (_, _) => SampleIdle();
    }
    internal void Start()
    {
        if (started) return;
        started = true;
        bar.MotionSurface.RenderTransformOrigin = new Point(.5, 0);
        bar.MotionSurface.RenderTransform = body;
        Rebind();
        Interrupt("小条初始化");
        sampling.Start();
    }
    internal void Rebind()
    {
        if (!started || disposed) return;
        var next = bar.MotionButtons;
        if (cells.Select(c => c.Button).SequenceEqual(next)) return;
        physics.Release();
        var previous = cells.Select((c, i) => (c.Button, Scale: (physics.Scale[i].X, physics.Scale[i].V, physics.Scale[i].Target), Shift: (physics.Shift[i].X, physics.Shift[i].V, physics.Shift[i].Target))).ToDictionary(c => c.Button);
        epoch++; releaseIndex = -1; pending = null; physics.Release();
        foreach (var (button, events) in subscriptions)
        { button.PreviewMouseLeftButtonDown -= events.Down; button.PreviewMouseLeftButtonUp -= events.Up; button.LostMouseCapture -= events.Lost; button.Click -= events.Click; }
        subscriptions.Clear();
        foreach (var c in cells) { c.Shift.X = 0; c.Scale.ScaleX = c.Scale.ScaleY = 1; if (c.Button.IsMouseCaptured) c.Button.ReleaseMouseCapture(); }
        cells.Clear();
        var old = physics; physics = new MiniMotion(next.Count);
        static void Copy(MotionSpring a, MotionSpring b) { b.X = a.X; b.V = a.V; b.Target = a.Target; }
        Copy(old.X, physics.X); Copy(old.Y, physics.Y); Copy(old.StretchX, physics.StretchX); Copy(old.StretchY, physics.StretchY); Copy(old.Skew, physics.Skew); Copy(old.Hover, physics.Hover);
        physics.BudgetUntil = old.BudgetUntil;
        foreach (var button in next)
        {
            button.ApplyTemplate();
            var shift = new TranslateTransform(); var scale = new ScaleTransform(1, 1);
            button.RenderTransform = shift;
            if (button.Template.FindName("MotionFace", button) is FrameworkElement face) face.RenderTransform = scale;
            int index = cells.Count;
            cells.Add((button, shift, scale));
            if (previous.TryGetValue(button, out var saved))
            { (physics.Scale[index].X, physics.Scale[index].V, physics.Scale[index].Target) = saved.Scale; (physics.Shift[index].X, physics.Shift[index].V, physics.Shift[index].Target) = saved.Shift; }
            MouseButtonEventHandler down = (_, e) =>
            {
                if (IsGuarded || !button.IsEnabled) { e.Handled = true; return; }
                needsExit = false; freshNeeded = false; lastPoint = null;
                physics.Press(index, Now); Wake();
            };
            MouseButtonEventHandler up = (_, e) =>
            {
                var hit = Pick(Mouse.GetPosition(bar.SurfaceWindow));
                bool cancelled = IsGuarded || !button.IsEnabled || physics.Pressed != index || hit != button;
                physics.Release(); Wake();
                if (cancelled) { e.Handled = true; button.ReleaseMouseCapture(); }
                else { releaseIndex = index; physics.Click(index, Now); }
            };
            MouseEventHandler lost = (_, _) => { if (physics.Pressed == index) physics.Release(); };
            RoutedEventHandler click = (_, _) =>
            {
                if (releaseIndex == index) { releaseIndex = -1; return; }
                if (IsGuarded || !button.IsEnabled) return;
                physics.Click(index, Now); Wake();
            };
            button.PreviewMouseLeftButtonDown += down; button.PreviewMouseLeftButtonUp += up; button.LostMouseCapture += lost; button.Click += click;
            subscriptions.Add(button, (down, up, lost, click));
        }
        ApplyPose();
    }
    internal void ScheduleAction(Action action)
    {
        if (IsGuarded) return;
        pending = (Now + MotionParameters.Get("actionDelay"), epoch, action); Wake();
    }
    internal void SetDragging(bool value) { dragging = value; Interrupt(value ? "宿主拖动开始" : "宿主拖动结束"); }
    internal void Scroll()
    {
        if (disposed || !started) return;
        // 列表滚动不改变宿主坐标：只作废旧点击，不清空整体物理或要求鼠标移出。
        epoch++; releaseIndex = -1; pending = null; physics.Release();
        foreach (var (button, _, _) in cells) if (button.IsMouseCaptured) button.ReleaseMouseCapture();
        if (!IsGuarded && !needsExit && Visible && (HostActive?.Invoke() ?? true) && physics.Moving) Wake();
        Record("列表滚动", "取消旧按压与延迟点击；保留整体物理及原保护");
    }
    internal void Interrupt(string reason)
    {
        Record("中断前", reason);
        epoch++; releaseIndex = -1; pending = null; physics.Reset(); lastPoint = null;
        suppressUntil = Now + .15; needsExit = false; freshNeeded = true; LastInterrupt = reason;
        if (ReadPointer() is { } point) needsExit = bar.StableInputBounds.Contains(point);
        foreach (var (button, shift, scale) in cells) { shift.X = 0; scale.ScaleX = scale.ScaleY = 1; if (button.IsMouseCaptured) button.ReleaseMouseCapture(); }
        body.Matrix = Matrix.Identity;
        if (bar.MotionSurface is SmoothBorder border) { border.MotionDrawing = bar.SurfaceWindow != bar; border.InvalidateVisual(); }
        Record("中断后", reason);
        Interrupted?.Invoke(reason);
    }
    static bool Crosses(Point a, Point b, Rect r)
    {
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        foreach (var (p, q) in new[] { (-dx, a.X - r.Left), (dx, r.Right - a.X), (-dy, a.Y - r.Top), (dy, r.Bottom - a.Y) })
        {
            if (Math.Abs(p) < 1e-8) { if (q < 0) return false; continue; }
            double t = q / p; if (p < 0) t0 = Math.Max(t0, t); else t1 = Math.Min(t1, t); if (t0 > t1) return false;
        }
        return true;
    }
    internal Button? Pick(Point p)
    {
        DependencyObject? hit = bar.SurfaceWindow.InputHitTest(p) as DependencyObject;
        while (hit is not null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
        return hit as Button;
    }
    Point? ReadPointer()
    {
        if (InputFixture is { } fixture) { fixture.Reads++; return fixture.Pointer; }
        if (PresentationSource.FromVisual(bar.SurfaceWindow) is null || !GetCursorPos(out var raw)) return null;
        return bar.SurfaceWindow.PointFromScreen(new Point(raw.X, raw.Y));
    }
    void SampleIdle()
    {
        // 活跃期只由渲染帧读取位置，定时器不再追加同一段受力。
        if (!rendering) Sample("静止唤醒");
    }
    internal void FixtureTimer()
    {
        if (InputFixture is null) throw new InvalidOperationException("仅内部隔离输入夹具允许手动时钟。");
        SampleIdle();
    }
    internal void FixtureFrame()
    {
        if (InputFixture is null) throw new InvalidOperationException("仅内部隔离输入夹具允许手动时钟。");
        if (rendering) RenderFrame(TimeSpan.FromSeconds(Now));
    }
    void Sample(string source)
    {
        sampleSource = source;
        try
        {
        SampleCount++;
        if (disposed || !Visible || Transitioning) { Record("采样", "隐藏或转场"); return; }
        bool active = HostActive?.Invoke() ?? true;
        if (!active) { if (wasActive) Interrupt("宿主失焦"); wasActive = false; Record("采样", "宿主未激活"); return; }
        wasActive = true;
        if (ReadPointer() is not { } p) { Record("采样", "光标读取失败"); return; }
        double now = Now;
        var before = lastPoint; double dt = now - lastSample;
        lastPoint = p; lastSample = now;
        if (before is not { } a) { Record("采样", "首个位置", dt, p); return; }
        if ((p - a).Length < .1) { Record("采样", "位置未变", dt, p, a); return; }
        var stable = bar.StableInputBounds;
        if (IsGuarded) { Record("采样", "保护期或拖动", dt, p, a); return; }
        if (needsExit) { if (!stable.Contains(p)) needsExit = false; else { Record("采样", "等待移出", dt, p, a); return; } }
        if (InputFixture?.ButtonsDown ?? new[] { 1, 2, 4 }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0)) { Record("采样", "鼠标键按住", dt, p, a); return; }
        // 屏幕位置由真实光标采样；宿主移动造成的相对变化由追踪器先中断并加保护。
        var local = bar.SurfaceWindow.TranslatePoint(p, bar.MotionSurface);
        bool hover = new RectangleGeometry(new Rect(0, 0, bar.DesignWidth, CompactBar.BarHeight), MotionParameters.Get("miniRadius"), MotionParameters.Get("miniRadius")).FillContains(local) || Pick(p) is not null;
        bool targetChanged = physics.Hover.Target != (hover ? MotionParameters.Get("hoverScale") : 1);
        if (targetChanged || physics.Moving) physics.Hovering(hover, now);
        if (freshNeeded) { freshNeeded = false; if (targetChanged) Wake(); Record("采样", "重建轨迹", dt, p, a); return; }
        bool crossing = dt is >= .0005 and <= .08 && Crosses(a, p, stable);
        if (crossing)
        {
            SwipeCount++;
            physics.Swipe((p.X - a.X) / dt, (p.Y - a.Y) / dt, dt, stable.Contains(a) && stable.Contains(p), now);
        }
        if (targetChanged || crossing || physics.Moving) Wake();
        Record("采样", crossing ? "已施力" : dt > .08 ? "间隔超过80ms" : dt < .0005 ? "间隔低于0.5ms" : hover ? "可见但未穿过稳定区" : "未穿过稳定区", dt, p, a);
        }
        finally { sampleSource = ""; }
    }
    bool rendering;
    void Wake() { if (rendering) return; rendering = true; lastFrame = Now; CompositionTarget.Rendering += Render; }
    void Render(object? sender, EventArgs e)
        => RenderFrame((e as RenderingEventArgs)?.RenderingTime);
    void RenderFrame(TimeSpan? compositionTime)
    {
        if (!rendering || disposed) return;
        // 同一合成时间可能重复通知；不能重复采样、积分或执行队列。
        if (compositionTime is { } time)
        {
            if (lastCompositionTime == time) return;
            lastCompositionTime = time;
        }
        double now = Now, dt = now - lastFrame; lastFrame = now;
        Record("帧前", "进入渲染", dt);
        if (dt > .25 || !Visible || IsGuarded) { if (dt > .25) Interrupt("异常长帧"); StopRendering(); return; }
        Sample("渲染帧");
        // 采样可能发现失焦并中断，不能在同一帧继续执行旧动作。
        if (disposed || !Visible || IsGuarded) { StopRendering(); return; }
        if (pending is { } action && now >= action.At)
        {
            pending = null;
            if (action.Epoch == epoch) action.Action();
            if (disposed || !Visible || Transitioning) { StopRendering(); return; }
        }
        if (!(InputFixture?.Animations ?? SystemParameters.ClientAreaAnimation)) physics.Reset(); else physics.Step(dt, Now);
        ApplyPose();
        // 只刷新原生命中，不把移动到静止光标下的控件当成新冲量。
        if (InputFixture is null) Mouse.Synchronize();
        Record("帧后", "完成渲染回调", dt);
        if (!physics.Moving && pending is null) StopRendering();
    }
    internal void ApplyPose()
    {
        bar.MotionSurface.RenderTransform = body;
        double sx = Math.Max(.05, physics.Hover.X * (1 + physics.StretchX.X)), sy = Math.Max(.05, physics.Hover.X * (1 + physics.StretchY.X));
        var m = Matrix.Identity; m.Scale(sx, sy); m.Skew(physics.Skew.X, 0); m.Translate(physics.X.X, physics.Y.X); body.Matrix = m;
        for (int i = 0; i < cells.Count; i++) { cells[i].Shift.X = physics.Shift[i].X; cells[i].Scale.ScaleX = cells[i].Scale.ScaleY = physics.Scale[i].X; }
        if (bar.MotionSurface is SmoothBorder border) { border.MotionDrawing = bar.SurfaceWindow != bar || physics.Moving; border.InvalidateVisual(); }
    }
    internal void BeginTransition()
    {
        Transitioning = true; epoch++; pending = null; physics.Release(); StopRendering();
        physics.Hover.Target = 1;
    }
    internal Transform AdvanceTransition(double dt)
    {
        physics.Step(dt, Now); ApplyPose();
        return body.CloneCurrentValue();
    }
    void StopRendering() { if (!rendering) return; rendering = false; CompositionTarget.Rendering -= Render; }
    public void Dispose() { if (disposed) return; disposed = true; sampling.Stop(); Interrupt("小条关闭"); StopRendering(); foreach (var (b, h) in subscriptions) { b.PreviewMouseLeftButtonDown -= h.Down; b.PreviewMouseLeftButtonUp -= h.Up; b.LostMouseCapture -= h.Lost; b.Click -= h.Click; } subscriptions.Clear(); }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
}
