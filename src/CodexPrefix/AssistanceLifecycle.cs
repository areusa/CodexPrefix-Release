namespace CodexPrefix;

internal enum AssistanceState { WaitingForHost, Active, Suspended }

internal sealed class AssistanceLifecycle
{
    internal AssistanceState State { get; private set; } = AssistanceState.WaitingForHost;
    internal WindowIdentity? Host { get; private set; }
    internal bool Active => State == AssistanceState.Active;
    internal void Enable(WindowIdentity host) { Host = host; State = AssistanceState.Active; }
    internal bool Observe(bool sameHostAlive)
    {
        if (!Active || sameHostAlive) return false;
        Suspend();
        return true;
    }
    internal void Suspend() { State = AssistanceState.Suspended; Host = null; }
    internal bool CanOperate(WindowIdentity? candidate) => Active && Host is not null && Host == candidate;
}
