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

internal static class EmojiIntegration
{
    static async Task Wait(Func<bool> condition, int timeout = 5000)
    { var watch = Stopwatch.StartNew(); while (!condition() && watch.ElapsedMilliseconds < timeout) await Task.Delay(20); if (!condition()) throw new TimeoutException("夹具条件未在期限内满足。"); }
    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal static async Task<int> Run()
    {
        var rows = new List<object>(); var phases = new List<object>(); bool failed = false;
        void Check(string name, bool pass, object? data = null) { rows.Add(new { name, pass, data }); failed |= !pass; }
        var mock = new MockWindow(); mock.Show(); var main = new MainWindow(mock); main.Show();
        try
        {
            await main.PauseDemoScanning(); main.Activate(); await Task.Delay(100);
            nint handle = new WindowInteropHelper(main).Handle; int moves = main.Panels.NativeMoveCount;
            main.ModePhase += (phase, ms) => phases.Add(new { phase, ms, moves = main.Panels.NativeMoveCount });
            main.SetCompact(true); bool live = main.LiveTransition is not null;
            await Wait(() => !main.MotionTransitionActive);
            Check("新进程第一次收起复用预留 HWND；没有同步搬动旧完整表面", live && main.Panels.NativeMoveCount == moves && main.LastMotionTransition == "正常完成", new { live, moves, after = main.Panels.NativeMoveCount, main.LastMotionTransition });
            Check("首切后仍是同一主 HWND 与真实内容", main.ActiveBar?.Handle == handle && main.Panels.MiniOpacity == 1 && main.Panels.FullOpacity == 0);
            main.SetCompact(false); await Wait(() => !main.MotionTransitionActive);
            main.Activate(); await Task.Delay(100);
            var samples = EmojiCatalog.Samples.GroupBy(s => { EmojiCatalog.TryIdentity(s, out var key); return key; }).Select(g => g.First()).Take(200).ToArray();
            foreach (int n in new[] { 0, 1, 6, 7, 8, 9, 12, 13, 18, 19, 20, 100, 200 })
            {
                var clock = Stopwatch.StartNew(); main.InstallEmojiFixture(samples.Take(n).ToArray()); await Task.Delay(240); main.UpdateLayout();
                Check("完整列表数量与六列半行边界 " + n, main.IconPanel.OrderedButtons.Count == n && Math.Abs(main.FullEmojiViewport.Height - EmojiLayout.FullViewport(n)) < .01,
                    new { n, height = main.FullEmojiViewport.Height, ms = clock.Elapsed.TotalMilliseconds });
                main.SetCompact(true); await Wait(() => !main.MotionTransitionActive);
                var bar = main.ActiveBar!; bar.UpdateLayout();
                Check("mini 数量/视口/固定区 " + n, Math.Abs(bar.DesignWidth - EmojiLayout.MiniWidth(n, true)) < .01 && Math.Abs(bar.EmojiScroll.Width - EmojiLayout.MiniViewport(n)) < .01, new { n, bar.DesignWidth, viewport = bar.EmojiScroll.Width });
                if (n > 8)
                {
                    bar.EmojiScroll.ScrollTo(double.MaxValue, false); await Task.Delay(40);
                    Check("末项可完全滚入且滚动不重建 HWND " + n, Math.Abs(bar.EmojiScroll.Offset - bar.EmojiScroll.Maximum) < .01 && bar.Handle == handle);
                }
                main.SetCompact(false); await Wait(() => !main.MotionTransitionActive); main.Activate();
            }
            main.InstallEmojiFixture(PrefixRules.Icons); await Task.Delay(240);
            var original = File.ReadAllBytes(main.EmojiConfigurationPath);
            int shortcuts = mock.ShortcutCount;
            using (var probeEntered = new ManualResetEventSlim())
            using (var releaseProbe = new ManualResetEventSlim())
            {
                await main.ConfigureDemoOpen(bridge => bridge.DemoBeforeProbe = () => { probeEntered.Set(); releaseProbe.Wait(4000); });
                var lateScan = main.RefreshDemo(); await Wait(() => probeEntered.IsSet);
                main.BeginEmojiEdit(); releaseProbe.Set(); await lateScan;
                Check("在途只读扫描不阻塞设置，晚到结果不恢复改名权限", main.EditingEmoji && main.CurrentBinding is null && !main.OpenRenameButton.IsEnabled && main.CurrentStatus?.Text.StartsWith("编辑列表") == true);
                await main.ConfigureDemoOpen(bridge => bridge.DemoBeforeProbe = null);
                main.CancelEmojiEdit();
            }
            main.BeginEmojiEdit();
            Check("未绑定也能编辑本地配置；固定添加入口位于首位", main.EditingEmoji && main.IconPanel.OrderedButtons[0].Uid == "add" && main.IconPanel.OrderedButtons.Skip(1).All(b => b.IsEnabled));
            await Task.Delay(240); Export(main, "emoji-settings-editing-preview.png");
            var editingButton = main.IconPanel.OrderedButtons[1];
            int settled = 0; var settling = Stopwatch.StartNew();
            while (settled < 5 && settling.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(20);
                settled = (editingButton.TranslatePoint(new Point(), main.IconPanel) - new Point(39, 0)).Length < .001 ? settled + 1 : 0;
            }
            var slot = editingButton.TranslatePoint(new Point(), main.IconPanel);
            var wiggleSamples = new List<object>(); var angles = new List<double>(); var shifts = new List<Vector>(); bool slotsStable = settled == 5; double maximumSlotDelta = 0;
            main.Activate();
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(20);
                var matrix = ((FrameworkElement)editingButton.Content).RenderTransform.Value;
                double angle = Math.Atan2(matrix.M12, matrix.M11) * 180 / Math.PI;
                angles.Add(angle); shifts.Add(new Vector(matrix.OffsetX, matrix.OffsetY));
                double delta = (editingButton.TranslatePoint(new Point(), main.IconPanel) - slot).Length;
                maximumSlotDelta = Math.Max(maximumSlotDelta, delta); slotsStable &= delta < .01;
                wiggleSamples.Add(new { angle, x = matrix.OffsetX, y = matrix.OffsetY, slotDelta = delta });
            }
            Check("编辑态实际可见内容有增强旋转和平滑位置抖动", angles.Max(Math.Abs) > 1.8 && shifts.Max(s => s.Length) > .15 && shifts.All(s => Math.Abs(s.X) <= .8 && Math.Abs(s.Y) <= .55));
            Check("编辑抖动不改变按钮槽位和命中层", slotsStable && editingButton.RenderTransform is TranslateTransform, new { settled, maximumSlotDelta, transform = editingButton.RenderTransform.GetType().Name });
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "emoji-edit-wiggle.json"), JsonSerializer.Serialize(wiggleSamples, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
            await main.RequestOpenRename();
            Check("设置态命令入口拒绝发送快捷键", mock.ShortcutCount == shortcuts && main.CurrentBinding is null);
            var first = main.IconPanel.OrderedButtons[1]; Click(first);
            Check("点击只标记草稿待删，不提交会话", main.ActiveEmojiDraft!.Items[0].Deleted && File.ReadAllBytes(main.EmojiConfigurationPath).SequenceEqual(original) && mock.ShortcutCount == shortcuts);
            Click(first); Check("待删除项再次点击恢复", !main.ActiveEmojiDraft.Items[0].Deleted);
            var id = first.Uid; main.ActiveEmojiDraft.Move(id, 5); main.ActiveEmojiDraft.Add("👩‍💻", out var newId);
            main.CancelEmojiEdit(); await Task.Delay(240);
            Check("取消撤销顺序/新增/删除且不落盘", main.EmojiConfiguration.Items[0].Id == id && main.EmojiConfiguration.Items.All(i => i.Id != newId) && original.SequenceEqual(File.ReadAllBytes(main.EmojiConfigurationPath)));
            main.BeginEmojiEdit(); main.ActiveEmojiDraft!.Add("👩‍💻", out newId); main.InjectEmojiSaveFailure(true); main.SaveEmojiEdit();
            Check("保存失败保留草稿与原配置", main.EditingEmoji && main.ActiveEmojiDraft.Items.Any(i => i.Item.Id == newId) && original.SequenceEqual(File.ReadAllBytes(main.EmojiConfigurationPath)) && main.CurrentStatus?.Kind == StatusKind.Error);
            main.InjectEmojiSaveFailure(false); main.SaveEmojiEdit(); await Task.Delay(260);
            Check("成功保存后一次发布到两种视图", !main.EditingEmoji && main.EmojiConfiguration.Items.Any(i => i.Id == newId) && new EmojiConfigurationStore(Path.GetDirectoryName(main.EmojiConfigurationPath)!).Load().Items.Any(i => i.Id == newId));
            main.InstallEmojiFixture(samples.Take(18).ToArray()); await Task.Delay(240); main.BeginEmojiEdit(); await Task.Delay(240);
            Check("18 项进入设置因添加单元变为 3.5 行", main.IconPanel.OrderedButtons.Count == 19 && main.FullEmojiViewport.Height == 134.5);
            main.CancelEmojiEdit(); await Task.Delay(240); Check("退出设置移除添加单元并恢复三行", main.IconPanel.OrderedButtons.Count == 18 && main.FullEmojiViewport.Height == 113);
            main.InstallEmojiFixture(PrefixRules.Icons); await Task.Delay(240);
            mock.Activate(); await main.RequestOpenRename(); await Wait(() => !main.IsWorking);
            Check("无前缀标题清除按钮禁用且有效 emoji 可用", main.CurrentBinding is not null && !main.ClearButton.IsEnabled && main.IconPanel.OrderedButtons.All(b => b.IsEnabled));
            var titleBefore = main.CurrentBinding!.OriginalValue;
            Click(main.IconPanel.OrderedButtons.First(b => Equals(b.Tag, "💭"))); await Wait(() => !main.IsWorking && main.CurrentStatus?.Kind == StatusKind.Submitted);
            mock.Activate(); await main.RequestOpenRename(); await Wait(() => !main.IsWorking);
            Check("真实绑定标题的当前前缀项禁用；其他项和清除仍可用", main.CurrentBinding?.OriginalValue == "💭 " + titleBefore && !main.IconPanel.OrderedButtons.First(b => Equals(b.Tag, "💭")).IsEnabled && main.ClearButton.IsEnabled);
            main.BeginEmojiEdit(); await Task.Delay(240);
            Check("当前前缀进入设置后可编辑且前台改名权限清空", main.IconPanel.OrderedButtons.First(b => Equals(b.Tag, "💭")).IsEnabled && main.CurrentBinding is null);
            main.CancelEmojiEdit();
            await Task.Delay(260); main.BeginEmojiEdit(); await Task.Delay(260);
            double beforeInputHeight = main.Panels.FullContent.Height;
            Click(main.IconPanel.OrderedButtons[0]);
            var heights = new List<double>(); var sampleClock = Stopwatch.StartNew();
            while (sampleClock.ElapsedMilliseconds < 260) { heights.Add(main.Panels.FullContent.Height); await Task.Delay(20); }
            Check("输入层独立尺寸且打开期间主面板高度不变", heights.All(h => Math.Abs(h - beforeInputHeight) < .01) && VisualTreeHelper.GetParent(main.EmojiInputLayer) == main.Panels.Root, new { beforeInputHeight, heights });
            Check("添加输入期间背后列表与配置确认禁用", !main.IconPanel.IsEnabled && !main.ClearButton.IsEnabled && main.EmojiInputLayer.IsVisible);
            main.EmojiInputText.Text = "👩‍💻"; Click(main.EmojiInputConfirm);
            await Wait(() => !main.EmojiInputLayer.IsVisible);
            Check("输入确认仅更新草稿并清除输入层焦点阻塞", main.EditingEmoji && main.ActiveEmojiDraft!.Items.Any(i => i.Item.Token == "👩‍💻") && main.ClearButton.IsEnabled);
            main.CancelEmojiEdit();
            Export(main, "emoji-settings-preview.png");
        }
        catch (Exception ex) { failed = true; rows.Add(new { name = "新增交互测试异常", pass = false, detail = ex.ToString() }); }
        finally
        {
            main.Close();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "emoji-integration.json"), JsonSerializer.Serialize(new { passed = !failed, results = rows, phases, screenFirstFrames = "未采集连续屏幕帧；该报告只证明程序内首切时序/几何与交互，不能证明 DWM 无闪帧。" }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(true));
        }
        return failed ? 1 : 0;
    }
    static void Export(MainWindow main, string name)
    {
        var shell = main.Panels.Shell; var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(shell), null, new Rect(0, 0, shell.ActualWidth, shell.ActualHeight));
        var image = new RenderTargetBitmap((int)Math.Ceiling(shell.ActualWidth), (int)Math.Ceiling(shell.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
    }
}
