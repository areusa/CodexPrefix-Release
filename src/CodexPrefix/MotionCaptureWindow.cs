using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexPrefix;

// 真正的产品小条和独立宿主；控制面板没有改名业务入口。
internal sealed class MotionCaptureWindow : Window
{
    readonly MockWindow fixture;
    readonly MainWindow panel;
    readonly TextBlock status;
    readonly Button start, finish;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    MotionCapture? capture;
    DateTime deadline;
    int session;
    bool closing;
    internal MotionCaptureWindow()
    {
        Title = "鼠标动效诊断 · 采样面板 0.5.4"; Width = 530; Height = 310;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");
        Foreground = System.Windows.Media.Brushes.Gainsboro;
        Left = 700; Top = 430; WindowStartupLocation = WindowStartupLocation.Manual;
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "独立诊断夹具：连续往返采样", FontSize = 18, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = "点击开始后，在测试小条上不按鼠标键连续来回移动。先在中部晃动，再掠过四边；出现停顿时记住大约秒数。60 秒自动导出，也可提前结束。测试不修改真实 Codex 会话。", TextWrapping = TextWrapping.Wrap });
        status = new TextBlock { Text = "正在准备真实小条…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 10) }; root.Children.Add(status);
        start = new Button { Content = "开始 60 秒采样", IsEnabled = false, Margin = new Thickness(0, 0, 0, 8) }; root.Children.Add(start);
        finish = new Button { Content = "结束并导出", IsEnabled = false }; root.Children.Add(finish); Content = root;
        MainWindow.TestDataRoot = Path.Combine(AppContext.BaseDirectory, "fixture-data", Guid.NewGuid().ToString("N"));
        fixture = new MockWindow { Title = "鼠标动效诊断 · 测试宿主", Width = 760, Left = 160, Top = 230 };
        fixture.Show();
        panel = new MainWindow(fixture) { Title = "鼠标动效诊断 · 测试小条", WindowStartupLocation = WindowStartupLocation.Manual, Left = 200, Top = 390 };
        panel.Show();
        Loaded += async (_, _) =>
        {
            await Task.Delay(500); fixture.Activate(); panel.SetCompact(true); await Task.Delay(1700);
            status.Text = "已就绪。测试小条位于独立测试宿主的顶边。开始后仅移动光标，不点按按钮。"; start.IsEnabled = true;
        };
        start.Click += (_, _) =>
        {
            if (panel.ActiveBar is not { } bar) return;
            capture = new MotionCapture(); bar.Motion.Capture = capture; session++;
            deadline = DateTime.Now.AddSeconds(60); start.IsEnabled = false; finish.IsEnabled = true;
            fixture.Activate(); timer.Start(); status.Text = "采样中：60 秒。请在测试小条上连续往返。";
        };
        finish.Click += async (_, _) => await Finish();
        timer.Tick += async (_, _) =>
        {
            if (DateTime.Now >= deadline) { await Finish(); return; }
            status.Text = $"采样中：剩余 {Math.Max(0, (int)(deadline - DateTime.Now).TotalSeconds)} 秒；已记录 {capture?.Count ?? 0} 条。";
        };
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; await Finish(); closing = true; panel.Close(); Close(); Application.Current.Shutdown();
        };
    }
    async Task Finish()
    {
        timer.Stop();
        if (capture is not { Active: true } current) return;
        finish.IsEnabled = false; start.IsEnabled = false;
        var bar = panel.ActiveBar!;
        TrackingNative.GetWindowRect(new WindowInteropHelper(panel).Handle, out var rect);
        var environment = new { scenario = "真实共享宿主；独立测试窗口；活跃期渲染前采样；60Hz 受力时间归一", version = "0.5.4", dpiScale = panel.Panels.Scale,
            hwndWidthPixels = rect.Right - rect.Left, hwndHeightPixels = rect.Bottom - rect.Top,
            visibleWidthDip = bar.DesignWidth, visibleHeightDip = CompactBar.BarHeight, session,
            automaticScan = "保持产品默认扫描与追踪，避免隐藏调度负担" };
        status.Text = "采样已停止，正在后台导出…";
        string folder = Path.Combine(AppContext.BaseDirectory, "captures", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + session);
        try { await current.Finish(folder, environment); status.Text = "已导出：./captures/" + Path.GetFileName(folder) + "。可再次采样；关闭面板会一并关闭本次夹具。"; }
        catch (Exception ex) { status.Text = "导出失败：" + ex.Message; }
        bar.Motion.Capture = null; start.IsEnabled = true;
    }
}
