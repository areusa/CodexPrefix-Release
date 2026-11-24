using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexPrefix;

// 展示规则与真实窗口测试分开；所有输入和窗口仅属于本程序创建的夹具。
internal static class FeedbackValidation
{
    internal static void Rules(Action<bool, string> check)
    {
        var target = new WindowIdentity((nint)10, 100, 1000);
        var origin = new WindowIdentity((nint)20, 200, 2000);
        var third = new WindowIdentity((nint)30, 300, 3000);
        var context = new FeedbackContext(origin, target);
        check(context.Allows(target, origin), "目标未在前台时也应展示本次点击的受阻原因");
        check(!context.Allows(target, third), "提示不能跟随到无关的第三个应用");
        check(context.Allows(target, origin), "未到过目标前返回原场景可继续展示同次反馈");
        check(context.Allows(target, target), "进入同一目标可以继续展示反馈");
        check(!context.Allows(target, origin), "进入目标后再离开不能继续使用原应用作为例外");
        check(!context.Allows(third, third), "换绑到其他宿主不能搬运旧反馈");
        check(!context.Allows(null, target), "丢失已绑定宿主必须隐藏旧反馈");
        check(!context.Allows(target with { Started = 1001 }, target), "复用句柄但创建时间不同也不是旧宿主");
        check(!context.Allows(target, null), "没有前台身份时不展示反馈");
        check(!context.Allows(target, target with { ProcessId = 101 }), "前台句柄复用不能沿用旧身份");
        context.BeginFade(); check(context.Fading, "渐隐阶段必须被显式记录");
        var unbound = new FeedbackContext(origin, null);
        check(unbound.Allows(null, origin), "无目标时仍可解释本次点击为何失败");
        check(unbound.Allows(target, target), "完整模式进入小条时允许固定首次追踪目标");
        check(!unbound.Allows(third, third), "首次固定目标后不能再追随换绑");
    }
    internal static async Task Windows(Action<string, bool, object?> check)
    {
        MockWindow? mock = null; MainWindow? main = null; Window? outside = null; Window? third = null;
        var evidence = new List<object>();
        try
        {
            mock = new MockWindow(ignoreShortcut: true); mock.Show();
            main = new MainWindow(mock); main.Show(); await main.PauseDemoScanning();
            main.SetCompact(true); mock.Activate(); await Task.Delay(150);
            var bar = main.ActiveBar!; var tracker = main.ActiveTracker!;
            outside = new Window { Title = "反馈起始前台（独立夹具）", Width = 300, Height = 100, Left = 850, Top = 200 };
            third = new Window { Title = "反馈切换前台（独立夹具）", Width = 300, Height = 100, Left = 850, Top = 350 };
            outside.Show(); outside.Activate(); await Task.Delay(100); tracker.Update();
            check("反馈回归：确实处于非目标前台且无改名权限", !tracker.CanOperate && Native.BelongsToWindow(Native.GetForegroundWindow(), new WindowInteropHelper(outside).Handle), null);
            async Task RunCase(bool hideDuringFeedback)
            {
                outside.Activate(); await Task.Delay(80); tracker.Update();
                var clock = Stopwatch.StartNew();
                var samples = new List<(long Ms, bool Visible, double Opacity, bool CanOperate)>();
                bool? previous = null;
                var changes = new List<(long Ms, bool Visible)>();
                void Sample()
                {
                    samples.Add((clock.ElapsedMilliseconds, bar.TipVisible, bar.FeedbackTip?.Opacity ?? 1, tracker.CanOperate));
                    if (previous != bar.TipVisible) { previous = bar.TipVisible; changes.Add((clock.ElapsedMilliseconds, bar.TipVisible)); }
                }
                var task = main.RequestOpenRename();
                var timeout = Stopwatch.StartNew();
                while (!main.OpenBlocked && !task.IsCompleted && timeout.ElapsedMilliseconds < 5000) await Task.Delay(10);
                check("反馈回归：非目标前台的真实打开请求进入受阻且提示可见", main.OpenBlocked && bar.TipVisible && !bar.OpenButton.IsEnabled && !bar.ActionsEnabled, new { hideDuringFeedback });
                if (!main.OpenBlocked) throw new InvalidOperationException("没有产生预期受阻，不得用后续断言代替复现。");
                var tip = bar.FeedbackTip!;
                DependencyPropertyChangedEventHandler visible = (_, _) => Sample();
                tip.IsVisibleChanged += visible;
                var sampling = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                sampling.Tick += (_, _) => Sample(); sampling.Start(); Sample();
                bool observedFade = false;
                try
                {
                    await Task.Delay(650); tracker.Update();
                    check("反馈回归：跨过多次轮询后说明仍在且没有授予改名权限", bar.TipVisible && !tracker.CanOperate && !bar.ActionsEnabled && bar.RestoreButton.IsEnabled && bar.CloseButton.IsEnabled, new { hideDuringFeedback });
                    if (hideDuringFeedback)
                    {
                        third.Show(); third.Activate(); await Task.Delay(80); tracker.Update();
                        check("反馈回归：提示中主动切换第三应用会隐藏说明", !bar.TipVisible, null);
                        var wait = Stopwatch.StartNew();
                        while (bar.OpenButton.Opacity >= .99 && !task.IsCompleted && wait.ElapsedMilliseconds < 6000) await Task.Delay(10);
                        observedFade = !task.IsCompleted && bar.OpenButton.Opacity < .99;
                        mock.Activate(); tracker.Update(); Sample();
                        check("反馈回归：隐藏后在渐隐期间回到目标也不能重新弹出", observedFade && !bar.TipVisible, null);
                    }
                    await task.WaitAsync(TimeSpan.FromSeconds(10)); Sample();
                    check("反馈回归：冷却完成后说明关闭、入口恢复", !bar.TipVisible && !main.OpenBlocked && bar.OpenButton.IsEnabled, new { hideDuringFeedback });
                    if (hideDuringFeedback)
                    {
                        int hidden = changes.FindIndex(x => !x.Visible);
                        check("反馈回归：隐藏后直到计时结束没有任何 Show 回跳", hidden >= 0 && changes.Skip(hidden).All(x => !x.Visible), changes.Select(x => new { x.Ms, x.Visible }).ToArray());
                    }
                    else
                    {
                        long hiddenAt = changes.FirstOrDefault(x => !x.Visible).Ms;
                        check("反馈回归：非目标前台提示持续覆盖三秒，末尾正常渐隐", hiddenAt >= 2900 && samples.Any(x => x.Visible && x.Opacity < .95) && changes.Count(x => x.Visible) == 1, new { hiddenAt });
                    }
                    check("反馈回归：前台不符时未发送快捷键或修改标题", mock.ShortcutCount == 0, null);
                }
                finally
                {
                    sampling.Stop(); tip.IsVisibleChanged -= visible;
                    evidence.Add(new { name = hideDuringFeedback ? "切应用隐藏后渐隐不重开" : "非目标前台连续显示", samples = samples.Select(x => new { x.Ms, x.Visible, x.Opacity, x.CanOperate }).ToArray(), changes = changes.Select(x => new { x.Ms, x.Visible }).ToArray(), observedFade });
                }
            }
            await RunCase(false);
            await RunCase(true);
        }
        finally
        {
            third?.Close(); outside?.Close(); main?.Close(); mock?.Close();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "feedback-timing.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(true));
        }
    }
}
