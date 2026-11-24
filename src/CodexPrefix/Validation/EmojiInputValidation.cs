using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexPrefix;

// 验证实际控件的可见范围、命中与草稿行为，不用外框高度替代按钮验收。
internal static class EmojiInputValidation
{
    internal static async Task<int> Run()
    {
        var rows = new List<object>(); bool failed = false;
        void Check(string name, bool pass, object? data = null) { rows.Add(new { name, pass, data }); failed |= !pass; }
        var previousRoot = MainWindow.TestDataRoot;
        try
        {
            var samples = EmojiCatalog.Samples.GroupBy(s => { EmojiCatalog.TryIdentity(s, out var key); return key; }).Select(g => g.First()).Take(18).ToArray();
            foreach (int count in new[] { 0, 1, 5, 6, 18 })
            {
                MainWindow.TestDataRoot = Path.Combine(AppContext.BaseDirectory, "fixture-data", Guid.NewGuid().ToString("N"));
                var mock = new MockWindow(); mock.Show();
                var main = new MainWindow(mock) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 200, Top = 250 }; main.Show();
                try
                {
                    await main.PauseDemoScanning(); main.InstallEmojiFixture(samples.Take(count).ToArray()); await Task.Delay(240);
                    main.BeginEmojiEdit(); await Task.Delay(240);
                    if (count == 18)
                    {
                        var work = TrackingNative.WorkArea(new WindowInteropHelper(main).Handle); var old = main.Panels.PhysicalBounds;
                        main.Panels.Snap(new(work.Right - (old.Right - old.Left), work.Bottom - (old.Bottom - old.Top), work.Right, work.Bottom), false);
                        await Task.Delay(150);
                    }
                    byte[] saved = File.ReadAllBytes(main.EmojiConfigurationPath);
                    double height = main.Panels.FullContent.Height; var bounds = main.Panels.PhysicalBounds;
                    var hwnd = new WindowInteropHelper(main).Handle;
                    main.IconPanel.OrderedButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var heights = new List<double>();
                    for (int i = 0; i < 12; i++) { heights.Add(main.Panels.FullContent.Height); await Task.Delay(20); }
                    main.UpdateLayout();
                    Check($"{count} 项：输入浮层脱离列表行及外壳裁切", VisualTreeHelper.GetParent(main.EmojiInputLayer) == main.Panels.Root);
                    if (count == 1)
                    {
                        double originalWidth = main.Panels.FullContent.Width;
                        main.Panels.FullContent.Width = 280; await Task.Delay(50); main.UpdateLayout();
                        Check("输入浮层宽度独立于主内容宽度，仅跟随对齐锚点", Math.Abs(main.EmojiInputLayer.ActualWidth - 352) < .01);
                        main.Panels.FullContent.Width = originalWidth; await Task.Delay(50);
                    }
                    Check($"{count} 项：打开输入不撑高或移动主面板", heights.All(h => Math.Abs(h - height) < .01) && main.Panels.PhysicalBounds == bounds, new { height, heights });
                    Check($"{count} 项：取消按钮完整可见且各点命中", Hit(main, main.EmojiInputCancel), Geometry(main, main.EmojiInputCancel));
                    main.EmojiInputText.Text = "这不是 emoji"; await Task.Delay(30);
                    Check($"{count} 项：无效输入禁用添加，取消仍可点", !main.EmojiInputConfirm.IsEnabled && Hit(main, main.EmojiInputCancel));
                    main.EmojiInputText.Text = "👩‍💻"; await Task.Delay(30);
                    Check($"{count} 项：合法输入的添加按钮完整可见且各点命中", main.EmojiInputConfirm.IsEnabled && Hit(main, main.EmojiInputConfirm), Geometry(main, main.EmojiInputConfirm));
                    Check($"{count} 项：浮层使用同一 HWND，底层列表和保存入口隔离", PresentationSource.FromVisual(main.EmojiInputLayer) == PresentationSource.FromVisual(main) && new WindowInteropHelper(main).Handle == hwnd && !main.IconPanel.IsEnabled && !main.ClearButton.IsEnabled);
                    if (count is 5 or 18)
                    {
                        int moves = main.Panels.NativeMoveCount;
                        if (count == 5)
                        {
                            // 只缩小自有夹具的透明宿主，验证没有预留整屏空间时仍能显示长提示。
                            var physical = main.Panels.PhysicalBounds; double scale = main.Panels.Scale;
                            int px = (int)Math.Round(CompactBar.MotionPadX * scale), py = (int)Math.Round(CompactBar.MotionPadY * scale);
                            TrackingNative.SetWindowPos(hwnd, 0, physical.Left - px, physical.Top - py, physical.Right - physical.Left + px * 2, physical.Bottom - physical.Top + py * 2, 0x14);
                            main.Panels.Apply(LivePanelHost.Dip(physical, scale), 1, 0, 14, Transform.Identity, true);
                        }
                        main.EmojiInputHint.Text = string.Concat(Enumerable.Repeat("本机字形预览说明需要完整阅读；长提示只在提示区域滚动。", 30));
                        await Task.Delay(220); main.UpdateLayout();
                        Check($"{count} 项：长提示不挤掉确认和取消按钮", Hit(main, main.EmojiInputConfirm) && Hit(main, main.EmojiInputCancel));
                        if (count == 5) Check("透明空间不足时扩展同一 HWND，主面板尺寸不变", main.Panels.NativeMoveCount > moves && new WindowInteropHelper(main).Handle == hwnd && Math.Abs(main.Panels.FullContent.Height - height) < .01 && InWorkArea(main));
                        Export(main, $"emoji-input-{count}-long-hint.png");
                        main.EmojiInputText.Text = "🎨"; await Task.Delay(30);
                    }
                    Check($"{count} 项：完整浮层位于屏幕工作区", InWorkArea(main), Geometry(main, main.EmojiInputLayer));
                    Export(main, $"emoji-input-{count}.png");
                    main.EmojiInputCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(240);
                    Check($"{count} 项：取消只关闭输入并恢复列表焦点，配置不变", !main.EmojiInputLayer.IsVisible && main.EditingEmoji && main.IconPanel.IsEnabled && main.ClearButton.IsEnabled && !main.EmojiInputLayer.IsKeyboardFocusWithin && saved.SequenceEqual(File.ReadAllBytes(main.EmojiConfigurationPath)));
                    main.IconPanel.OrderedButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200);
                    main.EmojiInputText.Text = "👩‍💻"; await Task.Delay(30);
                    main.EmojiInputConfirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(240);
                    Check($"{count} 项：添加只更新草稿，勾号确认后才写独立配置", main.ActiveEmojiDraft!.Items.Any(i => i.Item.Token == "👩‍💻") && saved.SequenceEqual(File.ReadAllBytes(main.EmojiConfigurationPath)));
                    main.SaveEmojiEdit(); await Task.Delay(240);
                    var committed = File.ReadAllBytes(main.EmojiConfigurationPath);
                    var reloaded = new EmojiConfigurationStore(Path.GetDirectoryName(main.EmojiConfigurationPath)!).Load();
                    Check($"{count} 项：新存储实例读取已保存 token、顺序与历史", !main.EditingEmoji && reloaded.Items.SequenceEqual(main.EmojiConfiguration.Items) && reloaded.History.SequenceEqual(main.EmojiConfiguration.History) && committed.SequenceEqual(File.ReadAllBytes(main.EmojiConfigurationPath)));
                }
                finally { main.Close(); }
            }
            MainWindow.TestDataRoot = Path.Combine(AppContext.BaseDirectory, "fixture-data", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(MainWindow.TestDataRoot);
            string invalidPath = Path.Combine(MainWindow.TestDataRoot, "emoji-settings.json");
            File.WriteAllText(invalidPath, "{\"schemaVersion\":999}", new UTF8Encoding(true));
            byte[] invalidOriginal = File.ReadAllBytes(invalidPath);
            var recoveryMock = new MockWindow(); recoveryMock.Show(); var recovery = new MainWindow(recoveryMock); recovery.Show();
            try
            {
                await recovery.PauseDemoScanning(); recovery.BeginEmojiEdit(); await Task.Delay(240);
                Check("配置恢复输入层的确认与取消均可见可点", Hit(recovery, recovery.EmojiInputCancel) && Hit(recovery, recovery.EmojiInputConfirm));
                recovery.EmojiInputCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(240); recovery.CancelEmojiEdit();
                Check("取消恢复不覆盖未知版本配置", invalidOriginal.SequenceEqual(File.ReadAllBytes(invalidPath)));
            }
            finally { recovery.Close(); }
            string upgradeFixture = Path.Combine(AppContext.BaseDirectory, "emoji-settings-upgrade-fixture.json");
            if (File.Exists(upgradeFixture))
            {
                MainWindow.TestDataRoot = Path.Combine(AppContext.BaseDirectory, "fixture-data", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(MainWindow.TestDataRoot);
                string sharedPath = Path.Combine(MainWindow.TestDataRoot, "emoji-settings.json");
                File.Copy(upgradeFixture, sharedPath); byte[] before = File.ReadAllBytes(sharedPath);
                var expected = new EmojiConfigurationStore(MainWindow.TestDataRoot).Load();
                for (int launch = 1; launch <= 2; launch++)
                {
                    var startupMock = new MockWindow(); startupMock.Show(); var startup = new MainWindow(startupMock); startup.Show();
                    try
                    {
                        await startup.PauseDemoScanning(); await Task.Delay(240);
                        Check($"用户配置副本第 {launch} 次启动：列表顺序、原始 token 与历史不重置", startup.EmojiConfiguration.Items.SequenceEqual(expected.Items) && startup.EmojiConfiguration.History.SequenceEqual(expected.History) && startup.EmojiConfiguration.Revision == expected.Revision && startup.IconPanel.OrderedButtons.Count == expected.Items.Count, new { items = expected.Items.Count, revision = expected.Revision });
                    }
                    finally { startup.Close(); }
                }
                Check("读取用户配置副本不重写或归一化原文件", before.SequenceEqual(File.ReadAllBytes(sharedPath)));
            }
        }
        catch (Exception ex) { failed = true; rows.Add(new { name = "输入浮层验收异常", pass = false, detail = ex.ToString() }); }
        finally { MainWindow.TestDataRoot = previousRoot; }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "emoji-input-validation.json"), JsonSerializer.Serialize(new { passed = !failed, results = rows, scope = "独立 WPF 夹具的布局、命中、草稿与隔离存储；位图不是连续 DWM 首帧证据" }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(true));
        return failed ? 1 : 0;
    }
    static bool Hit(MainWindow main, Button button)
    {
        if (!button.IsVisible || !button.IsEnabled || button.ActualWidth < 1 || button.ActualHeight < 1) return false;
        var p = button.TranslatePoint(new Point(), main.EmojiInputLayer);
        if (!new Rect(main.EmojiInputLayer.RenderSize).Contains(new Rect(p, button.RenderSize))) return false;
        // 按钮圆角的透明尖角本来就不参与命中；检查四条边的内侧与中心。
        foreach (var point in new[] { new Point(2, button.ActualHeight / 2), new Point(button.ActualWidth - 2, button.ActualHeight / 2), new Point(button.ActualWidth / 2, 2), new Point(button.ActualWidth / 2, button.ActualHeight - 2), new Point(button.ActualWidth / 2, button.ActualHeight / 2) })
        {
            DependencyObject? hit = main.InputHitTest(button.TranslatePoint(point, main)) as DependencyObject;
            while (hit is not null && hit != button) hit = VisualTreeHelper.GetParent(hit);
            if (hit != button) return false;
        }
        return true;
    }
    static object Geometry(MainWindow main, FrameworkElement element) => new { bounds = element.TransformToAncestor(main.Panels.Root).TransformBounds(new Rect(element.RenderSize)), layer = main.EmojiInputLayer.RenderSize, scale = main.Panels.Scale };
    static bool InWorkArea(MainWindow main)
    {
        var work = TrackingNative.WorkArea(new WindowInteropHelper(main).Handle);
        var a = main.EmojiInputLayer.PointToScreen(new Point()); var b = main.EmojiInputLayer.PointToScreen(new Point(main.EmojiInputLayer.ActualWidth, main.EmojiInputLayer.ActualHeight));
        return a.X >= work.Left - 1 && a.Y >= work.Top - 1 && b.X <= work.Right + 1 && b.Y <= work.Bottom + 1;
    }
    static void Export(MainWindow main, string name)
    {
        var root = main.Panels.Root; var layer = main.EmojiInputLayer;
        var rect = main.Panels.Shell.TransformToAncestor(root).TransformBounds(new Rect(main.Panels.Shell.RenderSize));
        rect.Union(layer.TransformToAncestor(root).TransformBounds(new Rect(layer.RenderSize))); rect.Inflate(8, 8);
        var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) { dc.PushTransform(new TranslateTransform(-rect.Left, -rect.Top)); dc.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(root.RenderSize), Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(root.RenderSize)); }
        var image = new RenderTargetBitmap((int)Math.Ceiling(rect.Width * 1.5), (int)Math.Ceiling(rect.Height * 1.5), 144, 144, PixelFormats.Pbgra32); image.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(file);
    }
}
