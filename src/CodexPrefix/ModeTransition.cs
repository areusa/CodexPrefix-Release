using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace CodexPrefix;

// 仅调度原控件的布局与变换；不是 Window，也不拥有任何位图或替身内容。
internal sealed class ModeTransition
{
    readonly LivePanelHost host;
    readonly MiniMotionDriver driver;
    readonly Action complete;
    readonly Stopwatch clock = new();
    readonly ModeMotion motion;
    double last;
    bool finished;
    internal string CompletionReason { get; private set; } = "";
    internal ModeMotion Motion => motion;
    internal event Action? FrameAdvanced;
    internal event Action? Completing;
    internal event Action? Completed;
    internal Action<string>? Trace;
    internal ModeTransition(LivePanelHost host, MiniMotionDriver driver, Rect from, Rect to, bool mini, Action complete)
    {
        this.host = host; this.driver = driver; this.complete = complete;
        motion = new ModeMotion(from, to, mini, !SystemParameters.ClientAreaAnimation);
    }
    internal void Begin()
    {
        driver.BeginTransition();
        Render(0); Trace?.Invoke("起始几何与源内容已应用"); clock.Start(); CompositionTarget.Rendering += Tick;
    }
    void Tick(object? sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalSeconds;
        if (last == 0) { last = now; Trace?.Invoke("首个 WPF 渲染回调（不等于屏幕提交）"); return; }
        double dt = now - last; last = now;
        motion.Step(dt); Render(dt);
        FrameAdvanced?.Invoke();
        if (motion.Done) Finish(dt > .25 ? "异常长帧保底" : "正常完成");
    }
    void Render(double dt)
    {
        var body = driver.AdvanceTransition(dt).Value;
        // 完整内容出现时连续卸掉 mini 残余受力，而不是结束回调里突然清零。
        double full = motion.ToMini ? motion.SourceOpacity : motion.TargetOpacity, weight = 1 - full;
        body = new Matrix(1 + (body.M11 - 1) * weight, body.M12 * weight, body.M21 * weight,
            1 + (body.M22 - 1) * weight, body.OffsetX * weight, body.OffsetY * weight);
        var d = motion.Deformation;
        var matrix = Matrix.Identity;
        matrix.Scale(d.X, d.Y); matrix.Append(body);
        host.Apply(motion.Bounds, full,
            motion.ToMini ? motion.TargetOpacity : motion.SourceOpacity, motion.Radius, new MatrixTransform(matrix), false);
    }
    internal void Retarget(Rect target, bool mini) => motion.Retarget(target, mini);
    internal void Finish(string reason)
    {
        if (finished) return;
        finished = true; CompletionReason = reason; motion.Finish();
        CompositionTarget.Rendering -= Tick;
        // 无真假窗口交接：最后一帧仍在同一外壳里，仅恢复命中。
        Render(0); Completing?.Invoke(); complete(); Completed?.Invoke();
    }
}
