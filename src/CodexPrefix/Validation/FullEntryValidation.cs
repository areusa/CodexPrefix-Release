using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace CodexPrefix;

internal static class FullEntryValidation
{
    internal static async Task Run(Action<string, bool, object?> check)
    {
        var mock = new MockWindow(); mock.Show();
        var main = new MainWindow(mock); main.Show();
        var evidence = new List<object>();
        var clock = Stopwatch.StartNew(); long submittedAt = 0;
        main.StatusChanged += status => { if (status.Kind == StatusKind.Submitted) submittedAt = clock.ElapsedMilliseconds; evidence.Add(new { ms = clock.ElapsedMilliseconds, kind = status.Kind.ToString(), status.Text }); };
        async Task Wait(Func<bool> condition)
        {
            var timeout = Stopwatch.StartNew(); while (!condition() && timeout.ElapsedMilliseconds < 12000) await Task.Delay(20);
        }
        try
        {
            await main.PauseDemoScanning(); mock.Activate(); main.UpdateLayout();
            var button = main.OpenRenameButton;
            check("完整模式新入口：30×30 编辑按钮位于 emoji 前，状态行不再是按钮", button.Width == 30 && button.Height == 30
                && Grid.GetColumn(button) < Grid.GetColumn(main.FullEmojiViewport) && ReferenceEquals(button.Parent, main.FullEmojiViewport.Parent)
                && main.StatusBorder.Parent is Grid && !button.IsAncestorOf(main.StatusBorder), null);
            check("完整模式新入口：与 mini 使用相同图标槽和交互模板", button.Style == Application.Current.FindResource("CompactActionButtonStyle")
                && button.Content is Viewbox { Width: 16, Height: 16 }, null);
            var root = main.Panels.FullContent;
            var controls = main.IconPanel.Children.OfType<Button>().Prepend(button).Append(main.ClearButton);
            check("完整模式新入口：加入编辑按钮后整行仍在真实内容宽度内", controls.All(b =>
            {
                var bounds = b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize));
                return bounds.Left >= 0 && bounds.Right <= root.ActualWidth + .01 && bounds.Top >= 0 && bounds.Bottom <= root.ActualHeight;
            }), null);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => main.CurrentBinding is not null && !main.IsWorking);
            check("完整模式新入口：真实 Click 处理器打开并严格绑定夹具输入框", main.CurrentBinding is not null && mock.ShortcutCount == 1 && !button.IsEnabled, null);
            main.IconPanel.Children.OfType<Button>().First(b => Equals(b.Tag, "💦")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !main.IsWorking && main.CurrentStatus?.Kind == StatusKind.Submitted);
            check("完整模式新入口：提交提示仍显示时编辑按钮已恢复，不等待提示计时", main.CurrentStatus?.Kind == StatusKind.Submitted && button.IsEnabled, new { sinceSubmitted = clock.ElapsedMilliseconds - submittedAt });
            mock.Activate(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => main.CurrentBinding is not null && !main.IsWorking);
            check("完整模式新入口：提示保留期间可直接再次打开，不必等状态变黄", main.CurrentBinding is not null && mock.ShortcutCount == 2, null);
            main.IconPanel.Children.OfType<Button>().First(b => Equals(b.Tag, "💭")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !main.IsWorking && main.CurrentStatus?.Kind == StatusKind.Submitted);
            await Task.Delay(2200); await main.RefreshDemo();
            check("完整模式新入口：提交提示两秒后退出，不再保留八秒", main.CurrentStatus?.Kind != StatusKind.Submitted && button.IsEnabled, new { sinceSubmitted = clock.ElapsedMilliseconds - submittedAt });
            var list = ((StackPanel)mock.Content).Children.OfType<ListBox>().Single(); list.SelectedIndex = -1;
            await main.ConfigureDemoOpen(bridge => bridge.DemoOpenWaitMilliseconds = 700);
            mock.Activate(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => main.OpenBlocked);
            check("完整模式新入口：受阻原位感叹号清晰、禁用，模式开关不受阻", main.OpenBlocked && !button.IsEnabled && button.Opacity == 1
                && button.Content is TextBlock { Text: "!" } && button.Style == Application.Current.FindResource("CompactFeedbackButtonStyle") && main.CompactToggle.IsEnabled, null);
            await Wait(() => !main.OpenBlocked && !main.IsWorking);
            check("完整模式新入口：三秒受阻反馈结束后恢复原编辑图标", button.IsEnabled && button.Content is Viewbox
                && button.Style == Application.Current.FindResource("CompactActionButtonStyle"), null);
        }
        finally
        {
            main.Close();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "full-entry-timing.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(true));
        }
    }
}
