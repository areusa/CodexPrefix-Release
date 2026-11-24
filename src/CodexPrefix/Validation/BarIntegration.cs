using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexPrefix;

// 仅自动操作本程序创建的测试窗口；真实 Codex 只做安装身份与窗口几何的只读审计。
internal static class BarIntegration
{
    internal static async void Host(string pipeName)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var a = new Window { Title = "小条自动测试宿主 A（非 Codex）", Width = 720, Height = 480, Left = 100, Top = 180 };
        var b = new Window { Title = "小条自动测试宿主 B（非 Codex）", Width = 680, Height = 460, Left = 250, Top = 220 };
        a.Show(); b.Show();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        await writer.WriteLineAsync($"{new WindowInteropHelper(a).Handle.ToInt64()} {new WindowInteropHelper(b).Handle.ToInt64()}");
        try
        {
            while (await reader.ReadLineAsync() is { } command)
            {
                switch (command)
                {
                    case "min": a.WindowState = WindowState.Minimized; break;
                    case "restore": a.WindowState = WindowState.Normal; a.Show(); break;
                    case "hide": a.Hide(); break;
                    case "show": a.Show(); break;
                    case "focusA": a.Activate(); break;
                    case "focusB": b.Activate(); break;
                    case "closeA": a.Close(); break;
                    case "closeB": b.Close(); break;
                    case "exit": Application.Current.Shutdown(); return;
                }
                await writer.WriteLineAsync("ok");
            }
        }
        finally { Application.Current.Shutdown(); }
    }

    internal static async Task<int> Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var rows = new List<object>();
        var failed = false;
        void Check(string name, bool pass, object? data = null)
        {
            rows.Add(new { name, pass, data }); failed |= !pass;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "bar-progress.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(true));
        }
        MainWindow? main = null;
        MockWindow? mock = null;
        Window? outside = null;
        CompactBar? independent = null;
        WindowTracker? cross = null;
        Process? host = null;
        try
        {
            var real = WindowTracker.Candidates(hwnd => WindowIdentity.Read(hwnd));
            rows.Add(new { name = "真实 Codex 只读窗口审计", candidates = real.Select(hwnd => new { id = hwnd.ToInt64(), dpi = TrackingNative.GetDpiForWindow(hwnd), bounds = TrackingNative.Bounds(hwnd) }).ToArray(), note = "未操作真实 Codex 界面或读取会话内容" });
            mock = new MockWindow(dense: true); mock.Show();
            main = new MainWindow(mock); main.Show();
            Check("完整窗口使用透明分层与共享真实外壳", Rounded(new WindowInteropHelper(main).Handle) && main.Panels.Shell is SmoothBorder);
            bool pin = main.Topmost;
            main.SetCompact(true);
            mock.Activate();
            await Task.Delay(1700);
            var bar = main.ActiveBar!; var tracker = main.ActiveTracker!;
            var handle = bar.Handle;
            var target = new WindowInteropHelper(mock).Handle;
            tracker.Update();
            Check("完整模式在同一 HWND 内切换为一行小条", main.IsVisible && main.IsCompact && bar.IsVisible && bar.Frame.ActualHeight == 40 && bar.Handle == new WindowInteropHelper(main).Handle && main.Opacity == 1);
            Check("绑定到独立夹具并收起拖动区", tracker.Target?.Handle == target && bar.Attached && bar.DesignWidth == CompactBar.AttachedWidth);
            Check("小条所有按钮尺寸、间距、外边距统一", CompactLayoutValidation.Balanced(bar));
            Check("透明分层窗口移除二值硬裁切，四角由抗锯齿透明轮廓决定", Rounded(handle));
            Check("外框固定深灰不随状态变黄", ((SolidColorBrush)bar.MotionSurface.BorderBrush).Color == Color.FromRgb(52, 52, 58));
            Check("事件监听注册成功", tracker.HooksAvailable);
            Check("条窗非全局置顶且不激活", !bar.Topmost && (TrackingNative.GetWindowLongPtrW(handle, -20).ToInt64() & 0x08000008) == 0x08000000);
            Check("原生 owner 为目标而不是改宿主 parent", TrackingNative.GetWindow(handle, 4) == target);
            Check("鼠标激活消息不夺焦点", SendMessageW(handle, 0x21, target, 0) == 3);
            Check("未打开输入框时前缀不可用", !bar.ActionsEnabled);
            mock.Left += 137; mock.Top += 33; mock.Width += 83;
            await Task.Delay(350);
            var hostRect = TrackingNative.Bounds(target); var barRect = bar.VisualBounds;
            Check("移动及缩放后顶边居中", Math.Abs(barRect.Left + barRect.Right - hostRect.Left - hostRect.Right) <= 2 && barRect.Top >= hostRect.Top, new { hostRect, barRect });

            var monitors = new List<TrackingNative.Rect>();
            MonitorProc monitorProc = (nint monitor, nint dc, ref TrackingNative.Rect rect, nint data) => { monitors.Add(rect); return true; };
            EnumDisplayMonitors(0, 0, monitorProc, 0);
            foreach (var monitor in monitors)
            {
                TrackingNative.SetWindowPos(target, 0, monitor.Left + 60, monitor.Top + 100, 740, 500, 0x14);
                await Task.Delay(450);
                tracker.Update(); await Task.Delay(100);
                hostRect = TrackingNative.Bounds(target); barRect = bar.VisualBounds;
                var dpi = TrackingNative.GetDpiForWindow(target);
                Check("实际显示器居中与 DPI 宽度", Math.Abs(barRect.Left + barRect.Right - hostRect.Left - hostRect.Right) <= 2 && Math.Abs((barRect.Right - barRect.Left) - CompactBar.AttachedWidth * dpi / 96d) <= 1, new { dpi, hostRect, barRect });
                Check("跨 DPI 后仍为透明分层窗口且没有 GDI 硬裁切", Rounded(handle));
                Check("跨 DPI 后按钮和边距保持规范（允许一物理像素取整）", CompactLayoutValidation.Balanced(bar), new { dpi });
            }
            GC.KeepAlive(monitorProc);
            mock.WindowState = WindowState.Maximized;
            await Task.Delay(350);
            barRect = bar.VisualBounds; var area = TrackingNative.WorkArea(target);
            Check("最大化时小条不会跑到屏幕外", barRect.Top >= area.Top && barRect.Bottom <= area.Bottom);
            mock.WindowState = WindowState.Minimized; await Task.Delay(350);
            Check("最小化收起但保留追踪", !bar.IsVisible && tracker.Target?.Handle == target);
            mock.WindowState = WindowState.Normal; mock.Activate(); await Task.Delay(400);
            Check("恢复后自动重新显示", bar.IsVisible && bar.Attached);
            mock.Hide(); await Task.Delay(350);
            Check("宿主隐藏时显式隐藏小条", !bar.IsVisible && tracker.Target is not null);
            mock.Show(); mock.Activate(); await Task.Delay(350);

            outside = new Window { Title = "其他应用遮挡测试（非 Codex）", Width = 740, Height = 500, Left = mock.Left, Top = mock.Top };
            outside.Show(); outside.Activate(); await Task.Delay(350);
            var outsider = new WindowInteropHelper(outside).Handle;
            Check("切到其他窗口时禁用改名", !bar.ActionsEnabled && !tracker.CanOperate);
            Check("普通窗口可覆盖小条", Above(outsider, handle) && Above(outsider, target));
            outside.Close(); outside = null; mock.Activate(); await Task.Delay(200);
            var content = (StackPanel)mock.Content;
            content.Children.OfType<Button>().First(button => (string)button.Content == "重命名所选").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => bar.ActionsEnabled, 8000);
            Check("手动打开测试输入框后小条启用", bar.ActionsEnabled, new { foreground = Native.GetForegroundWindow().ToInt64(), canOperate = tracker.CanOperate });
            var before = Native.GetForegroundWindow();
            Descendants<Button>(bar.Frame).First(button => Equals(button.Tag, "💭")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            main.SetCompact(false);
            Check("写入期间禁止切换模式", main.ActiveBar is not null);
            await WaitUntil(() => ((ListBoxItem)Descendants<ListBox>(mock).Single().Items[0]).Content as string == "💭 同名会话", 8000);
            var list = Descendants<ListBox>(mock).Single();
            Check("小条提交只改变目标夹具会话", (string)((ListBoxItem)list.Items[0]).Content == "💭 同名会话" && (string)((ListBoxItem)list.Items[1]).Content == "同名会话");
            rows.Add(new { name = "首次保存后的事务状态", note = "列表文本变化早于后台保存确认，后续打开不能抢跑", working = main.IsWorking });
            await WaitUntil(() => !main.IsWorking, 15000);
            Check("首次保存确认结束后才开始下一次打开", !main.IsWorking);
            rows.Add(new { name = "程序化按钮回归", note = "不是物理鼠标点击；真实鼠标焦点保持留待集中桌面验收", before = before.ToInt64() });
            await Task.Delay(300);
            ((ListBoxItem)list.Items[0]).Content = "同名会话";
            list.SelectedIndex = 1;
            mock.Activate(); await Task.Delay(100);
            await main.RequestOpenRename();
            Check("小条入口通过原生快捷键打开当前聊天的重命名框", bar.ActionsEnabled && Application.Current.Windows.OfType<Window>().Any(w => w.Title == "重命名"));
            Check("快捷键打开不依赖数百装饰节点的侧边栏遍历", list.Items.Count == 32 && bar.ActionsEnabled);
            Descendants<Button>(bar.Frame).First(button => Equals(button.Tag, "📄")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => (string)((ListBoxItem)list.Items[1]).Content == "📄 同名会话", 5000);
            Check("两条同名时只打开并修改第二条选中项", (string)((ListBoxItem)list.Items[0]).Content == "同名会话" && (string)((ListBoxItem)list.Items[1]).Content == "📄 同名会话");
            // 原生标题变化早于保存确认结束；不能绕过尚禁用的按钮提前调用下一次操作。
            await WaitUntil(() => !main.IsWorking, 15000);
            Check("前一次原生保存确认结束后再开始打开测试", !main.IsWorking);
            list.SelectedIndex = -1;
            var cooling = main.RequestOpenRename();
            // 密集结构下 UIA 单次调用可能超出调度限时；等到真实反馈出现再断言冷却行为。
            await WaitUntil(() => main.OpenBlocked || cooling.IsCompleted, 20000);
            Check("快捷键没有打开输入框时出现强提示且禁用入口", main.OpenBlocked && !bar.OpenButton.IsEnabled);
            if (!main.OpenBlocked) throw new InvalidOperationException("未进入受阻状态，不能继续以冷却断言代替真实结果。");
            var cooldownClock = Stopwatch.StartNew();
            await main.RequestOpenRename();
            Check("受阻期间重复调用不触发新操作", cooldownClock.ElapsedMilliseconds < 100 && main.OpenBlocked);
            Check("受阻反馈期间还原按钮仍可点击", bar.RestoreButton.IsEnabled);
            Check("受阻入口与其他按钮共用相同几何", CompactLayoutValidation.Balanced(bar) && bar.OpenButton.Opacity == 1);
            // 此正向断言要求目标处于前台。等待系统调用期间用户切走时，产品应隐藏说明；
            // 第三应用隐藏规则在后文及 FeedbackValidation 单独覆盖，不把它误判成展示失败。
            mock.Activate(); tracker.Update(); await Task.Delay(50);
            Check("说明正向验收前目标窗口处于前台", tracker.CanOperate, new { foreground = Native.GetForegroundWindow().ToInt64(), target = target.ToInt64() });
            Check("受阻时下方说明可见且包含手动操作指引", bar.TipVisible && bar.TipText.Contains("手动右键") && bar.TipText.Contains("右侧可展开"));
            var tipHandle = new WindowInteropHelper(bar.FeedbackTip!).Handle;
            Check("说明浮层同样使用透明分层与平滑底框", Rounded(tipHandle) && bar.FeedbackTip!.Content is SmoothBorder);
            Check("说明浮层不全局置顶且不夺焦点", !bar.FeedbackTip!.Topmost && (TrackingNative.GetWindowLongPtrW(tipHandle, -20).ToInt64() & 0x08000008) == 0x08000000);
            var oldTip = bar.FeedbackTip;
            main.SetCompact(false);
            Check("冷却中可以立即还原完整模式且说明不残留", main.IsVisible && main.ActiveBar is null && !oldTip.IsVisible && main.OpenBlocked);
            main.SetCompact(true); bar = main.ActiveBar!; tracker = main.ActiveTracker!;
            mock.Activate(); tracker.Update();
            Check("冷却中重新进入小条仍有提示且仅重试入口禁用", bar.RestoreButton.IsEnabled && bar.TipVisible && !bar.OpenButton.IsEnabled);
            outside = new Window { Title = "提示前台隔离测试（非 Codex）", Width = 300, Height = 100 };
            outside.Show(); outside.Activate(); tracker.Update();
            Check("切到其他应用时说明浮层隐藏", !bar.TipVisible);
            outside.Close(); outside = null; mock.Activate(); tracker.Update();
            await cooling;
            Check("三秒后渐隐并恢复可点击", cooldownClock.ElapsedMilliseconds >= 2900 && !main.OpenBlocked && bar.OpenButton.IsEnabled);
            Check("冷却结束后说明浮层收起", !bar.TipVisible);
            main.SetCompact(false);
            Check("切回完整模式恢复原置顶偏好", main.IsVisible && main.ActiveBar is null && main.Topmost == pin);
            list.SelectedIndex = 0; mock.Activate();
            await main.RequestOpenRename();
            Check("完整模式的独立入口可以打开重命名框", Application.Current.Windows.OfType<Window>().Any(w => w.Title == "重命名"));
            main.Close(); main = null; mock = null;

            await OpenTimingIntegration.Run(Check);
            await FeedbackValidation.Windows(Check);
            await MotionValidation.Windows(Check);
            await FullEntryValidation.Run(Check);
            await CompactLayoutValidation.ExportPreview();

            string pipeName = "CodexPrefix.BarTest." + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            host = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--bar-native-host " + pipeName) { UseShellExecute = false, CreateNoWindow = true })!;
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            string greeting = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? "";
            var ids = greeting.Split(' ').Select(long.Parse).Select(v => (nint)v).ToArray();
            bool foregroundPermission = false;
            async Task Command(string command)
            {
                // 仅给本测试新建的子进程转交一次前台权限，避免 Windows 前台锁使 Activate 返回失败。
                if (command.StartsWith("focus", StringComparison.Ordinal)) foregroundPermission = AllowSetForegroundWindow((uint)host.Id);
                await writer.WriteLineAsync(command);
                await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(4));
                await Task.Delay(300);
            }
            independent = new CompactBar(_ => Task.CompletedTask, () => { }, () => { });
            outside = new Window { Title = "多窗口歧义检查（非 Codex）", Width = 400, Height = 200 };
            outside.Show(); outside.Activate(); await Task.Delay(100);
            bool canSwitch = true;
            cross = new WindowTracker(independent, () => canSwitch, () => { }, testIdentity: hwnd => ids.Contains(hwnd) ? WindowIdentity.Read(hwnd, false) : null);
            Check("无激活历史时多个候选不得随意绑定", cross.Target is null && !independent.Attached);
            await Command("focusA"); cross.Update();
            var independentHandle = new WindowInteropHelper(independent).Handle;
            Check("跨进程追踪并设置从属关系", cross.Target?.Handle == ids[0] && TrackingNative.GetWindow(independentHandle, 4) == ids[0] && cross.Target.ProcessId != Environment.ProcessId, new { foreground = Native.GetForegroundWindow().ToInt64(), expected = ids[0].ToInt64(), foregroundPermission });
            canSwitch = false;
            await Command("focusB"); cross.Update();
            Check("事务进行中不切换追踪目标", cross.Target?.Handle == ids[0]);
            canSwitch = true; cross.Update();
            Check("前台宿主变化后换绑", cross.Target?.Handle == ids[1] && TrackingNative.GetWindow(independentHandle, 4) == ids[1]);
            await Command("closeB"); await WaitUntil(() => cross.Target?.Handle == ids[0], 3500);
            Check("跨进程宿主正常关闭后可转向唯一候选", Native.IsWindow(independentHandle) && cross.Target?.Handle == ids[0] && TrackingNative.GetWindow(independentHandle, 4) == ids[0]);
            host.Kill(); await host.WaitForExitAsync(); await Task.Delay(500); cross.Update();
            Check("跨进程退出后小条存活并展开拖动区", cross.Target is null && TrackingNative.GetWindow(independentHandle, 4) == 0 && !independent.Attached && independent.IsVisible && independent.DesignWidth == CompactBar.LostWidth);
        }
        catch (Exception ex) { failed = true; rows.Add(new { name = "自动测试异常", pass = false, detail = ex.ToString() }); }
        finally
        {
            cross?.Dispose(); independent?.Close(); outside?.Close(); main?.Close(); mock?.Close();
            if (host is not null) { if (!host.HasExited) host.Kill(); host.Dispose(); }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "bar-integration.json"), JsonSerializer.Serialize(new { passed = !failed, results = rows }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(true));
        }
        return failed ? 1 : 0;
    }
    static async Task WaitUntil(Func<bool> test, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!test() && clock.ElapsedMilliseconds < timeout) await Task.Delay(100);
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    static bool Above(nint first, nint second)
    {
        var list = new List<nint>(); TrackingNative.EnumWindows((hwnd, _) => { list.Add(hwnd); return true; }, 0);
        return list.Contains(first) && list.Contains(second) && list.IndexOf(first) < list.IndexOf(second);
    }
    static bool Rounded(nint window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            return (TrackingNative.GetWindowLongPtrW(window, -20).ToInt64() & 0x80000) != 0
                && GetWindowRgn(window, region) <= 0
                && System.Windows.Interop.HwndSource.FromHwnd(window)?.RootVisual is Window { AllowsTransparency: true };
        }
        finally { DeleteObject(region); }
    }
    [DllImport("gdi32.dll")] static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint region);
    [DllImport("user32.dll")] static extern int GetWindowRgn(nint hwnd, nint region);
    delegate bool MonitorProc(nint monitor, nint dc, ref TrackingNative.Rect rect, nint data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint data);
    [DllImport("user32.dll")] static extern nint SendMessageW(nint hwnd, uint message, nint wp, nint lp);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint processId);
}
