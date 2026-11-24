using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexPrefix;

// 只保存窗口身份，不读取会话标题；句柄、进程及创建时间必须同时相符。
internal sealed record WindowIdentity(nint Handle, uint ProcessId, long Started)
{
    internal bool IsCurrent() => Read(Handle, false) == this;
    internal static bool IsCodexPackage(string family, string file) => family.Equals("OpenAI.Codex_2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase)
        && (file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase));
    internal static WindowIdentity? Read(nint handle, bool verifyPackage = true)
    {
        if (handle == 0 || !Native.IsWindow(handle)) return null;
        Native.GetWindowThreadProcessId(handle, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (verifyPackage)
            {
                uint length = 0;
                if (TrackingNative.GetPackageFamilyName(process.Handle, ref length, null) != 122 || length > 1024) return null;
                var family = new StringBuilder((int)length);
                if (TrackingNative.GetPackageFamilyName(process.Handle, ref length, family) != 0 || !IsCodexPackage(family.ToString(), System.IO.Path.GetFileName(process.MainModule?.FileName ?? ""))) return null;
            }
            return new(handle, pid, process.StartTime.ToUniversalTime().Ticks);
        }
        catch { return null; }
    }
}

internal sealed class WindowTracker : IDisposable
{
    readonly CompactBar bar;
    readonly Func<nint, WindowIdentity?> identify;
    readonly Func<bool> canRebind;
    readonly Action changed;
    readonly DispatcherTimer recovery;
    readonly TrackingNative.EventProc callback;
    readonly List<nint> hooks = [];
    readonly HwndSource source;
    bool queued, disposed;
    DateTime nextDiscovery;
    WindowIdentity? previous;
    internal WindowIdentity? Target { get; private set; }
    internal bool CanOperate => Target is { } target && target.IsCurrent() && Native.IsWindowVisible(target.Handle) && !Native.IsIconic(target.Handle)
        && Native.BelongsToWindow(Native.GetForegroundWindow(), target.Handle) && !TrackingNative.Cloaked(target.Handle);
    internal bool HooksAvailable => hooks.All(h => h != 0);
    internal string? VisualInvalidReason => Target is not { } t ? null : !t.IsCurrent() ? "目标身份失效" : !Native.IsWindowVisible(t.Handle) ? "目标隐藏" : Native.IsIconic(t.Handle) ? "目标最小化" : TrackingNative.Cloaked(t.Handle) ? "目标被系统隐藏" : Native.GetForegroundWindow() == bar.Handle || Native.BelongsToWindow(Native.GetForegroundWindow(), t.Handle) ? null : "切到第三应用";
    internal string Hint { get; private set; } = "等待激活 Codex 窗口";

    internal WindowTracker(CompactBar bar, Func<bool> canRebind, Action changed, nint demoWindow = default, Func<nint, WindowIdentity?>? testIdentity = null)
    {
        this.bar = bar; this.canRebind = canRebind; this.changed = changed;
        // 演示模式仅接受本进程创建的夹具。测试解析器只由内部自检传入，无命令行任意 HWND 接口。
        identify = testIdentity ?? (demoWindow != 0 && WindowIdentity.Read(demoWindow, false)?.ProcessId == Environment.ProcessId
            ? hwnd => hwnd == demoWindow ? WindowIdentity.Read(hwnd, false) : null : hwnd => WindowIdentity.Read(hwnd));
        bar.Show();
        var handle = bar.Handle;
        source = HwndSource.FromHwnd(handle);
        source.AddHook(WindowMessage);
        TrackingNative.SetWindowLongPtrW(handle, -20, (nint)(TrackingNative.GetWindowLongPtrW(handle, -20).ToInt64() | 0x08000080));
        callback = (_, type, hwnd, objectId, child, _, _) =>
        {
            if (disposed || hwnd == handle) return;
            if (Target?.Handle == hwnd && type is 0xA or 0xB)
                bar.Dispatcher.BeginInvoke(() => { if (!disposed) bar.Motion.SetDragging(type == 0xA); });
            if (type == 3 || (Target?.Handle == hwnd && objectId == 0 && child == 0)) Queue();
        };
        hooks.Add(TrackingNative.SetWinEventHook(3, 3, 0, callback, 0, 0, 0));
        hooks.Add(TrackingNative.SetWinEventHook(0x8000, 0x800B, 0, callback, 0, 0, 0));
        hooks.Add(TrackingNative.SetWinEventHook(0x16, 0x17, 0, callback, 0, 0, 0));
        hooks.Add(TrackingNative.SetWinEventHook(0xA, 0xB, 0, callback, 0, 0, 0));
        recovery = new DispatcherTimer(DispatcherPriority.Background, bar.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        recovery.Tick += (_, _) => Update();
        recovery.Start();
        // 动效每帧只核对前台归属；进程创建时间仍由追踪更新与真实写入链路严格复核。
        // 不能每 16ms 打开进程句柄、读取启动时间，挤占输入和 UIA 调度。
        bar.Motion.HostActive = () => Target is null || Native.BelongsToWindow(Native.GetForegroundWindow(), Target.Handle);
        Update();
    }
    nint WindowMessage(nint hwnd, int message, nint wp, nint lp, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; } // 鼠标可点击，但不激活条窗或夺走输入框焦点。
        if (message is 0x5 or 0x2E0) Native.RoundCorners(hwnd, 9);
        if (message is 0x2E0 or 0x7E) { bar.Motion.Interrupt("显示环境重建"); Queue(); }
        return 0;
    }
    void Queue()
    {
        if (queued || disposed) return;
        queued = true;
        bar.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => { queued = false; if (!disposed) Update(); });
    }
    internal static nint Root(nint hwnd) => Native.OwnerRoot(hwnd);
    internal static bool MainCandidate(nint hwnd) => Native.IsWindowVisible(hwnd) && TrackingNative.GetWindow(hwnd, 4) == 0
        && (TrackingNative.GetWindowLongPtrW(hwnd, -20).ToInt64() & 0x80) == 0;
    internal static List<nint> Candidates(Func<nint, WindowIdentity?> resolver)
    {
        var found = new List<nint>();
        TrackingNative.EnumWindows((hwnd, _) => { if (MainCandidate(hwnd) && resolver(hwnd) is not null) found.Add(hwnd); return true; }, 0);
        return found;
    }
    internal void Update()
    {
        if (disposed) return;
        long diagnosticStart = bar.Motion.Capture is { Active: true } ? Stopwatch.GetTimestamp() : 0;
        try
        {
        var foreground = Root(Native.GetForegroundWindow());
        if (Target is { } old && (!old.IsCurrent() || identify(old.Handle) != old)) Target = null;
        if (canRebind())
        {
            var active = MainCandidate(foreground) ? identify(foreground) : null;
            if (active is not null) Target = active;
            else if (Target is null && DateTime.UtcNow >= nextDiscovery)
            {
                nextDiscovery = DateTime.UtcNow.AddSeconds(2);
                var candidates = Candidates(identify);
                if (candidates.Count == 1) Target = identify(candidates[0]);
                Hint = candidates.Count > 1 ? "多个 Codex 窗口：请激活要追踪的窗口" : "未找到 Codex：可从左侧拖动，或还原完整模式";
            }
        }
        var handle = bar.Handle;
        bool targetChanged = previous != Target;
        if (targetChanged)
        {
            TrackingNative.SetWindowLongPtrW(handle, -8, Target?.Handle ?? 0);
            bar.SetAttached(Target is not null);
            previous = Target;
        }
        if (Target is { } target)
        {
            bool visible = Native.IsWindowVisible(target.Handle) && !Native.IsIconic(target.Handle) && !TrackingNative.Cloaked(target.Handle);
            Hint = visible ? "已追踪 Codex · 顶边居中；手动打开重命名框后使用" : "Codex 已最小化或隐藏，小条随之收起";
            if (visible)
            {
                bool wasHidden = !bar.IsVisible;
                if (wasHidden) bar.Show();
                var host = TrackingNative.Bounds(target.Handle);
                double scale = Math.Max(96, TrackingNative.GetDpiForWindow(target.Handle)) / 96d;
                var work = TrackingNative.WorkArea(target.Handle);
                bar.FitWorkArea((work.Right - work.Left) / scale);
                // WPF 会在 WM_DPICHANGED 中回写 Width/Height，不能把回写值继续当作设计尺寸。
                if (lastHost != host || lastDpi != scale) { bar.Motion.Interrupt("宿主几何或 DPI 变化"); lastHost = host; lastDpi = scale; }
                var visibleRect = Place(host, work, scale, bar.DesignWidth, CompactBar.BarHeight);
                var rect = new TrackingNative.Rect(visibleRect.Left - (int)Math.Round(CompactBar.MotionPadX * scale), visibleRect.Top - (int)Math.Round(CompactBar.MotionPadY * scale), visibleRect.Right + (int)Math.Round(CompactBar.MotionPadX * scale), visibleRect.Bottom + (int)Math.Round(CompactBar.MotionPadY * scale));
                TrackingNative.GetWindowRect(handle, out var current);
                if (bar.HostPlace is not null) bar.HostPlace(visibleRect, scale);
                else if (current != rect)
                {
                    TrackingNative.SetWindowPos(handle, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x14);
                    // 坐标更新后再次判定实际重叠，不能沿用旧位置下的“须移出”标记。
                    bar.Motion.Interrupt("宿主定位完成");
                }
                // 恢复或换绑时把条窗插在宿主附近，不抬升到其他应用上方。
                if (wasHidden || targetChanged || lastForeground != foreground || (!IsTopmost(target.Handle) && IsTopmost(handle))) PlaceAbove(handle, target.Handle);
            }
            else if (bar.IsVisible) bar.Hide();
        }
        else
        {
            if (bar.HideWithoutHost) { if (bar.IsVisible) bar.Hide(); }
            else { if (!bar.IsVisible) bar.Show(); if (targetChanged) ClampLost(); }
        }
        lastForeground = foreground;
        bar.SetTrackingHint(Hint);
        changed();
        }
        finally
        {
            if (diagnosticStart != 0) bar.Motion.RecordExternal("窗口追踪耗时", Stopwatch.GetElapsedTime(diagnosticStart).TotalMilliseconds);
        }
    }
    nint lastForeground;
    TrackingNative.Rect lastHost;
    double lastDpi;
    internal const nint HwndTopmost = -1, HwndNoTopmost = -2;
    internal const uint PlaceAboveFlags = 0x0213; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER

    internal static bool IsTopmost(nint hwnd) => hwnd != 0 && (TrackingNative.GetWindowLongPtrW(hwnd, -20).ToInt64() & 0x8L) != 0;

    internal static nint? ResolveInsertAfter(nint bar, nint beforeHost, nint beforeBar, bool hostTopmost, bool barTopmost, bool beforeHostTopmost, bool beforeBarTopmost)
    {
        if (beforeHost == bar && hostTopmost == barTopmost) return null;
        var anchor = beforeHost == bar ? beforeBar : beforeHost;
        bool anchorTopmost = beforeHost == bar ? beforeBarTopmost : beforeHostTopmost;
        if (!hostTopmost)
            return anchor == 0 || anchorTopmost ? HwndNoTopmost : anchor;
        return anchor == 0 ? HwndTopmost : anchor;
    }

    internal static void PlaceAbove(nint bar, nint host)
    {
        if (bar == 0 || host == 0) return;
        bool hostTopmost = IsTopmost(host);
        bool barTopmost = IsTopmost(bar);
        var beforeHost = TrackingNative.GetWindow(host, 3);
        var beforeBar = beforeHost == bar ? TrackingNative.GetWindow(bar, 3) : 0;
        var after = ResolveInsertAfter(bar, beforeHost, beforeBar, hostTopmost, barTopmost, IsTopmost(beforeHost), IsTopmost(beforeBar));
        if (after is null) return;
        TrackingNative.SetWindowPos(bar, after.Value, 0, 0, 0, 0, PlaceAboveFlags);
        if (!hostTopmost && IsTopmost(bar))
            TrackingNative.SetWindowPos(bar, HwndNoTopmost, 0, 0, 0, 0, PlaceAboveFlags);
    }
    internal static TrackingNative.Rect Place(TrackingNative.Rect host, TrackingNative.Rect work, double scale, double width, double height)
    {
        int w = (int)Math.Round(width * scale), h = (int)Math.Round(height * scale);
        int x = (host.Left + host.Right - w) / 2;
        int y = host.Top + (int)Math.Round(6 * scale);
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        return new(x, y, x + w, y + h);
    }
    internal void ClampLost()
    {
        var handle = bar.Handle;
        var rect = bar.VisualBounds;
        var work = TrackingNative.WorkArea(handle);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        double scale = Math.Max(96, TrackingNative.GetDpiForWindow(handle)) / 96d;
        int x = Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - w)), y = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - h));
        if (bar.HostPlace is { } place) { place(new(x, y, x + w, y + h), scale); return; }
        TrackingNative.SetWindowPos(handle, 0, x - (int)Math.Round(CompactBar.MotionPadX * scale), y - (int)Math.Round(CompactBar.MotionPadY * scale), 0, 0, 0x15);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; recovery.Stop();
        foreach (var hook in hooks) if (hook != 0) TrackingNative.UnhookWinEvent(hook);
        source.RemoveHook(WindowMessage);
        if (bar.Handle != 0 && Native.IsWindow(bar.Handle)) TrackingNative.SetWindowLongPtrW(bar.Handle, -8, 0);
        Target = null;
        GC.KeepAlive(callback);
    }
}

internal static class TrackingNative
{
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Rect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    internal delegate bool EnumProc(nint window, nint parameter);
    internal delegate void EventProc(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint time);
    internal static Rect Bounds(nint hwnd) { if (DwmGetWindowAttribute(hwnd, 9, out Rect rect, Marshal.SizeOf<Rect>()) != 0) GetWindowRect(hwnd, out rect); return rect; }
    internal static bool Cloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, 14, out int value, sizeof(int)) == 0 && value != 0;
    internal static Rect WorkArea(nint hwnd) { var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; return GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info) ? info.Work : new(0, 0, 1920, 1080); }
    internal static Rect WorkArea(Rect rect) { var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; return GetMonitorInfoW(MonitorFromRect(ref rect, 2), ref info) ? info.Work : WorkArea((nint)0); }
    [DllImport("user32.dll")] static extern nint MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern int GetPackageFamilyName(nint process, ref uint length, StringBuilder? name);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect rect, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, EventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
}
