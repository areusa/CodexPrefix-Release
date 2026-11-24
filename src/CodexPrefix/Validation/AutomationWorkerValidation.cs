using System.Diagnostics;
using System.Text.Json;

namespace CodexPrefix;

// 仅运行命名管道协议夹具，子进程不创建 UIA 对象、不连接任何真实窗口。
internal static class AutomationWorkerValidation
{
    internal static async Task<int> Run()
    {
        var checks = new List<object>(); bool failed = false;
        void Check(string name, bool pass) { checks.Add(new { name, pass }); failed |= !pass; }
        var log = new LocalLog(System.IO.Path.Combine(AppContext.BaseDirectory, "fixture-data", Guid.NewGuid().ToString("N"), "logs"));
        using var worker = new AutomationWorker(fixtureProcess: true, log);
        try
        {
            var binding = new EditorBinding(unchecked((nint)0x1fedcba9876), 123, "范围甲", "输入甲", "保存甲", "✌️ 中文  空格 👩‍💻");
            var packet = new AutomationPacket { Binding = binding, Context = new(unchecked((nint)0x123456789ab), true, new(unchecked((nint)0x1fedcba9876), 123, 456)) };
            var decoded = JsonSerializer.Deserialize<AutomationPacket>(JsonSerializer.Serialize(packet, AutomationPacket.Json), AutomationPacket.Json)!;
            Check("协议保留 64 位 HWND、目标身份与 UTF-8 原始文本", decoded.Binding == binding && decoded.Context == packet.Context);
            Check("夹具进程不经 UIA 即可完成初始化", (await worker.Fixture("fixture-ready")).Text == "就绪");
            int first = worker.ActiveProcessId ?? throw new InvalidOperationException("未取得夹具进程。");
            var clock = Stopwatch.StartNew(); bool timedOut = false;
            try { await worker.Fixture("fixture-read-hang"); } catch (TimeoutException) { timedOut = true; }
            Check("同步读永不返回时有界失败", timedOut && clock.ElapsedMilliseconds < 4000);
            Check("超时返回之前旧进程已确认退出", Exited(first));
            Check("读超时不产生写入不确定封锁", !worker.WritesBlocked);
            Check("旧进程退出后新请求使用新进程恢复", (await worker.Fixture("fixture-ready")).Text == "就绪" && worker.ActiveProcessId != first);
            int crash = worker.ActiveProcessId!.Value; bool crashed = false;
            try { await worker.Fixture("fixture-crash"); } catch (TimeoutException) { crashed = true; }
            Check("子进程异常退出不占住后续队列", crashed && Exited(crash) && (await worker.Fixture("fixture-ready")).Text == "就绪");
            int canceled = worker.ActiveProcessId!.Value;
            using (var cancellation = new CancellationTokenSource(80))
            {
                bool stopped = false;
                try { await worker.Fixture("fixture-read-hang", token: cancellation.Token); } catch (OperationCanceledException) { stopped = true; }
                Check("取消进行中请求先终止旧进程", stopped && Exited(canceled));
            }
            await worker.Fixture("fixture-ready");
            int write = worker.ActiveProcessId!.Value; bool uncertain = false;
            try { await worker.Fixture("fixture-write-hang", binding); } catch (AutomationWriteUncertainException) { uncertain = true; }
            Check("写入超时确认终结旧进程并封锁继续写入", uncertain && Exited(write) && worker.WritesBlocked);
            worker.ObserveBinding(binding with { OriginalValue = "用户检查后的文本" });
            Check("同一原生输入框改文本不能解除不确定封锁", worker.WritesBlocked);
            worker.ObserveBinding(binding with { EditorId = "重建的输入控件" });
            Check("同一对话范围重建输入控件不能解除封锁", worker.WritesBlocked);
            worker.ObserveBinding(binding with { SaveId = "重建的保存控件" });
            Check("同一对话范围重建保存控件不能解除封锁", worker.WritesBlocked);
            worker.ObserveBinding(null);
            Check("一次未发现输入框不作为关闭完成证据", worker.WritesBlocked);
            worker.ObserveBinding(binding with { ScopeId = "重新打开后的原生范围" });
            Check("明确不同的原生输入范围才解除封锁", !worker.WritesBlocked);
            var unicode = await worker.Fixture("fixture-unicode", binding);
            Check("跨进程往返保留 emoji 变体、ZWJ 及双空格", unicode.Text == binding.OriginalValue && unicode.Probe?.Binding == binding);
            Check("跨进程写许可握手可允许当前配置", (await worker.Fixture("fixture-permission", permission: () => true)).Text == "允许");
            Check("跨进程写许可握手拒绝失效配置", (await worker.Fixture("fixture-permission", permission: () => false)).Text == "拒绝");
            int permissions = 0;
            Check("写入与提交之间配置变化会撤销第二次许可", (await worker.Fixture("fixture-permission-twice", permission: () => ++permissions == 1)).Text == "允许/拒绝");
            var readyRequests = Enumerable.Range(0, 4).Select(_ => worker.Fixture("fixture-ready")).ToArray();
            var readyResponses = await Task.WhenAll(readyRequests);
            Check("并发请求通过同一管道串行且响应代次不串线", readyResponses.All(response => response.Text == "就绪") && readyResponses.Select(response => response.Id).Distinct().Count() == 4);
            int last = worker.ActiveProcessId!.Value; worker.Dispose();
            Check("父控制器释放时终结隔离进程", Exited(last));
            using (var ordinaryFailure = new AutomationWorker(fixtureProcess: true, log))
            {
                bool writeFailed = false;
                try { await ordinaryFailure.Fixture("rename", binding); } catch (InvalidOperationException) { writeFailed = true; }
                Check("已发送写操作普通异常也保持父侧封锁", writeFailed && ordinaryFailure.WritesBlocked);
                try { await ordinaryFailure.Fixture("fixture-read-hang"); } catch (AutomationWriteUncertainException) { }
                Check("后续读超时重启不会遗失旧写入封锁", ordinaryFailure.WritesBlocked);
                var blocked = await ordinaryFailure.Fixture("fixture-unicode", binding);
                Check("同一输入框重新识别不能对界面授予绑定", blocked.Probe?.Binding is null && blocked.Probe?.Status.Kind == StatusKind.NeedsAction);
                var rebuilt = await ordinaryFailure.Fixture("fixture-unicode", binding with { EditorId = "重建输入", SaveId = "重建保存" });
                Check("同一对话范围控件双重重建仍不授予绑定", rebuilt.Probe?.Binding is null && ordinaryFailure.WritesBlocked);
                var fresh = await ordinaryFailure.Fixture("fixture-unicode", binding with { ScopeId = "再次重新打开" });
                Check("重新打开的不同原生范围可以恢复识别与写许可", fresh.Probe?.Binding is not null && !ordinaryFailure.WritesBlocked);
                await ordinaryFailure.Fixture("fixture-stop-reading");
                clock.Restart(); bool stalledWrite = false;
                try { await ordinaryFailure.Fixture("fixture-unicode", binding with { OriginalValue = new string('测', 2_000_000) }); } catch (TimeoutException) { stalledWrite = true; }
                Check("管道对端停止读取时发送请求同样有界", stalledWrite && clock.ElapsedMilliseconds < 4000);
            }
            using (var startup = new AutomationWorker(fixtureProcess: true, log, fixtureStartupHang: true))
            {
                clock.Restart(); bool initializationFailed = false;
                try { await startup.Fixture("fixture-ready"); } catch (TimeoutException) { initializationFailed = true; }
                Check("管道已连接但不回初始化回执时启动握手仍有界", initializationFailed && clock.ElapsedMilliseconds < 10000 && startup.ActiveProcessId is null);
            }
        }
        catch (Exception error) { failed = true; checks.Add(new { name = "隔离进程验证异常", pass = false, error = error.GetType().Name + ":" + error.Message }); }
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "automation-worker-validation.json"), JsonSerializer.Serialize(new { passed = !failed, checks }, new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(true));
        return failed ? 1 : 0;
    }
    static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
}
