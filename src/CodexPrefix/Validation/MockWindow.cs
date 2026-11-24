using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace CodexPrefix;

// 独立验收窗口，只修改本进程的测试数据，不读取或控制 Codex。
public sealed class MockWindow : Window
{
    readonly ListBox list;
    bool mockClosed;
    internal int ShortcutDelayMilliseconds { get; set; }
    internal int EditorDelayMilliseconds { get; set; }
    internal int SaveDelayMilliseconds { get; set; }
    internal int ShortcutCount { get; private set; }
    public MockWindow(bool dense = false, bool ignoreShortcut = false)
    {
        Title = "会话前缀 · 独立验收窗口";
        Icon = (System.Windows.Media.ImageSource)Application.Current.FindResource("AppIcon");
        Width = 460; Height = 465;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");
        Left = 120; Top = 150;
        var layout = new StackPanel { Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock { Text = "手动打开重命名框后再点击浮窗", Margin = new Thickness(0, 0, 0, 12) });
        list = new ListBox { Background = Background, Foreground = System.Windows.Media.Brushes.White, Height = 180 };
        AutomationProperties.SetAutomationId(list, "Sidebar");
        var names = new[] { "同名会话", "同名会话", "💭 方案讨论", "中文  空格与特殊字符 ✌️" };
        if (dense)
        {
            names = names.Concat(Enumerable.Range(1, 28).Select(i => "结构压力测试 " + i)).ToArray();
            VirtualizingStackPanel.SetIsVirtualizing(list, false);
        }
        for (var index = 0; index < names.Length; index++)
        {
            var item = new ListBoxItem { Content = names[index], Padding = new Thickness(12) };
            AutomationProperties.SetAutomationId(item, "Thread" + (index + 1));
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "重命名" };
            rename.Click += (_, _) => ShowRename(item);
            menu.Items.Add(rename); item.ContextMenu = menu;
            var row = new FrameworkElementFactory(typeof(DockPanel));
            var more = new FrameworkElementFactory(typeof(Button));
            more.SetValue(ContentControl.ContentProperty, "⋯");
            more.SetValue(FrameworkElement.WidthProperty, 26d);
            more.SetValue(Control.PaddingProperty, new Thickness(0));
            more.SetValue(UIElement.FocusableProperty, false);
            more.SetValue(DockPanel.DockProperty, Dock.Right);
            more.SetValue(AutomationProperties.NameProperty, "更多操作");
            more.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, args) => { args.Handled = true; menu.PlacementTarget = (Button)sender; menu.IsOpen = true; }));
            row.AppendChild(more);
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("."));
            title.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            if (dense)
            {
                // 模拟真实侧边栏每个条目包含大量图标与装饰节点，禁止靠扩大扫描上限过关。
                var details = new FrameworkElementFactory(typeof(StackPanel));
                details.AppendChild(title);
                for (int decoration = 0; decoration < 8; decoration++)
                {
                    var text = new FrameworkElementFactory(typeof(TextBlock));
                    text.SetValue(TextBlock.TextProperty, "装饰节点 " + decoration);
                    text.SetValue(FrameworkElement.HeightProperty, 1d);
                    details.AppendChild(text);
                }
                row.AppendChild(details);
            }
            else row.AppendChild(title);
            item.ContentTemplate = new DataTemplate { VisualTree = row };
            list.Items.Add(item);
        }
        list.SelectedIndex = 0;
        layout.Children.Add(list);
        var open = new Button { Content = "重命名所选", Margin = new Thickness(0, 12, 0, 10) };
        open.Click += (_, _) => { if (list.SelectedItem is ListBoxItem item) ShowRename(item); };
        layout.Children.Add(open);
        var search = new TextBox { Text = "同名会话", FontSize = 15, Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(search, "搜索会话（必须拒绝接管）");
        layout.Children.Add(search);
        var bad = new Button { Content = "打开歧义输入框（应禁用浮窗）" };
        bad.Click += (_, _) => { if (list.SelectedItem is ListBoxItem item) ShowRename(item, true); };
        layout.Children.Add(bad);
        layout.Children.Add(new TextBlock { Text = "前两行完全同名。对第二行操作时，第一行必须保持不变。", Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });
        Content = layout;
        Closed += (_, _) => mockClosed = true;
        PreviewKeyDown += async (_, e) =>
        {
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key == System.Windows.Input.Key.R && System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt))
            {
                e.Handled = true;
                ++ShortcutCount;
                if (!ignoreShortcut && list.SelectedItem is ListBoxItem item)
                {
                    if (ShortcutDelayMilliseconds > 0) await Task.Delay(ShortcutDelayMilliseconds);
                    if (!mockClosed) ShowRename(item);
                }
            }
        };
    }
    internal void OpenTestRename(bool ambiguous = false) { if (list.SelectedItem is ListBoxItem item) ShowRename(item, ambiguous); }
    void ShowRename(ListBoxItem item, bool ambiguous = false)
    {
        var original = (string)item.Content;
        var dialog = new Window { Owner = this, Icon = Icon, Title = "重命名", Width = 350, Height = ambiguous ? 225 : 190, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, ResizeMode = ResizeMode.NoResize };
        var layout = new StackPanel { Margin = new Thickness(18) };
        var edit = new TextBox { Text = original, FontSize = 16, Margin = new Thickness(0, 0, 0, 16) };
        AutomationProperties.SetName(edit, "会话名称");
        var save = new Button { Content = "保存", IsDefault = true, IsEnabled = false };
        edit.IsEnabled = EditorDelayMilliseconds == 0;
        save.Visibility = SaveDelayMilliseconds == 0 ? Visibility.Visible : Visibility.Collapsed;
        edit.TextChanged += (_, _) => save.IsEnabled = edit.Text != original;
        save.Click += (_, _) => { item.Content = edit.Text; dialog.Close(); };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 7, 0, 0) };
        cancel.Click += (_, _) => dialog.Close();
        layout.Children.Add(edit);
        if (ambiguous) layout.Children.Add(new TextBox { Text = "第二个输入框", Margin = new Thickness(0, 0, 0, 12) });
        layout.Children.Add(save); layout.Children.Add(cancel);
        dialog.Content = layout;
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) dialog.Close(); };
        bool dialogClosed = false;
        dialog.Closed += (_, _) => dialogClosed = true;
        dialog.Loaded += async (_, _) =>
        {
            if (EditorDelayMilliseconds > 0) await Task.Delay(EditorDelayMilliseconds);
            if (dialogClosed || mockClosed) return;
            edit.IsEnabled = true; edit.Focus(); edit.SelectAll();
        };
        dialog.Loaded += async (_, _) =>
        {
            if (SaveDelayMilliseconds > 0) await Task.Delay(SaveDelayMilliseconds);
            if (!dialogClosed && !mockClosed) save.Visibility = Visibility.Visible;
        };
        // 测试夹具与浮窗同进程；不能用 ShowDialog 禁用同线程浮窗。
        // 真正 Codex 的模态窗口属于另一个进程，不会禁用工具。
        dialog.Show();
    }
}
