using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexPrefix;

// 只对本程序的真实视觉树取样；这些位图仅为验收产物，不进入产品转场。
internal static class LiveTransitionValidation
{
    internal static async Task<int> Standalone()
    {
        var rows = new List<object>(); bool failed = false;
        var mock = new MockWindow(); mock.Show();
        var main = new MainWindow(mock) { WindowStartupLocation = WindowStartupLocation.Manual, Left = mock.Left + 50, Top = mock.Top + 180 }; main.Show();
        try
        {
            await main.PauseDemoScanning(); mock.Activate(); await Task.Delay(100);
            await Run(main, mock, (name, pass, data) => { rows.Add(new { name, pass, data }); failed |= !pass; });
        }
        catch (Exception ex) { failed = true; rows.Add(new { name = "真实转场测试异常", pass = false, detail = ex.ToString() }); }
        finally
        {
            main.Close();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "live-transition-validation.json"), JsonSerializer.Serialize(new { passed = !failed, results = rows }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(true));
        }
        return failed ? 1 : 0;
    }
    internal static async Task Run(MainWindow main, MockWindow mock, Action<string, bool, object?> check)
    {
        var panels = main.Panels;
        var shell = panels.Shell; var full = panels.FullContent; var mini = panels.MiniContent;
        nint hwnd = new WindowInteropHelper(main).Handle;
        var rows = new List<object>();
        var frames = new List<(string Label, BitmapSource Image)>();
        foreach (bool toMini in new[] { true, false })
        {
            var before = panels.PhysicalBounds;
            main.SetCompact(toMini);
            var transition = main.LiveTransition;
            check("真实转场：准备运动空间不改变外壳屏幕坐标", panels.PhysicalBounds == before, new { before, after = panels.PhysicalBounds });
            if (transition is null) { check("真实转场：同 DPI 正向路径必须有中途帧", false, main.LastMotionTransition); continue; }
            int count = 0; bool identity = true, liveUpdate = false, earlyContent = false;
            byte[]? endBefore = null, endAfter = null;
            BitmapSource? beforeImage = null, afterImage = null;
            int doneWindowsBefore = 0, doneWindowsAfter = 0;
            var clock = Stopwatch.StartNew();
            var savedText = main.TargetText.Text;
            transition.FrameAdvanced += () =>
            {
                count++;
                identity &= new WindowInteropHelper(main).Handle == hwnd && main.Opacity == 1 && main.IsVisible
                    && ReferenceEquals(main.Panels.Shell, shell) && ReferenceEquals(main.Panels.FullContent, full)
                    && ReferenceEquals(main.Panels.MiniContent, mini) && full.Parent == mini.Parent && shell.Parent == panels.Root;
                earlyContent |= !transition.Motion.Done && transition.Motion.TargetOpacity > .1;
                if (count == 3)
                {
                    main.TargetText.Text = "真实控件：过渡中更新";
                    liveUpdate = full.IsAncestorOf(main.TargetText) && main.TargetText.Text.Contains("过渡中更新");
                }
                if (count is 1 or 10 or 22 or 35)
                {
                    frames.Add(($"{(toMini ? "收起" : "展开")} · 第 {count} 帧", Capture(main)));
                    rows.Add(new { toMini, frame = count, ms = clock.Elapsed.TotalMilliseconds, pose = panels.Pose, full = panels.FullOpacity, mini = panels.MiniOpacity, hwnd = hwnd.ToInt64() });
                }
            };
            transition.Completing += () => { beforeImage = Capture(main); endBefore = Pixels(beforeImage); doneWindowsBefore = VisibleWindows(); };
            transition.Completed += () => { afterImage = Capture(main); endAfter = Pixels(afterImage); doneWindowsAfter = VisibleWindows(); };
            while (main.MotionTransitionActive && clock.ElapsedMilliseconds < 3500) await Task.Delay(15);
            main.TargetText.Text = savedText;
            check("真实转场：每帧保持同一 HWND、同一外壳及原控件实例", identity && count > 5, new { toMini, frames = count });
            check("真实转场：中途实时修改原文字控件，目标内容到位前显示", liveUpdate && earlyContent, new { toMini, liveUpdate, earlyContent });
            bool samePixels = endBefore is not null && endAfter is not null && endBefore.SequenceEqual(endAfter);
            if (!samePixels && beforeImage is not null && afterImage is not null)
            {
                SaveFailure(beforeImage, $"tail-{toMini}-before.png"); SaveFailure(afterImage, $"tail-{toMini}-after.png");
            }
            check("真实转场：结束回调前后实际像素逐字节相同，没有替换残影", samePixels, new { toMini, before = endBefore?.Length, after = endAfter?.Length });
            check("真实转场：结束时不创建、关闭或显隐任何窗口", doneWindowsBefore == doneWindowsAfter && new WindowInteropHelper(main).Handle == hwnd, new { toMini, doneWindowsBefore, doneWindowsAfter });
            check("真实转场：正常收束且没有异常长帧降级", !main.MotionTransitionActive && main.LastMotionTransition == "正常完成", new { toMini, main.LastMotionTransition });
        }
        // 连续三次切换，观察同一个模型的保速反向，而不是结束旧动画后新建替身。
        main.SetCompact(true); await Task.Delay(180);
        var active = main.LiveTransition;
        var at = active?.Motion.Bounds; var velocity = active?.Motion.Springs.Select(s => s.V).ToArray();
        double fullAt = panels.FullOpacity, miniAt = panels.MiniOpacity;
        main.SetCompact(false);
        check("真实转场：中途反向沿用同一模型、当前位置和速度", active is not null && ReferenceEquals(active, main.LiveTransition)
            && at == active.Motion.Bounds && velocity!.SequenceEqual(active.Motion.Springs.Select(s => s.V)), null);
        check("真实转场：反向当刻原内容没有透明度跳变", fullAt == panels.FullOpacity && miniAt == panels.MiniOpacity, null);
        await Task.Delay(120); main.SetCompact(true);
        var wait = Stopwatch.StartNew(); while (main.MotionTransitionActive && wait.ElapsedMilliseconds < 3500) await Task.Delay(15);
        check("真实转场：多次反向后仍在原窗口正确落到 mini", main.IsCompact && panels.MiniOpacity == 1 && new WindowInteropHelper(main).Handle == hwnd, null);
        main.SetCompact(false); wait.Restart(); while (main.MotionTransitionActive && wait.ElapsedMilliseconds < 3500) await Task.Delay(15);
        main.SetCompact(true); await Task.Delay(100);
        var movingBar = main.ActiveBar!;
        movingBar.Motion.SetDragging(true);
        check("真实转场：拖动开始立即终止并落点，禁止再次受力", !main.MotionTransitionActive && movingBar.Motion.IsGuarded
            && !movingBar.Motion.Physics.Moving && panels.MiniOpacity == 1, null);
        movingBar.Motion.SetDragging(false); await Task.Delay(180);
        mock.Left += 31; mock.Top += 19; await Task.Delay(320); main.ActiveTracker!.Update();
        var hostRect = TrackingNative.Bounds(new WindowInteropHelper(mock).Handle);
        var visible = panels.PhysicalBounds;
        check("真实转场：共用 HWND 后宿主移动仍按实际外壳顶边居中", Math.Abs(visible.Left + visible.Right - hostRect.Left - hostRect.Right) <= 2
            && visible.Top >= hostRect.Top && visible.Right - visible.Left == (int)Math.Round(344 * panels.Scale), new { hostRect, visible });
        main.SetCompact(false); wait.Restart(); while (main.MotionTransitionActive && wait.ElapsedMilliseconds < 3500) await Task.Delay(15);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "live-transition-frames.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
        if (frames.Count > 0)
        {
            const int width = 720, rowHeight = 236;
            var board = new DrawingVisual();
            using (var dc = board.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 35)), null, new Rect(0, 0, width, rowHeight * frames.Count));
                for (int i = 0; i < frames.Count; i++)
                {
                    var frame = frames[i];
                    dc.DrawText(new FormattedText(frame.Label, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, Brushes.LightGray, 1), new Point(12, i * rowHeight + 8));
                    double ratio = Math.Min(1, Math.Min((width - 24) / frame.Image.Width, (rowHeight - 38) / frame.Image.Height));
                    dc.DrawImage(frame.Image, new Rect(12, i * rowHeight + 32, frame.Image.Width * ratio, frame.Image.Height * ratio));
                }
            }
            var image = new RenderTargetBitmap(width, rowHeight * frames.Count, 96, 96, PixelFormats.Pbgra32); image.Render(board);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "live-transition-contact-sheet.png")); encoder.Save(file);
        }
    }
    static BitmapSource Capture(MainWindow main)
    {
        main.UpdateLayout();
        var root = main.Panels.Root;
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(root.ActualWidth)), Math.Max(1, (int)Math.Ceiling(root.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        image.Render(root); image.Freeze(); return image;
    }
    static byte[] Pixels(BitmapSource image) { var result = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(result, image.PixelWidth * 4, 0); return result; }
    static void SaveFailure(BitmapSource image, string name)
    { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(file); }
    static int VisibleWindows() => Application.Current.Windows.OfType<Window>().Count(w => w.IsVisible);
}
