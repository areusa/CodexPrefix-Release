namespace CodexPrefix;

// 只约束错误说明的展示；不授予改名、激活或写入权限。
internal sealed class FeedbackContext(WindowIdentity? origin, WindowIdentity? target)
{
    WindowIdentity? boundTarget = target;
    bool reachedTarget = target is not null && origin == target;
    internal bool Fading { get; private set; }
    internal void BeginFade() => Fading = true;
    internal static WindowIdentity? Foreground() => WindowIdentity.Read(WindowTracker.Root(Native.GetForegroundWindow()), false);
    internal bool Allows(WindowIdentity? currentTarget, WindowIdentity? foreground)
    {
        // 完整模式尚无追踪目标时，首次进入小条才固定其展示宿主；固定后不跨目标搬运提示。
        boundTarget ??= currentTarget;
        if (boundTarget != currentTarget || foreground is null) return false;
        if (boundTarget is not null && foreground == boundTarget) { reachedTarget = true; return true; }
        // 点击前已在其他应用时允许解释本次拒绝；到过目标后，不再把原应用当作展示场景。
        return !reachedTarget && origin is not null && foreground == origin;
    }
}
