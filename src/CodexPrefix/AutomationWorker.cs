using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodexPrefix;

internal sealed record AutomationContext(nint OwnWindow, bool Tracking, WindowIdentity? Target);
internal sealed class AutomationWriteUncertainException(string message) : InvalidOperationException(message);

// 正式窗口的同步 UIA 调用限定在独立进程内。超时先终结旧进程，再允许下一次请求。
public sealed class AutomationWorker : IDisposable
{
    readonly LocalLog log;
    readonly BlockingCollection<(Action<CodexBridge> Run, Action<Exception> Fail)>? demoQueue;
    readonly SemaphoreSlim serial = new(1, 1);
    readonly CancellationTokenSource lifetime = new();
    readonly object processGate = new();
    Process? process;
    NamedPipeServerStream? pipe;
    StreamReader? reader;
    StreamWriter? writer;
    long nextRequest, activeRequest;
    volatile bool disposed;
    bool terminationFailed;
    EditorBinding? uncertainWrite;
    readonly bool fixtureProcess;
    readonly bool fixtureStartupHang;
    internal bool WritesBlocked => uncertainWrite is not null;
    internal int? ActiveProcessId => process is { HasExited: false } child ? child.Id : null;
    internal AutomationWorker(bool fixtureProcess, LocalLog log, bool fixtureStartupHang = false) : this(0, log) { this.fixtureProcess = fixtureProcess; this.fixtureStartupHang = fixtureStartupHang; }
    public AutomationWorker(nint demoWindow, LocalLog log)
    {
        this.log = log;
        if (demoWindow == 0) return;
        demoQueue = new();
        var thread = new Thread(() =>
        {
            try
            {
                using var bridge = new CodexBridge(demoWindow, log);
                foreach (var action in demoQueue.GetConsumingEnumerable()) action.Run(bridge);
            }
            catch (Exception ex)
            {
                foreach (var action in demoQueue.GetConsumingEnumerable()) action.Fail(new InvalidOperationException("Windows 可访问性服务无法初始化", ex));
            }
        }) { IsBackground = true, Name = "独立夹具可访问性线程" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
    }
    // 任意委托只保留给本进程的隔离夹具；正式模式必须使用下面的显式协议。
    public Task<T> Run<T>(Func<CodexBridge, T> action)
    {
        if (demoQueue is null) throw new InvalidOperationException("正式自动化必须使用隔离进程入口。");
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        demoQueue.Add((bridge => { try { result.TrySetResult(action(bridge)); } catch (Exception ex) { result.TrySetException(ex); } }, ex => result.TrySetException(ex)));
        return result.Task;
    }
    static void Configure(CodexBridge bridge, AutomationContext context)
    { bridge.OwnWindow = context.OwnWindow; bridge.TrackingMode = context.Tracking; bridge.TrackedTarget = context.Target; }
    internal Task<ProbeResult> Probe(AutomationContext context, Action<WorkStatus>? progress = null, CancellationToken token = default) => demoQueue is not null
        ? Run(bridge => { Configure(bridge, context); return bridge.Probe(progress); })
        : ReadProbe(new() { Operation = "probe", Context = context }, progress, token);
    internal Task<ProbeResult> Open(AutomationContext context, Action<WorkStatus>? progress = null, CancellationToken token = default) => demoQueue is not null
        ? Run(bridge => { Configure(bridge, context); return bridge.OpenRename(token, progress); })
        : ReadProbe(new() { Operation = "open", Context = context }, progress, token);
    async Task<ProbeResult> ReadProbe(AutomationPacket request, Action<WorkStatus>? progress, CancellationToken token)
    {
        var response = await Execute(request, progress, token);
        var result = response.Probe ?? throw new InvalidDataException("识别进程未返回完整结果。");
        // 只有不同原生范围才能解除不确定写入封锁；同一输入框的文本变化不能解锁。
        return ProtectProbe(result);
    }
    ProbeResult ProtectProbe(ProbeResult result)
    {
        ObserveBinding(result.Binding);
        return WritesBlocked && result.Binding is not null
            ? new(null, result.ReferenceTitle, new(StatusKind.NeedsAction, "上次写入结果待核验，请关闭原输入框后重新打开", "不会在同一输入框内自动重复写入或保存。")) : result;
    }
    internal void ObserveBinding(EditorBinding? binding)
    {
        if (binding is not null && uncertainWrite is { } old && (binding.Window != old.Window || binding.ProcessId != old.ProcessId || binding.ScopeId != old.ScopeId)) uncertainWrite = null;
    }
    internal Task<ProbeResult?> Recover(AutomationContext context, WindowIdentity target, CancellationToken token = default) => demoQueue is not null
        ? Run(bridge => { Configure(bridge, context); return bridge.RecoverOpen(target, token); })
        : RecoverRemote(context, target, token);
    async Task<ProbeResult?> RecoverRemote(AutomationContext context, WindowIdentity target, CancellationToken token)
    {
        var result = (await Execute(new() { Operation = "recover", Context = context, Target = target }, null, token)).Probe;
        return result is null ? null : ProtectProbe(result);
    }
    internal Task<RenameResult> Rename(AutomationContext context, EditorBinding binding, string? icon, PrefixSnapshot rules, Func<bool> rulesCurrent, Action<WorkStatus> progress, CancellationToken token = default) => demoQueue is not null
        ? Run(bridge => { Configure(bridge, context); return bridge.Rename(binding, icon, progress, token, rules, rulesCurrent); })
        : RenameRemote(context, binding, icon, rules, rulesCurrent, progress, token);
    async Task<RenameResult> RenameRemote(AutomationContext context, EditorBinding binding, string? icon, PrefixSnapshot rules, Func<bool> rulesCurrent, Action<WorkStatus> progress, CancellationToken token)
    {
        if (WritesBlocked) throw new AutomationWriteUncertainException("上次写入结果不确定。请检查原生名称，关闭原重命名框并重新打开后再操作；不会自动重复保存。");
        var request = new AutomationPacket { Operation = "rename", Context = context, Binding = binding, Icon = icon, Revision = rules.Revision, Items = rules.Items.ToArray(), History = rules.History.ToArray() };
        return (await Execute(request, progress, token, rulesCurrent)).Rename ?? throw new InvalidDataException("写入进程未返回完整结果。");
    }
    internal Task<string> Diagnostic(AutomationContext context, CancellationToken token = default) => demoQueue is not null
        ? Run(bridge => { Configure(bridge, context); return bridge.Diagnostic(); })
        : DiagnosticRemote(context, token);
    async Task<string> DiagnosticRemote(AutomationContext context, CancellationToken token) => (await Execute(new() { Operation = "diagnostic", Context = context }, null, token)).Text ?? "";
    internal async Task<AutomationPacket> Fixture(string operation, EditorBinding? binding = null, CancellationToken token = default, Func<bool>? permission = null)
    {
        if (!fixtureProcess) throw new InvalidOperationException("故障注入仅允许隔离协议夹具。");
        var response = await Execute(new() { Operation = operation, Binding = binding }, null, token, permission);
        if (response.Probe is { } probe) response.Probe = ProtectProbe(probe);
        return response;
    }
    async Task Start(CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(6)); token = startup.Token;
        if (terminationFailed) throw new InvalidOperationException("未能确认旧自动化进程退出，请退出工具后重试；未启动新进程。");
        if (process is { HasExited: false } && pipe?.IsConnected == true) return;
        Stop();
        if (terminationFailed) throw new InvalidOperationException("旧自动化进程未确认退出，已禁止启动新进程。");
        string pipeName = "CodexPrefix.Automation." + Guid.NewGuid().ToString("N");
        NamedPipeServerStream connection;
        lock (processGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
            pipe = connection = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--automation-worker"); start.ArgumentList.Add(pipeName); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (fixtureProcess) start.ArgumentList.Add("--automation-fixture");
            if (fixtureStartupHang) start.ArgumentList.Add("--automation-fixture-no-ready");
            process = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离自动化进程。");
        }
        await connection.WaitForConnectionAsync(token);
        StreamReader responseReader; StreamWriter requestWriter;
        lock (processGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
            reader = responseReader = new(connection, new UTF8Encoding(false, true), false, 4096, true);
            writer = requestWriter = new(connection, new UTF8Encoding(false, true), 4096, true) { AutoFlush = true };
        }
        await requestWriter.WriteLineAsync(JsonSerializer.Serialize(new AutomationPacket { Operation = "initialize", Text = log.Folder }, AutomationPacket.Json).AsMemory(), token);
        var ready = JsonSerializer.Deserialize<AutomationPacket>(await responseReader.ReadLineAsync(token) ?? throw new EndOfStreamException(), AutomationPacket.Json);
        if (ready?.Kind != "ready") throw new InvalidDataException("隔离进程初始化回执无效。");
    }
    async Task<AutomationPacket> Execute(AutomationPacket request, Action<WorkStatus>? progress, CancellationToken token, Func<bool>? rulesCurrent = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await serial.WaitAsync(linked.Token);
        bool sent = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await Start(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (request.Operation == "rename" && (WritesBlocked || rulesCurrent?.Invoke() == false)) throw new InvalidOperationException("写入许可或 emoji 配置已变化，本次未发送。");
            request.Id = Interlocked.Increment(ref nextRequest); Interlocked.Exchange(ref activeRequest, request.Id);
            // 用户点击父面板后，将其已有的前台切换许可授予自有子进程；此处不激活任何窗口。
            if (!fixtureProcess && request.Operation == "open" && process is { HasExited: false } child) AllowSetForegroundWindow((uint)child.Id);
            var budget = TimeSpan.FromMilliseconds(fixtureProcess ? 450 : request.Operation is "open" ? 14000 : request.Operation is "rename" ? 12000 : request.Operation is "diagnostic" ? 12000 : 7000);
            linked.CancelAfter(budget);
            sent = true;
            var requestWriter = writer!;
            await requestWriter.WriteLineAsync(JsonSerializer.Serialize(request, AutomationPacket.Json).AsMemory(), linked.Token);
            var response = await Read(request.Id, progress, linked.Token, rulesCurrent, requestWriter);
            if (request.Operation == "rename" && response.Rename?.Status.Kind is not (StatusKind.Submitted or StatusKind.Completed)) uncertainWrite = request.Binding;
            return response;
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException or InvalidDataException or JsonException or ObjectDisposedException)
        {
            Interlocked.Exchange(ref activeRequest, 0);
            linked.Cancel();
            if (sent && request.Operation is "rename" or "fixture-write-hang") uncertainWrite = request.Binding;
            Stop();
            log.Write("自动化隔离", $"操作={request.Operation};异常={error.GetType().Name};旧进程已终结={!terminationFailed};写入待核验={WritesBlocked}");
            if (terminationFailed) throw new InvalidOperationException("旧自动化进程未确认退出，已禁止重新启动和继续写入。", error);
            if (WritesBlocked) throw new AutomationWriteUncertainException("自动化写入被中断，结果无法确认。请核对原生界面，关闭该输入框并重新打开；不会自动重试。");
            if (error is OperationCanceledException && (token.IsCancellationRequested || lifetime.IsCancellationRequested)) throw;
            throw new TimeoutException("原生可访问性请求未完成，隔离进程已终止。可手动打开重命名框后重新识别。", error);
        }
        catch
        {
            // 即使 provider 返回普通异常，已发送写入也可能执行了一部分；封锁跨进程重启保留。
            if (sent && request.Operation is "rename" or "fixture-write-hang") uncertainWrite = request.Binding;
            throw;
        }
        finally { Interlocked.Exchange(ref activeRequest, 0); serial.Release(); }
    }
    async Task<AutomationPacket> Read(long id, Action<WorkStatus>? progress, CancellationToken token, Func<bool>? rulesCurrent, StreamWriter requestWriter)
    {
        var responseReader = reader!;
        while (true)
        {
            var line = await responseReader.ReadLineAsync(token) ?? throw new EndOfStreamException("隔离进程已断开。");
            var message = JsonSerializer.Deserialize<AutomationPacket>(line, AutomationPacket.Json) ?? throw new InvalidDataException("隔离响应格式错误。");
            if (message.Id != id) throw new InvalidDataException("隔离响应代次不一致。");
            if (message.Kind == "permission")
            {
                bool allowed = Interlocked.Read(ref activeRequest) == id && !token.IsCancellationRequested && !WritesBlocked && rulesCurrent?.Invoke() != false;
                await requestWriter.WriteLineAsync(JsonSerializer.Serialize(new AutomationPacket { Id = id, Kind = "permission", Allowed = allowed }, AutomationPacket.Json).AsMemory(), token);
                continue;
            }
            if (message.Kind == "progress") { if (Interlocked.Read(ref activeRequest) == id && message.Status is { } status) progress?.Invoke(status); continue; }
            if (message.Kind == "error")
            {
                if (!string.IsNullOrEmpty(message.ErrorCode)) throw new OpenRenameBlockedException(message.ErrorCode, message.Text ?? "原生识别失败。") { RecoveryTarget = message.Target, RequestId = message.RequestId ?? "" };
                if (message.Canceled) throw new OperationCanceledException();
                throw new InvalidOperationException(message.Text ?? "隔离自动化操作失败。");
            }
            if (message.Kind != "result") throw new InvalidDataException("隔离响应类型错误。");
            return message;
        }
    }
    void Stop()
    {
        lock (processGate)
        {
        Interlocked.Exchange(ref activeRequest, 0);
        var child = process;
        if (child is not null)
        {
            try
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                if (!child.WaitForExit(2500)) { terminationFailed = true; return; }
                child.Dispose(); process = null;
            }
            catch { terminationFailed = true; return; }
        }
        pipe?.Dispose();
        try { reader?.Dispose(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        try { writer?.Dispose(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        reader = null; writer = null; pipe = null;
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; lifetime.Cancel();
        if (demoQueue is not null) demoQueue.CompleteAdding();
        else Stop();
    }
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint processId);
}
