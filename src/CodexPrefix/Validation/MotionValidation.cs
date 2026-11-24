using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CodexPrefix;

internal static class MotionValidation
{
    internal static void Rules(Action<bool, string> check)
    {
        check(MotionParameters.Values.Count == 50 && MotionParameters.Get("hoverScale") == 1.08 && MotionParameters.Get("internalForceRatio") == .2, "内嵌参数必须为用户确认的完整 50 项，hover 为 1.08 倍");
        using var stream = typeof(MotionValidation).Assembly.GetManifestResourceStream("CodexPrefix.Motion.Reference.json")!;
        using var doc = JsonDocument.Parse(stream);
        double maxError = 0;
        foreach (var row in doc.RootElement.GetProperty("springs").EnumerateArray())
        {
            var s = new MotionSpring(1.17) { V = -3.2, Target = .34 };
            for (int i = 0; i < 60; i++) s.Step(1d / 120, row.GetProperty("k").GetDouble(), row.GetProperty("z").GetDouble(), -100, 100);
            double error = Math.Max(Math.Abs(s.X - row.GetProperty("x").GetDouble()), Math.Abs(s.V - row.GetProperty("v").GetDouble()));
            maxError = Math.Max(maxError, error); check(error < 1e-10, "WPF 弹簧须与 HTML 实际公式一致，包括临界附近与零刚度");
        }
        var morph = new ModeMotion(new Rect(620, 400, 382, 166), new Rect(240, 106, 344, 40), true);
        int firstTarget = -1;
        foreach (var (row, i) in doc.RootElement.GetProperty("morph").EnumerateArray().Select((row, i) => (row, i)))
        {
            morph.Step(1d / 120);
            var geometry = row.GetProperty("geometry");
            foreach (var (key, s) in new[] { ("x", morph.X), ("y", morph.Y), ("w", morph.Width), ("h", morph.Height) })
            {
                var reference = geometry.GetProperty(key);
                double error = Math.Max(Math.Abs(reference.GetProperty("x").GetDouble() - s.X), Math.Abs(reference.GetProperty("v").GetDouble() - s.V));
                maxError = Math.Max(maxError, error); check(error < 1e-8, "WPF 转场几何逐帧须与 HTML 一致");
            }
            check(Math.Abs(morph.SourceOpacity - row.GetProperty("full").GetDouble()) < 1e-10 && Math.Abs(morph.TargetOpacity - row.GetProperty("mini").GetDouble()) < 1e-10 && morph.Done == row.GetProperty("done").GetBoolean(), "转场内容时序、到位条件须与 HTML 逐帧一致");
            if (firstTarget == -1 && morph.TargetOpacity > 0) firstTarget = i;
        }
        check(firstTarget >= 0 && firstTarget < 100 && morph.Done, "目标内容应到位前开始显示，转场最终收束");
        var external = new MiniMotion(10); var inner = new MiniMotion(10);
        external.Swipe(12000, 2000, .016, false, 0); inner.Swipe(12000, 2000, .016, true, 0);
        foreach (var pair in external.Springs.Take(5).Zip(inner.Springs.Take(5))) check(Math.Abs(pair.First.V * .2 - pair.Second.V) < 1e-10, "内部平移与形变均在外部封顶增量后乘 0.2");
        var click = new MiniMotion(10); click.Press(4, 0);
        check(Math.Abs(click.Scale[4].Target - .34) < 1e-10, "按压深度独立于释放弹性");
        click.Release(); click.Click(4, 0);
        check(click.Scale[4].V == 12.6 && click.WaveCount == 9, "释放弹性与独立邻居波已接入");
        click.Step(.016, .016); check(click.Shift.All(s => s.X == 0 && s.V == 0), "邻居传递遵循 22ms 逐级延迟");
        click.Step(.016, .032); check(click.Shift[3].V < 0 && click.Shift[5].V > 0 && click.Shift[2].V == 0, "第一层邻居先受力，第二层仍等待");
        click.Reset(); check(!click.Moving && click.WaveCount == 0 && click.Pressed == -1 && click.Springs.All(s => s.V == 0), "中断清空速度、按压和延迟波");
        var budget = new MiniMotion(10); budget.Hovering(true, 0); budget.Swipe(1000, 200, .016, false, 0); budget.Step(.016, 9);
        check(!budget.Moving && budget.Hover.X == MotionParameters.Get("hoverScale"), "预算到期保留当前 hover 意图，不强制缩回");
        var stalled = new ModeMotion(new Rect(0, 0, 382, 166), new Rect(100, 100, 344, 40), true); stalled.Step(.3);
        check(stalled.Done && stalled.Bounds.X == 100, "长帧直接落点，不追赶过时动画");
        var reduced = new ModeMotion(new Rect(0, 0, 382, 166), new Rect(100, 100, 344, 40), true, true); reduced.Step(.08); reduced.Step(.08);
        check(reduced.Done && reduced.TargetOpacity == 1, "系统减少动画时使用 160ms 淡入交接");
        foreach (bool lessMotion in new[] { false, true })
        {
            var reverse = new ModeMotion(new Rect(20, 240, 382, 166), new Rect(100, 100, 344, 40), true, lessMotion);
            for (int i = 0; i < 9; i++) reverse.Step(1d / 120);
            var pose = reverse.Bounds; var velocity = reverse.Springs.Select(s => s.V).ToArray();
            double full = reverse.SourceOpacity, mini = reverse.TargetOpacity;
            reverse.Retarget(new Rect(20, 240, 382, 166), false);
            check(reverse.Bounds == pose && reverse.Springs.Select(s => s.V).SequenceEqual(velocity), "中途反向保留位置、尺寸和四轴速度");
            check(reverse.SourceOpacity == mini && reverse.TargetOpacity == full, "中途反向不交换或重置真实内容的当前透明度");
            reverse.Step(0);
            check(reverse.SourceOpacity == mini && reverse.TargetOpacity == full, "反向首帧和减少动画路径均保持透明度连续");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "motion-numeric.json"), JsonSerializer.Serialize(new { calibration = "./assets/motion/approved-2026-10-28.json", hoverScale = MotionParameters.Get("hoverScale"), springCases = 64, morphFrames = 180, maxError, firstTargetFrame = firstTarget }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
    }
    internal static async Task Windows(Action<string, bool, object?> check)
    {
        var bar = new CompactBar(_ => Task.CompletedTask, () => { }, () => { });
        try
        {
            bar.SetAttached(true); bar.Show(); await Task.Delay(200); bar.UpdateLayout();
            bar.Motion.HostActive = () => true;
            bar.SetActions(true, false); bar.SetOpenEnabled(true);
            var m = bar.Motion.Physics;
            foreach (var p in new[] { (MotionParameters.Get("hoverScale"), 12d, 1d, .03, -.02, 3d), (1d, -24d, -1d, -.05, .05, -8d) })
            {
                m.Hover.Snap(p.Item1); m.X.Snap(p.Item2); m.Y.Snap(p.Item3); m.StretchX.Snap(p.Item4); m.StretchY.Snap(p.Item5); m.Skew.Snap(p.Item6);
                for (int i = 0; i < m.Scale.Length; i++) { m.Scale[i].Snap(i == 5 ? .34 : 1.22); m.Shift[i].Snap(i % 2 == 0 ? 3 : -3); }
                bar.Motion.ApplyPose();
                foreach (var button in bar.ActionButtons)
                {
                    var screen = button.TranslatePoint(new Point(15, 15), bar);
                    check("原生变换后按钮中心仍命中自身（点击缩放不缩命中区）", bar.Motion.Pick(screen) == button, new { index = bar.ActionButtons.ToList().IndexOf(button), p });
                }
                var image = new RenderTargetBitmap((int)bar.Width * 2, (int)bar.Height * 2, 192, 192, PixelFormats.Pbgra32); image.Render((Visual)bar.Content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(AppContext.BaseDirectory, p.Item2 > 0 ? "motion-native-pose.png" : "motion-native-deform.png")); encoder.Save(file);
            }
            int before = bar.Motion.Epoch;
            bar.Motion.Interrupt("原生重建测试");
            check("重建立即归位并使旧手势代数失效", bar.Motion.Epoch > before && !m.Moving && bar.Motion.IsGuarded, null);
            var padding = bar.PointToScreen(new Point(2, 2));
            var nativeHit = WindowFromPoint(new NativePoint((int)padding.X, (int)padding.Y));
            var handle = new WindowInteropHelper(bar).Handle;
            check("透明预留区没有按钮命中且 Windows 不截留该像素", bar.Motion.Pick(new Point(2, 2)) is null && nativeHit != handle,
                new { nativeHit = nativeHit.ToInt64(), handle = handle.ToInt64(), wpfHit = bar.InputHitTest(new Point(2, 2))?.GetType().Name });
            bar.Motion.SetDragging(true); await Task.Delay(180);
            check("拖动超过保护时间仍保持禁止响应", bar.Motion.IsGuarded, null);
            bar.Motion.SetDragging(false); await Task.Delay(180);
            check("拖动结束保护期后可重新响应", !bar.Motion.IsGuarded, null);
        }
        finally { bar.Close(); }
        var mock = new MockWindow(); mock.Show();
        var main = new MainWindow(mock) { WindowStartupLocation = WindowStartupLocation.Manual, Left = mock.Left + 50, Top = mock.Top + 180 }; main.Show();
        try
        {
            // 主窗默认 CenterScreen 可能跟随前一项多屏测试落到另一 DPI；
            // 此处是同 DPI 动画的正向测试，必须明确把两个夹具放到同一屏。
            await main.PauseDemoScanning(); mock.Activate(); await Task.Delay(100);
            check("转场正向验收前完整窗口与宿主 DPI 相同",
                TrackingNative.GetDpiForWindow(new WindowInteropHelper(main).Handle) == TrackingNative.GetDpiForWindow(new WindowInteropHelper(mock).Handle),
                new { sourceDpi = TrackingNative.GetDpiForWindow(new WindowInteropHelper(main).Handle), hostDpi = TrackingNative.GetDpiForWindow(new WindowInteropHelper(mock).Handle) });
            main.SetCompact(true);
            check("已加载窗口收起时直接驱动原控件", main.MotionTransitionActive, new { main.LastMotionTransition });
            await Task.Delay(1700);
            check("同 DPI 收起完成且真实小条从未被替换", !main.MotionTransitionActive && main.Panels.MiniOpacity == 1 && main.LastMotionTransition == "正常完成", new { main.LastMotionTransition });
            main.SetCompact(false);
            check("展开时直接驱动原控件", main.MotionTransitionActive, new { main.LastMotionTransition });
            await Task.Delay(1700);
            check("展开完成并交回真实完整窗口", !main.MotionTransitionActive && main.Opacity == 1 && main.ActiveBar is null && main.LastMotionTransition == "正常完成", new { main.LastMotionTransition });
            await LiveTransitionValidation.Run(main, mock, check);
        }
        finally { main.Close(); }
        await MotionSamplingValidation.Windows(check);
    }
    // 不创建 HWND、不获取桌面画面、不发送鼠标；使用真实模板离屏渲染。
    internal static void Offscreen(Action<bool, string> check)
    {
        var bar = new CompactBar(_ => Task.CompletedTask, () => { }, () => { });
        var originalMode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            bar.SetAttached(true); bar.SetActions(true, false); bar.SetOpenEnabled(true); bar.RenderIcons();
            var root = (FrameworkElement)bar.Content;
            root.Measure(new Size(bar.Width, bar.Height)); root.Arrange(new Rect(0, 0, bar.Width, bar.Height)); root.UpdateLayout();
            bar.Motion.Start();
            var poses = new List<(string Label, BitmapSource Image)>();
            void Capture(string label)
            {
                root.UpdateLayout(); bar.Motion.ApplyPose();
                var image = new RenderTargetBitmap((int)bar.Width * 2, (int)bar.Height * 2, 192, 192, PixelFormats.Pbgra32);
                image.Render(root); image.Freeze(); poses.Add((label, image));
                int stride = image.PixelWidth * 4; var pixels = new byte[stride * image.PixelHeight]; image.CopyPixels(pixels, stride, 0);
                bool hasPixels = false, clipped = false;
                for (int y = 0; y < image.PixelHeight; y++) for (int x = 0; x < image.PixelWidth; x++) if (pixels[y * stride + x * 4 + 3] > 0)
                { hasPixels = true; if (x < 2 || x >= image.PixelWidth - 2 || y < 2 || y >= image.PixelHeight - 2) clipped = true; }
                check(hasPixels && !clipped, "已确认参数的可见形变不能撞到透明运动窗口边缘");
                foreach (var button in bar.ActionButtons)
                {
                    check(button.Template.FindName("MotionFace", button) is FrameworkElement { RenderTransform: ScaleTransform }, "按钮内容与底框共用点击缩放，命中层不缩小");
                    check(button.RenderTransform is TranslateTransform, "邻居位移在点击缩放层之外独立变换");
                    var transform = button.TransformToAncestor(root);
                    var point = transform.Transform(new Point(15, 15));
                    var back = transform.Inverse!.Transform(point);
                    check((back - new Point(15, 15)).Length < 1e-8, "WPF 变换链正反映射在组合形变下保持一致");
                }
            }
            Capture("静止：30px 按钮 / 22px 原生字形");
            var m = bar.Motion.Physics;
            m.Hover.Snap(MotionParameters.Get("hoverScale")); Capture($"悬停：顶部中心 {MotionParameters.Get("hoverScale"):0.00} 倍");
            m.X.Snap(34); m.Y.Snap(2); m.StretchX.Snap(.05); m.StretchY.Snap(-.05); m.Skew.Snap(8);
            for (int i = 0; i < m.Scale.Length; i++) { m.Scale[i].Snap(i == 5 ? .34 : 1.22); m.Shift[i].Snap(i % 2 == 0 ? 12 : -12); }
            Capture("边界组合：平移 / 剪切 / 形变 / 邻居波");
            bar.Motion.Interrupt("离屏重建测试"); Capture("中断后立即归位，清空旧手势");
            double width = bar.Width, rowHeight = 112;
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 33)), null, new Rect(0, 0, width, rowHeight * poses.Count));
                for (int i = 0; i < poses.Count; i++)
                {
                    dc.DrawImage(poses[i].Image, new Rect(0, i * rowHeight + 8, width, bar.Height));
                    dc.DrawText(new FormattedText(poses[i].Label, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 10, Brushes.LightGray, 2), new Point(16, i * rowHeight + 4));
                }
            }
            var board = new RenderTargetBitmap((int)width * 2, (int)(rowHeight * poses.Count) * 2, 192, 192, PixelFormats.Pbgra32); board.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(board));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "motion-offscreen-preview.png")); encoder.Save(file);
            foreach (double dpi in new[] { 1d, 1.25, 1.5, 2d }) foreach (string icon in PrefixRules.Icons)
            {
                var image = NativeEmoji.RenderFitted(icon, dpi);
                check(Math.Abs(image.Width - 24) <= 1 / dpi && Math.Abs(image.Height - 24) <= 1 / dpi, "各 DPI 原生 emoji 保持 24 DIP 图标槽");
            }
        }
        finally { bar.Motion.Dispose(); bar.Close(); Application.Current.ShutdownMode = originalMode; }
    }
    [StructLayout(LayoutKind.Sequential)] readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(NativePoint point);
}
