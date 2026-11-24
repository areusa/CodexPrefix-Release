using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Diagnostics;

namespace CodexPrefix;

public partial class MainWindow
{
    LivePanelHost panels = null!;
    TrackingNative.Rect fullBounds, miniBounds;
    double fullDpi;
    bool fullTopmost, preparingMode;
    readonly Stopwatch modeClock = new();
    long modeAttempt;
    bool modeInputPending;
    internal event Action<string, double>? ModePhase;
    void TraceMode(string phase)
    { ModePhase?.Invoke(phase, modeClock.Elapsed.TotalMilliseconds); log.Write("转场阶段", $"请求={modeAttempt};毫秒={modeClock.Elapsed.TotalMilliseconds:F3};阶段={phase}"); }
    void BeginModePointerTrace()
    { modeClock.Restart(); modeAttempt++; modeInputPending = true; TraceMode("模式按钮按下；尚未改变原生窗口"); }
    internal LivePanelHost Panels => panels;
    internal bool IsCompact => compact;
    internal ModeTransition? LiveTransition => modeTransition;
    void InitializeLivePanels()
    {
        bar = new CompactBar(Rename, () => SetCompact(false), Close, RequestOpenRename) { ViewActive = false };
        bar.HideWithoutHost = demoHandle == 0 && !isCustomDataDir;
        panels = new LivePanelHost(this, (SmoothBorder)Content, bar);
        FullRootGrid.Children.Remove(EmojiInputLayer);
        panels.Root.Children.Add(EmojiInputLayer);
        panels.Root.LayoutUpdated += (_, _) => UpdateEmojiInputPlacement();
        LocationChanged += (_, _) => UpdateEmojiInputPlacement();
        panels.Trace = TraceMode;
        panels.PlacementCompleted += () => { if (!closed) { if (!compact) UpdateFullEmojiLayout(); EnableActions(current is not null); _ = Scan(); } };
        bar.LayoutNotice = message => log.Write("布局适配", message);
        CompactToggle.PreviewMouseLeftButtonDown += (_, _) => BeginModePointerTrace();
        bar.RestoreButton.PreviewMouseLeftButtonDown += (_, _) => BeginModePointerTrace();
        bar.SetItems(emojiConfig.Items);
        bar.HostPlace = PlaceMini;
        bar.Motion.Interrupted += InterruptTransition;
        bar.Dragged += () => tracker?.ClampLost();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { InterruptTransition("窗口隐藏"); bar.Motion.Interrupt("窗口隐藏"); bar.FeedbackTip?.Hide(); } };
    }
    void PlaceMini(TrackingNative.Rect rect, double scale)
    {
        if (rect == miniBounds && !panels.Relocating) return;
        miniBounds = rect;
        if (preparingMode) return;
        InterruptTransition("宿主坐标变化");
        panels.Snap(rect, true); bar!.Motion.Interrupt("宿主定位完成");
    }
    internal void SetCompact(bool value)
    {
        if (IsUpdateBusy || profileSwitching || !AssistanceAvailable || closed || operation || EditingEmoji || compact == value) return;
        if (ownHandle == 0) return;
        if (!modeInputPending) { modeClock.Restart(); modeAttempt++; }
        modeInputPending = false; TraceMode("模式请求入口");
        panels.ReserveInitialEnvelope();
        bool reversing = modeTransition is not null;
        if (!compact && !reversing) { fullBounds = panels.PhysicalBounds; fullDpi = panels.Scale; fullTopmost = Topmost; }
        var from = panels.PhysicalBounds;
        double sourceDpi = panels.Scale;
        ++contextVersion; ++visualEpoch; current = null; compact = value;
        preparingMode = true;
        bar!.ViewActive = value;
        try
        {
            if (value)
            {
                Topmost = false;
                if (WindowTracker.IsTopmost(ownHandle)) TrackingNative.SetWindowPos(ownHandle, WindowTracker.HwndNoTopmost, 0, 0, 0, 0, WindowTracker.PlaceAboveFlags);
                TraceMode("置顶已切换；准备 tracker");
                tracker = new WindowTracker(bar, () => !operation, TrackingChanged, demoHandle,
                    testIdentity: DemoLostHost && demoHandle != 0 ? _ => null : null);
                TraceMode("tracker owner/样式/初次定位已处理");
                if (miniBounds == default)
                {
                    var work = TrackingNative.WorkArea(ownHandle);
                    miniBounds = WindowTracker.Place(from, work, sourceDpi, bar.DesignWidth, CompactBar.BarHeight);
                }
                if (OpenBlocked) bar.OpenFeedback(true, detail: openBlockedDetail, context: openFeedbackContext);
            }
            else
            {
                tracker?.Dispose(); tracker = null; previousTarget = null;
                ApplyFullWindowActivationStyle();
                Topmost = fullTopmost; bar.FeedbackTip?.Hide();
            }
        }
        finally { preparingMode = false; }
        EnableActions(false);
        if (!value) fullBounds = fullBounds with { Right = fullBounds.Left + (int)Math.Round(panels.FullSize.Width * fullDpi), Bottom = fullBounds.Top + (int)Math.Round(panels.FullSize.Height * fullDpi) };
        var destination = value ? miniBounds : fullBounds;
        double destinationDpi = value && tracker?.Target is { } target ? Math.Max(96, TrackingNative.GetDpiForWindow(target.Handle)) / 96d : value ? sourceDpi : fullDpi;
        if (sourceDpi != destinationDpi || !panels.Prepare(destination) || panels.Scale != sourceDpi)
        {
            modeTransition?.Finish("环境变化，直接落点"); modeTransition = null;
            panels.Snap(destination, value); CompleteMode("跨 DPI 或运动区域过大，直接落点"); return;
        }
        if (reversing && modeTransition is not null)
        {
            modeTransition.Retarget(LivePanelHost.Dip(destination, sourceDpi), value); return;
        }
        modeTransition = new ModeTransition(panels, bar.Motion, LivePanelHost.Dip(from, sourceDpi), LivePanelHost.Dip(destination, sourceDpi), value, () =>
        {
            var reason = modeTransition?.CompletionReason ?? "已完成"; modeTransition = null;
            CompleteMode(reason);
        });
        LastMotionTransition = "进行中";
        modeTransition.Trace = TraceMode;
        modeTransition.Begin();
        TraceMode("源内容与几何已提交；等待合成帧");
    }
    void CompleteMode(string reason)
    {
        LastMotionTransition = reason;
        TraceMode("结束：" + reason);
        log.Write("真实面板过渡", (compact ? "收起：" : "展开：") + reason);
        panels.SetInteractive(compact);
        bar!.Motion.Transitioning = false;
        // mini 的当前位置和残余速度保留；完整模式不能遗留受力振动。
        if (compact) bar.Motion.ApplyPose(); else bar.Motion.Interrupt("完整模式就绪");
        EnableActions(current is not null);
    }
    void InterruptTransition(string reason)
    {
        if (preparingMode || closingTransition || modeTransition is null || reason == "完整模式就绪") return;
        closingTransition = true;
        try { modeTransition.Finish(reason); }
        finally { closingTransition = false; }
    }
}
