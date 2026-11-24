using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexPrefix;

public partial class MainWindow
{
    AssistanceLifecycle? assistance;
    TrayController? tray;
    readonly DispatcherTimer hostTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    CancellationTokenSource? assistanceCancellation;
    bool hostTemporarilyHidden;
    bool exitRequested;
    CancellationToken WorkToken => assistanceCancellation?.Token ?? lifetime.Token;
    bool AssistanceAvailable => assistance is null || (assistance.Active && assistance.Host is { } h && h.IsCurrent()
        && Native.IsWindowVisible(h.Handle) && !Native.IsIconic(h.Handle) && !TrackingNative.Cloaked(h.Handle));

    internal void StartAssistance()
    {
        if (demoHandle != 0 || isCustomDataDir) { Show(); return; }
        assistance = new();
        tray = new TrayController(Dispatcher, () => !closed && !operation && FindAssistanceHost() is not null,
            () => assistance.Active, ResumeAssistance, () => SuspendAssistance("用户收纳并暂停"), ExitAssistance);
        hostTimer.Tick += (_, _) => CheckAssistanceHost();
        hostTimer.Start();
        ResumeAssistance();
    }
    WindowIdentity? FindAssistanceHost()
    {
        var active = WindowTracker.Root(Native.GetForegroundWindow());
        if (WindowTracker.MainCandidate(active) && WindowIdentity.Read(active) is { } foreground) return foreground;
        var candidates = WindowTracker.Candidates(h => WindowIdentity.Read(h));
        return candidates.Count == 1 ? WindowIdentity.Read(candidates[0]) : null;
    }
    void ResumeAssistance()
    {
        if (closed || operation || assistance is null) return;
        var host = FindAssistanceHost();
        if (host is null) return;
        assistanceCancellation?.Dispose();
        assistanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        assistance.Enable(host);
        hostTemporarilyHidden = !Native.IsWindowVisible(host.Handle) || Native.IsIconic(host.Handle) || TrackingNative.Cloaked(host.Handle);
        IsEnabled = !hostTemporarilyHidden;
        if (!hostTemporarilyHidden)
        {
            if (!IsVisible) Show();
            if (compact && tracker is null && bar is not null)
                tracker = new WindowTracker(bar, () => !operation && assistance.Active, TrackingChanged);
            timer.Start();
        }
        log.Write("辅助生命周期", "经启动或托盘明确启用；目标窗口身份已核验。");
    }
    void SuspendAssistance(string reason)
    {
        if (assistance is null || closed) return;
        assistance.Suspend();
        ++contextVersion; ++visualEpoch; Interlocked.Increment(ref emojiGeneration);
        current = null;
        assistanceCancellation?.Cancel();
        timer.Stop();
        InterruptTransition("辅助收纳");
        tracker?.Dispose(); tracker = null; previousTarget = null;
        bar?.SetActions(false, false); bar?.SetOpenEnabled(false); bar?.FeedbackTip?.Hide();
        IsEnabled = false;
        Hide();
        log.Write("辅助生命周期", reason + "；已隐藏并封锁操作，只能从托盘重新启用。");
    }
    void CheckAssistanceHost()
    {
        if (closed || assistance is null || !assistance.Active) return;
        var host = assistance.Host;
        if (assistance.Observe(host is not null && host.IsCurrent() && WindowIdentity.Read(host.Handle) == host))
        { SuspendAssistance("宿主关闭或身份失效"); return; }
        if (host is null) return;
        bool visible = Native.IsWindowVisible(host.Handle) && !Native.IsIconic(host.Handle) && !TrackingNative.Cloaked(host.Handle);
        if (!visible && !hostTemporarilyHidden)
        {
            hostTemporarilyHidden = true;
            ++contextVersion; ++visualEpoch; Interlocked.Increment(ref emojiGeneration); current = null;
            assistanceCancellation?.Cancel();
            timer.Stop();
            InterruptTransition("宿主最小化或隐藏");
            IsEnabled = false; Hide();
        }
        else if (visible && hostTemporarilyHidden)
        {
            assistanceCancellation?.Dispose();
            assistanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            hostTemporarilyHidden = false;
            IsEnabled = true;
            if (!IsVisible) Show();
            timer.Start();
        }
        if (tracker is not null && !operation && !EditingEmoji && tracker.Target is { } tracked && tracked != host)
            assistance.Enable(tracked);
    }
    void ExitAssistance() { exitRequested = true; Close(); }
    void DisposeAssistance()
    {
        hostTimer.Stop();
        assistanceCancellation?.Cancel(); assistanceCancellation?.Dispose();
        tray?.Dispose(); tray = null;
    }
}
