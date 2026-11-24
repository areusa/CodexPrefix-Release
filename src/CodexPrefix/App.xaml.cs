using System.IO;
using System.Text;
using System.Windows;

namespace CodexPrefix;

public partial class App : Application
{
    Mutex? singleInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--apply-update")) { ShutdownMode = ShutdownMode.OnExplicitShutdown; Shutdown(UpdateApplier.Run(e.Args)); return; }
        if (e.Args.FirstOrDefault() == "--automation-worker")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(e.Args.Length >= 3 && int.TryParse(e.Args[2], out var parentId)
                ? AutomationWorkerHost.Run(e.Args[1], parentId, e.Args.Contains("--automation-fixture")) : 2);
            return;
        }
        if (e.Args.Contains("--automation-worker-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(async () => Shutdown(await AutomationWorkerValidation.Run()));
            return;
        }
        if (e.Args.Contains("--motion-record"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var recorder = new MotionCaptureWindow(); MainWindow = recorder; recorder.Show(); return;
        }
        if (e.Args.Contains("--emoji-input-test"))
        {
            CodexPrefix.MainWindow.TestDataRoot = Path.Combine(AppContext.BaseDirectory, "fixture-data", Guid.NewGuid().ToString("N"));
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(async () => Shutdown(await EmojiInputValidation.Run()));
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            if (e.Args.Contains("--bar-test") || e.Args.Contains("--bar-test-click-start") || e.Args.Contains("--bar-native-host") || e.Args.Contains("--live-test-click-start") || e.Args.Contains("--emoji-test") || e.Args.Contains("--emoji-test-click-start"))
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "bar-test-unhandled.txt"), args.Exception.ToString(), new UTF8Encoding(true));
                args.Handled = true; Shutdown(1); return;
            }
            MessageBox.Show("操作失败：" + args.Exception.Message, "会话前缀");
            args.Handled = true;
        };
        if (e.Args.Contains("--self-test"))
        {
            var result = SelfTests.Run();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), result, new UTF8Encoding(true));
            Shutdown(result.Contains("失败") ? 1 : 0);
            return;
        }
        if (e.Args.Contains("--bar-native-host")) { BarIntegration.Host(e.Args.Last()); return; }
        if (e.Args.Contains("--emoji-review") || e.Args.Contains("--emoji-input-review"))
        {
            CodexPrefix.MainWindow.TestDataRoot = Path.Combine(AppContext.BaseDirectory, "fixture-data", Guid.NewGuid().ToString("N"));
            var fixture = new MockWindow(); fixture.Show(); var panel = new MainWindow(fixture) { Title = "会话前缀 · 新功能夹具" }; MainWindow = panel; panel.Show();
            Dispatcher.BeginInvoke(async () =>
            {
                await panel.PauseDemoScanning();
                panel.InstallEmojiFixture(EmojiCatalog.Samples.GroupBy(s => { EmojiCatalog.TryIdentity(s, out var k); return k; }).Select(g => g.First()).Take(e.Args.Contains("--emoji-input-review") ? 5 : 20).ToArray());
                if (e.Args.Contains("--emoji-input-review")) { panel.BeginEmojiEdit(); await Task.Delay(240); panel.IconPanel.OrderedButtons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); }
                panel.StartReviewDiagnostics();
            });
            return;
        }
        if (e.Args.Contains("--emoji-test") || e.Args.Contains("--emoji-test-click-start"))
        {
            CodexPrefix.MainWindow.TestDataRoot = Path.Combine(AppContext.BaseDirectory, "fixture-data", Guid.NewGuid().ToString("N"));
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (e.Args.Contains("--emoji-test-click-start"))
            {
                var start = new Window { Title = "Emoji 新功能验收 · 点击开始", Width = 360, Height = 130, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                var button = new System.Windows.Controls.Button { Content = "开始新增交互测试", Margin = new Thickness(20) }; start.Content = button; start.Show();
                button.Click += async (_, _) => { start.Hide(); int code = await EmojiIntegration.Run(); start.Close(); Shutdown(code); };
            }
            else Dispatcher.BeginInvoke(async () => Shutdown(await EmojiIntegration.Run()));
            return;
        }
        if (e.Args.Contains("--bar-test") || e.Args.Contains("--bar-test-click-start") || e.Args.Contains("--live-test-click-start"))
        {
            CodexPrefix.MainWindow.TestDataRoot = Path.Combine(AppContext.BaseDirectory, "fixture-data", Guid.NewGuid().ToString("N"));
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (e.Args.Contains("--bar-test-click-start") || e.Args.Contains("--live-test-click-start"))
            {
                // Windows 前台锁会阻止后台进程的 Activate；一次真实点击给测试进程明确的用户前台授权。
                var start = new Window { Title = "小条集中验收 · 点击开始", Width = 360, Height = 130, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                var button = new System.Windows.Controls.Button { Content = "开始自动窗口测试", Margin = new Thickness(20) };
                start.Content = button;
                start.Show();
                button.Click += async (_, _) => { start.Hide(); int code = e.Args.Contains("--live-test-click-start") ? await LiveTransitionValidation.Standalone() : await BarIntegration.Run(); start.Close(); Shutdown(code); };
                return;
            }
            Dispatcher.BeginInvoke(async () => { int code = await BarIntegration.Run(); Shutdown(code); });
            return;
        }
        MockWindow? mock = null;
        var demo = e.Args.Contains("--demo");
        string? customDataDir = null;
        for (int i = 0; i < e.Args.Length - 1; i++) if (e.Args[i] == "--data-dir") customDataDir = Path.GetFullPath(e.Args[i + 1]);
        var customId = customDataDir is null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(customDataDir.ToUpperInvariant())))[..16];
        singleInstance = new Mutex(true, demo ? "Local\\CodexPrefix.Demo.v02" : customDataDir is null ? "Local\\CodexPrefix.Main" : "Local\\CodexPrefix.Custom." + customId, out var first);
        if (!first) { MessageBox.Show("工具已经在运行。", "会话前缀"); Shutdown(); return; }
        if (demo) { mock = new MockWindow(ignoreShortcut: e.Args.Contains("--demo-no-shortcut")); mock.Show(); }
        var updatesRoot = Path.Combine(customDataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPrefix"), "updates");
        string? transaction = UpdateApplier.Argument(e.Args, "--update-transaction");
        if (!demo && transaction is null && Directory.Exists(updatesRoot))
        {
            try
            {
                using var updateLock = new FileStream(Path.Combine(updatesRoot, "apply.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                UpdateApplier.RecoverInterrupted(AppContext.BaseDirectory, updatesRoot);
            }
            catch (Exception ex) { new LocalLog(Path.Combine(updatesRoot, "logs")).Write("启动恢复", ex.Message); Shutdown(3); return; }
        }
        var main = new MainWindow(mock, customDataDir) { DemoLostHost = demo && e.Args.Contains("--demo-lost") };
        MainWindow = main;
        main.StartAssistance();
        if (transaction is not null && UpdateApplier.Argument(e.Args, "--health-token") is { } healthToken)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () => UpdateApplier.ConfirmHealth(transaction, healthToken, updatesRoot));
        if (demo && e.Args.Contains("--demo-lost")) main.SetCompact(true);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
