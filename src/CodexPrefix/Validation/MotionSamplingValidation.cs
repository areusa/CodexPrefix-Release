using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace CodexPrefix;

// 只供同程序集隔离测试使用，不注入桌面光标，也没有外部 HWND/轨迹命令入口。
internal sealed class MotionInputFixture
{
    internal double Seconds = 10;
    internal Point? Pointer = new Point(0, 0);
    internal bool Visible = true, ButtonsDown, Animations = true;
    internal int Reads;
}

internal static class MotionSamplingValidation
{
    sealed class Fixture : IDisposable
    {
        internal readonly CompactBar Bar = new(_ => Task.CompletedTask, () => { }, () => { });
        internal readonly MotionInputFixture Input = new();
        internal bool Active = true;
        internal MiniMotionDriver Driver => Bar.Motion;
        internal Rect Bounds => Bar.StableInputBounds;
        internal Fixture(int count = 6)
        {
            Driver.InputFixture = Input; Driver.HostActive = () => Active;
            if (count != 6) Bar.SetItems(EmojiCatalog.Samples.GroupBy(s => { EmojiCatalog.TryIdentity(s, out var key); return key; }).Select(g => new EmojiItem(Guid.NewGuid().ToString("N"), g.First())).Take(count).ToArray());
            Bar.SetAttached(true);
            var root = (FrameworkElement)Bar.Content;
            root.Measure(new Size(Bar.Width, Bar.Height)); root.Arrange(new Rect(0, 0, Bar.Width, Bar.Height)); root.UpdateLayout();
            Driver.Start();
            Input.Seconds = 10.2; Driver.FixtureTimer();
            Input.Seconds += .016; Input.Pointer = new Point(1, 0); Driver.FixtureTimer();
            Input.Seconds += .016; Input.Pointer = new Point(Bounds.Left + 80, Bounds.Top + 20); Driver.FixtureTimer();
        }
        internal void Frame(Point? point = null, double dt = 1d / 60)
        {
            Input.Seconds += dt;
            if (point is not null) Input.Pointer = point;
            Driver.FixtureFrame();
        }
        public void Dispose() { Driver.Dispose(); Bar.Close(); }
    }
    internal static void Rules(Action<bool, string> check)
    {
        var shutdown = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
        var scenarios = new List<object>();
        foreach (int count in new[] { 6, 18 })
        {
            using var f = new Fixture(count);
            for (int i = 0; i < 20; i++) f.Frame();
            var physics = f.Driver.Physics;
            // 离屏夹具只核对既有 hover 意图的保留；真实视觉坐标由原生共享宿主检查。
            physics.Hover.Snap(1.08);
            physics.X.X = 2; physics.X.V = 12;
            double hover = physics.Hover.X;
            int epoch = f.Driver.Epoch;
            bool executed = false; f.Driver.ScheduleAction(() => executed = true); physics.Press(0, f.Input.Seconds);
            f.Bar.EmojiScroll.ScrollBy(48);
            check(!f.Driver.IsGuarded && f.Driver.Physics.Hover.Target == 1.08 && f.Driver.Physics.Hover.X == hover && f.Driver.Physics.X.X == 2 && f.Driver.Physics.X.V == 12,
                $"滚轮（包括无溢出）保留 hover 和整体物理，不进入等待移出的全局中断；数量={count}；保护={f.Driver.IsGuarded}；目标={f.Driver.Physics.Hover.Target}；hover={f.Driver.Physics.Hover.X}/{hover}；X={f.Driver.Physics.X.X}；V={f.Driver.Physics.X.V}；中断={f.Driver.LastInterrupt}");
            check(f.Driver.Epoch > epoch && f.Driver.Physics.Pressed == -1 && f.Driver.Physics.Scale[0].Target == 1,
                "滚轮取消旧按压及动作代次，但不留下缩小目标");
            f.Bar.EmojiScroll.ScrollTo(48, false); f.Bar.UpdateLayout();
            long swipes = f.Driver.SwipeCount;
            for (int i = 0; i < 20; i++) f.Frame();
            check(!executed && f.Driver.SwipeCount == swipes && f.Driver.Physics.Hover.Target == 1.08,
                "滚动或静止光标不会执行旧延迟动作或制造新冲量，hover 不丢失");
            f.Frame(new Point(f.Bounds.Left + 140, f.Bounds.Top + 20));
            check(f.Driver.SwipeCount > swipes, "滚动后无需移出小条，条内真实移动立即恢复受力");
        }
        using (var f = new Fixture(18))
        {
            f.Driver.Physics.Press(0, f.Input.Seconds);
            f.Bar.EmojiScroll.ScrollTo(96, false); f.Bar.UpdateLayout();
            check(f.Driver.Physics.Pressed == -1 && f.Driver.Physics.Scale[0].Target == 1,
                "程序滚动重绑可见项也取消旧按压，不能恢复已释放的缩小目标");
            f.Driver.Interrupt("DPI 重建");
            f.Bar.EmojiScroll.ScrollBy(48);
            check(f.Driver.IsGuarded, "滚轮不能解除宿主坐标重建的真实保护");
            long before = f.Driver.SwipeCount;
            f.Frame(dt: .2);
            f.Frame(new Point(f.Bounds.Left + 120, f.Bounds.Top + 20));
            check(f.Driver.SwipeCount == before, "真实中断的移出条件不能被滚轮绕过");
        }
        foreach (bool inside in new[] { false, true })
        {
            double[]? reference = null;
            foreach (int hz in new[] { 60, 120, 144 })
            {
                var physics = new MiniMotion(1);
                // 相同速度、相同半秒输入且远离限幅；不推进弹簧，直接比较注入的物理冲量。
                for (int i = 1; i <= hz / 2; i++) physics.Swipe(40, 20, 1d / hz, inside, i / (double)hz);
                var impulse = new[] { physics.X.V, physics.Y.V, physics.StretchX.V, physics.StretchY.V, physics.Skew.V };
                if (reference is null) reference = impulse;
                else check(impulse.Zip(reference).All(p => Math.Abs(p.First - p.Second) < 1e-9),
                    "不同刷新率下相同时间输入的平移、形变和剪切冲量一致，内部倍率仍有效");
            }
        }
        foreach (int hz in new[] { 60, 120, 144 })
        {
            using var f = new Fixture();
            var capture = new MotionCapture(); f.Driver.Capture = capture;
            long before = f.Driver.SwipeCount;
            int duplicateInputs = 0;
            for (int i = 1; i <= hz * 6; i++)
            {
                double x = f.Bounds.Left + f.Bounds.Width / 2 + 280 * Math.Sin(i * 2 * Math.PI * 3 / hz);
                f.Frame(new Point(x, f.Bounds.Top + 20), 1d / hz);
                // 模拟定时器延迟到 300ms 才被调度；帧时钟仍应正常采样。
                if (i % Math.Max(1, hz * 3 / 10) == 0) f.Driver.FixtureTimer();
                long sampled = f.Driver.SampleCount, swipes = f.Driver.SwipeCount;
                int reads = f.Input.Reads;
                f.Driver.FixtureTimer(); f.Driver.FixtureFrame();
                if (f.Driver.SampleCount != sampled || f.Driver.SwipeCount != swipes || f.Input.Reads != reads) duplicateInputs++;
            }
            var measured = capture.Measure();
            check(duplicateInputs == 0, "活跃定时器及同一合成时间的重复通知不能二次读取或施力");
            check(measured.Over80Ms == 0 && measured.MaximumSampleIntervalMs <= 1000d / hz + .001,
                "定时器延迟时连续往返仍按每个渲染帧采样，不出现 80ms 丢弃");
            check(measured.Sources.GetValueOrDefault("渲染帧") == hz * 6 && measured.Sources.Count == 1,
                "活跃输入只来自渲染帧，定时器只承担静止唤醒");
            check(f.Driver.SwipeCount - before > hz, "左右反向穿过应持续产生有效受力");
            scenarios.Add(new { name = $"{hz}Hz 合成往返；300ms 定时器延迟", metrics = measured });
        }
        using (var f = new Fixture())
        {
            long before = f.Driver.SwipeCount;
            f.Driver.Physics.X.V = 40;
            for (int i = 0; i < 20; i++) f.Frame();
            check(f.Driver.SwipeCount == before, "面板自己回弹到静止光标下不能产生新划过冲量");
            int reads = f.Input.Reads; long sampled = f.Driver.SampleCount;
            f.Input.Seconds += .016; f.Input.Pointer = new Point(f.Bounds.Left + 120, f.Bounds.Top + 20);
            f.Driver.FixtureFrame();
            check(f.Input.Reads == reads + 1 && f.Driver.SampleCount == sampled + 1, "新渲染帧只读一次最新位置");
            before = f.Driver.SwipeCount; f.Input.ButtonsDown = true;
            for (int i = 0; i < 5; i++) f.Frame(new Point(f.Bounds.Left + 130 + i * 12, f.Bounds.Top + 20));
            check(f.Driver.SwipeCount == before, "按住鼠标键时仍禁止划过施力");
        }
        using (var f = new Fixture())
        {
            bool executed = false; f.Driver.ScheduleAction(() => executed = true);
            int epoch = f.Driver.Epoch;
            f.Active = false; f.Frame(dt: .1);
            check(f.Driver.Epoch > epoch && f.Driver.LastInterrupt == "宿主失焦", "帧内采样发现失焦仍立即中断");
            f.Input.Seconds += .3; f.Driver.FixtureTimer();
            check(!executed && !f.Driver.Physics.Moving, "失焦中断后不能推进旧物理或执行延迟业务动作");
            f.Active = true; long before = f.Driver.SwipeCount;
            f.Input.Pointer = new Point(f.Bounds.Left + 170, f.Bounds.Top + 20); f.Driver.FixtureTimer();
            check(f.Driver.SwipeCount == before, "坐标保护期间条内移动不能绕过移出条件");
            f.Input.Seconds += .016; f.Input.Pointer = new Point(0, 0); f.Driver.FixtureTimer();
            f.Input.Seconds += .016; f.Input.Pointer = new Point(f.Bounds.Left + 90, f.Bounds.Top + 20); f.Driver.FixtureTimer();
            check(f.Driver.SwipeCount > before, "保护结束且实际移出后，定时器能够重新唤醒动效");
        }
        using (var f = new Fixture())
        {
            int epoch = f.Driver.Epoch;
            f.Frame(dt: .3);
            check(f.Driver.Epoch > epoch && f.Driver.LastInterrupt == "异常长帧" && f.Driver.IsGuarded,
                "真正超过 250ms 的长帧仍保留原中断保护，不追赶旧轨迹");
        }
        using (var f = new Fixture())
        {
            long before = f.Driver.SwipeCount;
            f.Driver.Transitioning = true;
            f.Frame(new Point(f.Bounds.Left + 180, f.Bounds.Top + 20)); f.Driver.FixtureTimer();
            check(f.Driver.SwipeCount == before, "模式过渡期间两个采样入口都不能追加划过受力");
        }
        using (var f = new Fixture())
        {
            f.Input.Animations = false; f.Frame(new Point(f.Bounds.Left + 180, f.Bounds.Top + 20));
            check(!f.Driver.Physics.Moving && f.Driver.Physics.X.X == 0, "系统关闭动画时仍清除物理响应");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "motion-sampling-validation.json"), JsonSerializer.Serialize(new
        {
            scope = "真实驱动与内部合成输入；不改变桌面光标，不代替用户实际手势或 DWM 验收", scenarios
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(true));
        }
        finally { Application.Current.ShutdownMode = shutdown; }
    }
    internal static async Task Windows(Action<string, bool, object?> check)
    {
        var mock = new MockWindow(); mock.Show();
        var main = new MainWindow(mock) { WindowStartupLocation = WindowStartupLocation.Manual, Left = mock.Left + 50, Top = mock.Top + 180 };
        main.Show();
        try
        {
            await main.PauseDemoScanning(); mock.Activate(); main.SetCompact(true); await Task.Delay(1800);
            var bar = main.ActiveBar!;
            var driver = bar.Motion;
            // 此项只核验真实 Dispatcher/Rendering 调度，前台权限由其他原生场景单独验证。
            driver.HostActive = () => true;
            driver.Interrupt("独立渲染采样验收"); await Task.Delay(180);
            var capture = new MotionCapture(); driver.Capture = capture;
            driver.Physics.Click(0, driver.ElapsedSeconds);
            driver.ScheduleAction(() => { });
            await Task.Delay(750);
            var measured = capture.Measure();
            check("真实合成调度：活跃期持续获得逐帧位置采样", measured.Frames >= 10 && measured.Sources.GetValueOrDefault("渲染帧") == measured.Frames, measured);
            check("真实合成调度：定时器没有重复施力且样本来源明确", measured.Sources.Keys.All(s => s is "渲染帧" or "静止唤醒"), measured);
            check("真实合成调度：没有 80ms 轨迹丢弃", measured.Over80Ms == 0, measured);
            await capture.Finish(Path.Combine(AppContext.BaseDirectory, "native-motion-sampling"), new
            {
                scope = "真实共享宿主和 WPF 渲染；内部点击激励；无桌面鼠标轨迹注入；不代替实际手势复测",
                version = typeof(MotionSamplingValidation).Assembly.GetName().Version?.ToString(), metrics = measured
            });
            driver.Capture = null;
            driver.Physics.X.X = 2; driver.Physics.X.V = 12;
            bool staleAction = false; driver.ScheduleAction(() => staleAction = true);
            int epoch = driver.Epoch;
            bar.EmojiScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            check("真实共享宿主：滚轮路由不清空整体物理或进入全局保护", !driver.IsGuarded && driver.Physics.X.X == 2 && driver.Physics.X.V == 12 && driver.Epoch > epoch,
                new { driver.LastInterrupt, driver.IsGuarded, x = driver.Physics.X.X, velocity = driver.Physics.X.V });
            await Task.Delay(180);
            check("真实共享宿主：滚轮作废旧延迟业务动作", !staleAction, null);
        }
        finally { main.Close(); }
    }
}
