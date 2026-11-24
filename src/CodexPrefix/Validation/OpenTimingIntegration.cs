using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace CodexPrefix;

// 仅在本程序创建的独立夹具内运行；故障注入不对真实 Codex 开放。
internal static class OpenTimingIntegration
{
    internal static async Task Run(Action<string, bool, object?> check)
    {
        var evidence = new List<object>();
        async Task Case(string name, int openDelay = 0, int editDelay = 0, int saveDelay = 0,
            int transientAfter = 0, int transientBefore = 0, int budget = 8000,
            bool ignore = false, bool late = false, bool repeat = false, bool fatal = false,
            bool existing = false, bool ambiguous = false, bool closePending = false, bool staleScan = false)
        {
            var mock = new MockWindow(ignoreShortcut: ignore) { ShortcutDelayMilliseconds = openDelay, EditorDelayMilliseconds = editDelay, SaveDelayMilliseconds = saveDelay };
            mock.Show();
            var main = new MainWindow(mock); main.Show();
            var clock = new Stopwatch();
            var states = new List<(long Ms, StatusKind Kind, string Text)>();
            main.StatusChanged += status => states.Add((clock.ElapsedMilliseconds, status.Kind, status.Text));
            int faultCount = 0;
            int delayedScans = 0;
            var scanEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                if (staleScan) await main.PauseDemoScanning();
                await main.ConfigureDemoOpen(bridge =>
                {
                    bridge.DemoOpenWaitMilliseconds = budget;
                    if (staleScan) bridge.DemoBeforeProbe = () => { bridge.DemoBeforeProbe = null; ++delayedScans; scanEntered.TrySetResult(true); Thread.Sleep(600); };
                    bridge.DemoOpenProbe = (phase, attempt) =>
                    {
                        if (fatal && phase == "发送后") throw new COMException("夹具权限错误，不得当作瞬态重试", unchecked((int)0x80070005));
                        if ((phase == "发送前" && attempt <= transientBefore) || (phase == "发送后" && attempt <= transientAfter))
                        {
                            ++faultCount;
                            throw new COMException("夹具复现 UIA 控件重建", unchecked((int)0x80040201));
                        }
                    };
                });
                mock.Activate();
                if (existing || ambiguous) mock.OpenTestRename(ambiguous);
                clock.Start();
                var oldScan = staleScan ? main.RefreshDemo() : Task.CompletedTask;
                if (staleScan) await scanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var task = main.RequestOpenRename();
                if (repeat || closePending)
                {
                    var wait = Stopwatch.StartNew();
                    while (mock.ShortcutCount == 0 && !task.IsCompleted && wait.ElapsedMilliseconds < 10000) await Task.Delay(25);
                    if (repeat)
                    {
                        var second = Stopwatch.StartNew();
                        await main.RequestOpenRename();
                        check(name + "：处理中重复点击立即返回", second.ElapsedMilliseconds < 200, null);
                    }
                    if (closePending) main.Close();
                }
                await task.WaitAsync(TimeSpan.FromSeconds(25));
                await oldScan;
                long completed = clock.ElapsedMilliseconds;
                bool ready = main.CurrentStatus?.Kind == StatusKind.Ready && main.CurrentBinding is not null;
                bool error = states.Any(state => state.Kind == StatusKind.Error);
                if (closePending)
                {
                    await Task.Delay(openDelay + 100);
                    check(name + "：关闭后不会继续弹窗或回写状态", !mock.IsVisible && !main.IsVisible && !Application.Current.Windows.OfType<Window>().Any(window => window.Owner == mock), null);
                }
                else if (ignore || fatal || ambiguous)
                {
                    check(name + "：不得误报成功或授予绑定", !ready && main.CurrentBinding is null && !states.Any(state => state.Kind == StatusKind.Ready), null);
                    check(name + "：反馈结束后允许重试", !main.OpenBlocked && !main.IsWorking, null);
                    if (fatal) check(name + "：非瞬态权限错误不等待八秒重试", states.First(state => state.Kind == StatusKind.Error).Ms < 8000, null);
                    if (ignore) check(name + "：等待上限前不出现受阻提示", states.First(state => state.Kind == StatusKind.Error).Ms >= budget, null);
                }
                else
                {
                    check(name + "：严格识别成功", ready, null);
                    check(name + "：等待期间不提前误报失败", late ? error : !error, null);
                    check(name + "：控件真正就绪之前不授予绑定", states.First(state => state.Kind == StatusKind.Ready).Ms >= openDelay + Math.Max(editDelay, saveDelay) - 50, null);
                    if (staleScan)
                    {
                        var afterRequest = states.SkipWhile(state => state.Text != "正在打开重命名框…").ToArray();
                        check(name + "：旧扫描被丢弃且不覆盖处理中状态", delayedScans == 1 && afterRequest.Length > 0 && !afterRequest.Any(state => state.Kind == StatusKind.NeedsAction), new { delayedScans });
                    }
                    if (late)
                    {
                        var errorAt = states.FirstOrDefault(state => state.Kind == StatusKind.Error).Ms;
                        var readyAt = states.FirstOrDefault(state => state.Kind == StatusKind.Ready).Ms;
                        // UIA 是不可强制限时的外部系统调用：到期前开始的核验可以到期后才返回。
                        // 应证明成功来自晚到恢复分支，且没有先渐隐/解锁再由普通扫描补救，而非假定调用必小于三秒。
                        var afterError = states.SkipWhile(state => state.Kind != StatusKind.Error).ToArray();
                        check(name + "：晚到核验返回即结束反馈，不等待旧冷却回调", readyAt > errorAt && main.LateRecoveryCount == 1 && !main.OpenBlocked && !afterError.Any(s => s.Kind == StatusKind.NeedsAction), new { errorAt, readyAt, main.LateRecoveryCount, main.LastLateProbeMilliseconds });
                        await Task.Delay(3300);
                        check(name + "：旧冷却回调不能覆盖成功", main.CurrentStatus?.Kind == StatusKind.Ready && main.CurrentBinding is not null, null);
                    }
                }
                check(name + "：快捷键至多发送一次且已有输入框不重发", mock.ShortcutCount == (existing || ambiguous ? 0 : 1), new { mock.ShortcutCount });
                check(name + "：注入的瞬态故障确实发生", faultCount == transientAfter + transientBefore, new { faultCount });
                evidence.Add(new { name, openDelay, editDelay, saveDelay, transientAfter, transientBefore, budget, completed, shortcuts = mock.ShortcutCount, faultCount, states = states.Select(state => new { ms = state.Ms, kind = state.Kind.ToString(), text = state.Text }).ToArray() });
            }
            finally
            {
                main.Close();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "open-timing.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(true));
            }
        }
        await Case("发送后连续三次控件失效", openDelay: 450, transientAfter: 3);
        await Case("发送前两次控件失效", transientBefore: 2);
        await Case("延迟 2.6 秒打开超过旧上限", openDelay: 2600, repeat: true);
        await Case("编辑框与保存控件分阶段就绪", openDelay: 150, editDelay: 700, saveDelay: 2300);
        await Case("旧扫描与新打开请求重叠", openDelay: 450, staleScan: true);
        await Case("正常八秒等待之外的晚到弹窗", openDelay: 9500, late: true);
        await Case("快捷键无响应", ignore: true, budget: 700);
        await Case("非瞬态权限错误", fatal: true);
        await Case("已打开输入框复用", existing: true);
        await Case("多个编辑框拒绝绑定", ambiguous: true, budget: 700);
        await Case("等待期间关闭工具", openDelay: 900, closePending: true);
    }
}
