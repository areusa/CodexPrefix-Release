using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexPrefix;

internal sealed class AutomationPacket
{
    public long Id { get; set; }
    public string Kind { get; set; } = "request";
    public string Operation { get; set; } = "";
    public AutomationContext? Context { get; set; }
    public WindowIdentity? Target { get; set; }
    public EditorBinding? Binding { get; set; }
    public string? Icon { get; set; }
    public long Revision { get; set; }
    public EmojiItem[]? Items { get; set; }
    public string[]? History { get; set; }
    public WorkStatus? Status { get; set; }
    public ProbeResult? Probe { get; set; }
    public RenameResult? Rename { get; set; }
    public string? Text { get; set; }
    public string? ErrorCode { get; set; }
    public string? RequestId { get; set; }
    public bool Canceled { get; set; }
    public bool Allowed { get; set; }
    internal static readonly JsonSerializerOptions Json = new() { Converters = { new NativeIntegerConverter() } };
    sealed class NativeIntegerConverter : JsonConverter<nint>
    {
        public override nint Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => checked((nint)reader.GetInt64());
        public override void Write(Utf8JsonWriter writer, nint value, JsonSerializerOptions options) => writer.WriteNumberValue(value.ToInt64());
    }
}

internal static class AutomationWorkerHost
{
    // App 在任何可见窗口、互斥锁或全局异常弹窗建立前调用。仅管道名和父 PID 出现在命令行。
    internal static int Run(string pipeName, int parentId, bool fixture)
    {
        int exit = 1;
        var thread = new Thread(() =>
        {
            try { exit = Serve(pipeName, parentId, fixture); }
            catch { exit = 1; }
        }) { IsBackground = true, Name = "隔离原生可访问性线程" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); thread.Join(); return exit;
    }
    static int Serve(string pipeName, int parentId, bool fixture)
    {
        if (!pipeName.StartsWith("CodexPrefix.Automation.", StringComparison.Ordinal) || parentId <= 0 || parentId == Environment.ProcessId) return 2;
        using var parent = Process.GetProcessById(parentId);
        if (parent.HasExited || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 2;
        // 父程序异常退出时也结束本进程；不是依赖管道循环再次读取才退出。
        _ = Task.Run(async () => { try { await parent.WaitForExitAsync(); } finally { Environment.Exit(0); } });
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(6000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, true) { AutoFlush = true };
        var init = JsonSerializer.Deserialize<AutomationPacket>(reader.ReadLine() ?? "", AutomationPacket.Json);
        if (init?.Operation != "initialize" || string.IsNullOrWhiteSpace(init.Text)) return 2;
        if (fixture && Environment.GetCommandLineArgs().Contains("--automation-fixture-no-ready")) Thread.Sleep(Timeout.Infinite);
        var log = new LocalLog(init.Text);
        Write(new() { Kind = "ready" });
        CodexBridge? bridge = null;
        try
        {
            while (reader.ReadLine() is { } line)
            {
                var request = JsonSerializer.Deserialize<AutomationPacket>(line, AutomationPacket.Json) ?? throw new InvalidDataException();
                try
                {
                    var response = new AutomationPacket { Id = request.Id, Kind = "result" };
                    bool Permission()
                    {
                        Write(new() { Id = request.Id, Kind = "permission" });
                        var permission = JsonSerializer.Deserialize<AutomationPacket>(reader.ReadLine() ?? throw new EndOfStreamException(), AutomationPacket.Json);
                        return permission?.Id == request.Id && permission.Kind == "permission" && permission.Allowed;
                    }
                    if (fixture)
                    {
                        // 无 UIA 实例、无目标窗口；仅验证进程与协议的失效恢复。
                        switch (request.Operation)
                        {
                            case "fixture-read-hang": case "fixture-write-hang": Thread.Sleep(Timeout.Infinite); break;
                            case "fixture-crash": Environment.Exit(7); break;
                            case "fixture-unicode": response.Text = request.Binding?.OriginalValue; response.Probe = new(request.Binding, null, new(StatusKind.Ready, "隔离编码回执")); break;
                            case "rename": throw new InvalidOperationException("夹具模拟写入后的普通异常。");
                            case "fixture-stop-reading": response.Text = "停止读取"; break;
                            case "fixture-permission": response.Text = Permission() ? "允许" : "拒绝"; break;
                            case "fixture-permission-twice": response.Text = (Permission() ? "允许" : "拒绝") + "/" + (Permission() ? "允许" : "拒绝"); break;
                            case "fixture-ready": response.Text = "就绪"; break;
                            default: throw new InvalidOperationException("协议夹具拒绝真实自动化操作。");
                        }
                    }
                    else
                    {
                        bridge ??= new CodexBridge(0, log);
                        bridge.ConfigureProviderTimeouts();
                        var context = request.Context ?? throw new InvalidDataException("缺少目标上下文。");
                        bridge.OwnWindow = context.OwnWindow; bridge.TrackingMode = context.Tracking; bridge.TrackedTarget = context.Target;
                        void Progress(WorkStatus status) => Write(new() { Id = request.Id, Kind = "progress", Status = status });
                        switch (request.Operation)
                        {
                            case "probe": response.Probe = bridge.Probe(Progress); break;
                            case "open": response.Probe = bridge.OpenRename(default, Progress); break;
                            case "recover": response.Probe = bridge.RecoverOpen(request.Target ?? throw new InvalidDataException(), default); break;
                            case "rename":
                                var rules = new PrefixSnapshot(request.Revision, request.Items ?? [], request.History ?? []);
                                response.Rename = bridge.Rename(request.Binding ?? throw new InvalidDataException(), request.Icon, Progress, default, rules, Permission); break;
                            case "diagnostic": response.Text = bridge.Diagnostic(); break;
                            default: throw new InvalidDataException("未知自动化操作。");
                        }
                    }
                    Write(response);
                    if (fixture && request.Operation == "fixture-stop-reading") Thread.Sleep(Timeout.Infinite);
                }
                catch (Exception error)
                {
                    var open = error as OpenRenameBlockedException;
                    Write(new() { Id = request.Id, Kind = "error", ErrorCode = open?.Code, Target = open?.RecoveryTarget, RequestId = open?.RequestId, Text = error.Message, Canceled = error is OperationCanceledException });
                }
            }
            return 0;
        }
        finally { bridge?.Dispose(); }
        void Write(AutomationPacket value) => writer.WriteLine(JsonSerializer.Serialize(value, AutomationPacket.Json));
    }
}

public sealed partial class CodexBridge
{
    internal void ConfigureProviderTimeouts()
    {
        // 原生 provider 的单次等待短于业务总预算；真正兜底仍是父进程可终结的进程边界。
        automation.ConnectionTimeout = TimeSpan.FromMilliseconds(800);
        automation.TransactionTimeout = TimeSpan.FromMilliseconds(1500);
    }
}
