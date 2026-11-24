using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexPrefix;

public partial class MainWindow
{
    enum EmojiEditPhase { Normal, Editing, Adding, Saving }
    EmojiEditPhase emojiPhase;
    EmojiConfigurationStore emojiStore = null!;
    PrefixSnapshot emojiConfig = PrefixSnapshot.Default;
    EmojiDraft? emojiDraft;
    readonly Dictionary<string, Button> fullEmojiButtons = [];
    Button? addEmojiButton;
    long emojiGeneration;
    string[]? dragOrder;
    string? savedAnchor;
    double savedOffset;
    bool writingEditorStatus, composingEmoji, inputClosing, recoveringConfig;
    static readonly DependencyProperty AnimatedListHeightProperty = DependencyProperty.Register("AnimatedListHeight", typeof(double), typeof(MainWindow), new PropertyMetadata(35d, (d, e) => ((MainWindow)d).ApplyListHeight((double)e.NewValue)));
    internal static string? TestDataRoot;
    internal bool EditingEmoji => emojiPhase != EmojiEditPhase.Normal;
    internal PrefixSnapshot EmojiConfiguration => emojiConfig;
    internal EmojiDraft? ActiveEmojiDraft => emojiDraft;
    internal string EmojiConfigurationPath => emojiStore.FilePath;

    void InitializeEmojiConfiguration()
    {
        InitializeProfiles();
        IconPanel.Viewport = FullEmojiViewport;
        IconPanel.ToggleDeleted += id =>
        {
            if (emojiPhase != EmojiEditPhase.Editing || emojiDraft is null) return;
            var item = emojiDraft.Items.Single(i => i.Item.Id == id); item.Deleted = !item.Deleted;
            RefreshFullEmojiVisuals(); EditorStatus("草稿已更新；勾号保存，叉号取消。再次点击可撤销待删除。");
        };
        IconPanel.DragBeginning += () => { dragOrder = emojiDraft?.Items.Select(i => i.Item.Id).ToArray(); ClearButton.IsEnabled = EmojiSettingsButton.IsEnabled = false; };
        IconPanel.Reorder += (id, slot) =>
        {
            if (emojiDraft is null || emojiPhase != EmojiEditPhase.Editing) return;
            emojiDraft.Move(id, slot); RefreshFullItems();
            AutomationProperties.SetHelpText(IconPanel, "已移动到第 " + (slot + 1) + " 项");
        };
        IconPanel.DragEnded += commit =>
        {
            if (!commit && emojiDraft is not null && dragOrder is not null) { emojiDraft.RestoreOrder(dragOrder); RefreshFullItems(); }
            dragOrder = null;
            if (!closed) EnableActions(false);
        };
        IconPanel.CancelEditing += CancelEmojiEdit;
        FullEmojiViewport.UserScrolling += () => { if (!IconPanel.Dragging) IconPanel.CancelGesture(); };
        Activated += (_, _) => IconPanel.Resume();
        Deactivated += (_, _) => IconPanel.CancelGesture();
        TextCompositionManager.AddPreviewTextInputStartHandler(EmojiInputText, (_, _) => composingEmoji = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(EmojiInputText, (_, _) => composingEmoji = true);
        TextCompositionManager.AddPreviewTextInputHandler(EmojiInputText, (_, _) => { composingEmoji = false; Dispatcher.BeginInvoke(UpdateEmojiInput); });
        EmojiInputLayer.PreviewKeyDown += (_, e) =>
        {
            if (composingEmoji || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
            if (inputClosing || e.IsRepeat) { if (e.Key is Key.Enter or Key.Escape) e.Handled = true; return; }
            if (e.Key == Key.Escape) { CancelEmojiInput(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.Enter && !composingEmoji && EmojiInputConfirm.IsEnabled) { ConfirmEmojiInput(this, new RoutedEventArgs()); e.Handled = true; }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Handled || emojiPhase != EmojiEditPhase.Editing || e.Key != Key.Escape) return;
            if (IconPanel.Dragging) IconPanel.CancelGesture(); else CancelEmojiEdit(); e.Handled = true;
        };
        RefreshFullItems(); UpdateConfigurationIcons();
    }
    internal static object ActionGlyph(string key, double size = 16)
    {
        var path = new System.Windows.Shapes.Path { Data = (Geometry)Application.Current.FindResource(key), Stroke = new SolidColorBrush(Color.FromRgb(163, 163, 174)), StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        if (key is "CloseGeometry" or "ProfileSwitchGeometry" or "BuiltinDefaultGeometry" or "RefreshUpdateGeometry" or "DownloadUpdateGeometry") { path.Stroke = null; path.Fill = new SolidColorBrush(Color.FromRgb(163, 163, 174)); }
        return new Viewbox { Width = size, Height = size, IsHitTestVisible = false, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }
    void UpdateConfigurationIcons()
    {
        RefreshProfileUi();
        ClearButton.Content = ActionGlyph(EditingEmoji ? "CheckGeometry" : "EraserGeometry");
        EmojiSettingsButton.Content = ActionGlyph(EditingEmoji ? "CloseGeometry" : "SettingsGeometry");
        ClearButton.ToolTip = EditingEmoji ? "确认并保存 emoji 配置" : "清除已登记的前缀";
        EmojiSettingsButton.ToolTip = EditingEmoji ? "取消全部草稿修改" : "编辑 emoji 列表";
        AutomationProperties.SetName(ClearButton, (string)ClearButton.ToolTip);
        AutomationProperties.SetName(EmojiSettingsButton, (string)EmojiSettingsButton.ToolTip);
        if (IsLoaded && SystemParameters.ClientAreaAnimation)
        {
            var fade = new DoubleAnimation(.5, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            ClearButton.BeginAnimation(OpacityProperty, fade); EmojiSettingsButton.BeginAnimation(OpacityProperty, fade);
        }
    }
    Button NewFullEmojiButton(EmojiItem item)
    {
        var button = new Button { Uid = item.Id, Tag = item.Token, Style = (Style)FindResource("CompactActionButtonStyle"), Width = 35, Height = 35, Focusable = true };
        AutomationProperties.SetAutomationId(button, "Emoji-" + item.Id);
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += async (_, _) =>
        {
            if (EditingEmoji) { if (emojiPhase == EmojiEditPhase.Editing) IconPanel.ToggleByKeyboard(button.Uid); return; }
            if (button.IsEnabled && emojiConfig.Items.Any(i => i.Id == button.Uid && i.Token == (string)button.Tag)) await Rename((string)button.Tag);
        };
        return button;
    }
    void RefreshFullItems(bool snapImmediate = false)
    {
        var items = emojiDraft?.Items.Select(i => i.Item).ToArray() ?? emojiConfig.Items.ToArray();
        foreach (var id in fullEmojiButtons.Keys.Except(items.Select(i => i.Id)).ToArray()) fullEmojiButtons.Remove(id);
        foreach (var item in items) { if (!fullEmojiButtons.ContainsKey(item.Id)) fullEmojiButtons.Add(item.Id, NewFullEmojiButton(item)); else fullEmojiButtons[item.Id].Tag = item.Token; }
        addEmojiButton ??= MakeAddButton();
        IconPanel.SetButtons((EditingEmoji ? new[] { addEmojiButton } : Array.Empty<Button>()).Concat(items.Select(i => fullEmojiButtons[i.Id])), snapImmediate);
        IconPanel.Editing = EditingEmoji;
        RefreshFullEmojiVisuals(); UpdateFullEmojiLayout();
        foreach(var b in fullEmojiButtons.Values) b.Focusable = EditingEmoji;
        ClearButton.Focusable = EmojiSettingsButton.Focusable = EditingEmoji;
    }
    Button MakeAddButton()
    {
        var b = new Button { Uid = "add", Width = 35, Height = 35, Style = (Style)FindResource("CompactActionButtonStyle"), Focusable = true, Content = ActionGlyph("AddGeometry"), ToolTip = "添加一枚 emoji" };
        AutomationProperties.SetAutomationId(b, "AddEmoji"); AutomationProperties.SetName(b, "添加一枚 emoji");
        b.Click += (_, _) => OpenEmojiInput(); return b;
    }
    void RefreshFullEmojiVisuals()
    {
        foreach (var (id, button) in fullEmojiButtons)
        {
            string token = (string)button.Tag;
            var state = emojiDraft?.Items.FirstOrDefault(i => i.Item.Id == id);
            var visual = new Grid { Width = 35, Height = 35, IsHitTestVisible = false };
            FrameworkElement glyph;
            bool fallback = false;
            try { glyph = new Image { Source = NativeEmoji.RenderFitted(token, VisualTreeHelper.GetDpi(this).DpiScaleX), Width = 24, Height = 24 }; }
            catch (Exception ex) { fallback = true; log.Write("彩色字形", "系统渲染失败，回退文字：" + ex.GetType().Name); glyph = new TextBlock { Text = token, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
            glyph.Opacity = state?.Deleted == true ? .28 : 1; visual.Children.Add(glyph);
            if (state?.Added == true && !state.Deleted) visual.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(47, 99, 76)), CornerRadius = new CornerRadius(3), Width = 12, Height = 11, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = "新", FontSize = 8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center } });
            if (state?.Deleted == true) visual.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(137, 48, 65)), CornerRadius = new CornerRadius(6), Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Child = (UIElement)ActionGlyph("CloseGeometry", 9) });
            button.Content = visual;
            var text = state is null ? "设置前缀 " + token : (state.Deleted ? "待删除；点击撤销 " : "点击标记待删除；拖动或 Alt+方向键排序 ") + token;
            if (fallback) text += "（系统彩色渲染不可用，已回退文字）";
            button.ToolTip = text; AutomationProperties.SetName(button, text);
        }
    }
    internal void UpdateFullEmojiLayout()
    {
        int count = (emojiDraft?.Items.Count ?? emojiConfig.Items.Count) + (EditingEmoji ? 1 : 0);
        double maximum = EmojiLayout.FullViewport(count);
        if (ownHandle != 0)
        {
            var work = TrackingNative.WorkArea(ownHandle); double scale = panels.Scale;
            maximum = Math.Min(maximum, Math.Max(17.5, (work.Bottom - work.Top) / scale - 153));
            if (maximum < EmojiLayout.FullViewport(count)) log.Write("布局适配", "工作区不足，完整列表视口已收窄；保留固定操作与状态区。");
        }
        if (IsLoaded && !compact && SystemParameters.ClientAreaAnimation && Math.Abs((double)GetValue(AnimatedListHeightProperty) - maximum) > .01)
            BeginAnimation(AnimatedListHeightProperty, new DoubleAnimation((double)GetValue(AnimatedListHeightProperty), maximum, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { BeginAnimation(AnimatedListHeightProperty, null); SetValue(AnimatedListHeightProperty, maximum); ApplyListHeight(maximum); }
    }
    void ApplyListHeight(double height)
    {
        FullEmojiViewport.Height = height; EmojiActionRow.Height = new GridLength(48 + height - 35);
        if (panels is null) return;
        panels.ResizeFullContent(166 + height - 35, !compact && IsLoaded);
    }
    void UpdateEmojiInputPlacement()
    {
        if (closed || !IsLoaded || emojiPhase != EmojiEditPhase.Adding || !EmojiInputLayer.IsVisible) return;
        // 输入层位于共享 HWND 的根画布中；主面板仅提供对齐锚点，不提供高度约束。
        var work = TrackingNative.WorkArea(ownHandle); double scale = panels.Scale;
        var topLeft = PointFromScreen(new Point(work.Left, work.Top));
        double width = Math.Max(1, (work.Right - work.Left) / scale - 16), height = Math.Max(1, (work.Bottom - work.Top) / scale - 16);
        EmojiInputLayer.Width = Math.Min(352, width); EmojiInputLayer.MaxHeight = height;
        EmojiInputLayer.Measure(new Size(EmojiInputLayer.Width, height));
        var anchor = panels.FullContent.TranslatePoint(new Point(), panels.Root);
        double left = Math.Clamp(anchor.X, topLeft.X + 8, topLeft.X + 8 + width - EmojiInputLayer.DesiredSize.Width);
        double top = Math.Clamp(anchor.Y, topLeft.Y + 8, topLeft.Y + 8 + height - EmojiInputLayer.DesiredSize.Height);
        if (Canvas.GetLeft(EmojiInputLayer) != left) Canvas.SetLeft(EmojiInputLayer, left);
        if (Canvas.GetTop(EmojiInputLayer) != top) Canvas.SetTop(EmojiInputLayer, top);
        var start = PointToScreen(new Point(left, top));
        panels.ReserveOverlayBounds(new((int)Math.Floor(start.X), (int)Math.Floor(start.Y),
            (int)Math.Ceiling(start.X + EmojiInputLayer.DesiredSize.Width * scale), (int)Math.Ceiling(start.Y + EmojiInputLayer.DesiredSize.Height * scale)));
    }
    internal static bool ShouldSuppressFullActivation(bool compact, bool editingEmoji) => !compact && !editingEmoji;
    internal void ApplyFullWindowActivationStyle()
    {
        if (ownHandle == 0 || compact) return;
        long ex = TrackingNative.GetWindowLongPtrW(ownHandle, -20).ToInt64();
        ex &= ~0x80L;
        ex |= 0x00040000L;
        if (EditingEmoji) ex &= ~0x08000000L;
        else ex |= 0x08000000L;
        TrackingNative.SetWindowLongPtrW(ownHandle, -20, (nint)ex);
    }
    nint EmojiWindowMessage(nint hwnd, int message, nint wp, nint lp, ref bool handled)
    {
        if (message == 0x21 && ShouldSuppressFullActivation(compact, EditingEmoji)) { handled = true; return 3; }
        if (message != 0x20E || !compact || bar is null) return 0;
        int x = (short)(lp.ToInt64() & 0xFFFF), y = (short)((lp.ToInt64() >> 16) & 0xFFFF);
        var p = bar.EmojiScroll.PointFromScreen(new Point(x, y));
        if (new Rect(bar.EmojiScroll.RenderSize).Contains(p)) { bar.EmojiScroll.ScrollBy((short)((wp.ToInt64() >> 16) & 0xFFFF) / 120d * 48); handled = true; }
        return 0;
    }
    void EditorStatus(string text, bool failed = false)
    {
        writingEditorStatus = true;
        try { ShowStatus(new(failed ? StatusKind.Error : StatusKind.Ready, text)); }
        finally { writingEditorStatus = false; }
    }
    internal void BeginEmojiEdit()
    {
        if (profileSwitching || closed || operation || compact || panels.Relocating || modeTransition is not null || EditingEmoji) return;
        CancelEditorPointers();
        emojiPhase = EmojiEditPhase.Editing; emojiDraft = new(emojiConfig);
        ApplyFullWindowActivationStyle(); if (IsVisible && !IsActive) Activate();
        ++contextVersion; ++visualEpoch; Interlocked.Increment(ref emojiGeneration); current = null;
        savedOffset = FullEmojiViewport.Offset;
        savedAnchor = emojiConfig.Items.ElementAtOrDefault((int)(savedOffset / 39) * 6)?.Id;
        RefreshFullItems(); UpdateConfigurationIcons(); EnableActions(false);
        EditorStatus("编辑列表：点击待删/撤销，拖动排序，勾号保存、叉号取消。");
        if (emojiStore.RecoveryRequired) OpenEmojiInput(true);
    }
    void ToggleEmojiSettings(object sender, RoutedEventArgs e)
    { if (!EditingEmoji) BeginEmojiEdit(); else if (emojiPhase == EmojiEditPhase.Editing) CancelEmojiEdit(); }
    internal void SaveEmojiEdit()
    {
        if (emojiPhase != EmojiEditPhase.Editing || emojiDraft is null || IconPanel.Dragging) return;
        emojiPhase = EmojiEditPhase.Saving; EnableActions(false);
        try
        {
            var committed = SavePersonalSnapshot(emojiDraft.Commit());
            emojiConfig = committed; Interlocked.Increment(ref emojiGeneration);
            FinishEmojiEdit(); bar?.SetItems(emojiConfig.Items); EditorStatus("emoji 列表已保存。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { emojiPhase = EmojiEditPhase.Editing; EnableActions(false); EditorStatus("保存失败，草稿已保留：" + ex.Message, true); }
    }
    internal void CancelEmojiEdit()
    {
        if (emojiPhase != EmojiEditPhase.Editing) return;
        if (recoveringConfig) emojiStore.Load();
        IconPanel.CancelGesture(); FinishEmojiEdit(); EditorStatus("已取消草稿，原配置未改变。");
    }
    void FinishEmojiEdit()
    {
        CancelEditorPointers(); emojiDraft = null; emojiPhase = EmojiEditPhase.Normal;
        ApplyFullWindowActivationStyle();
        ++contextVersion; ++visualEpoch; Interlocked.Increment(ref emojiGeneration); current = null;
        RefreshFullItems(); UpdateConfigurationIcons(); FullOpenFeedback(OpenBlocked);
        FullEmojiViewport.UpdateLayout();
        if (savedAnchor is not null && fullEmojiButtons.ContainsKey(savedAnchor)) IconPanel.Reveal(savedAnchor); else FullEmojiViewport.ScrollTo(savedOffset, false);
        EnableActions(false);
        _ = Scan();
    }
    void CancelEditorPointers()
    {
        IconPanel.CancelGesture(); FullEmojiViewport.Stop();
        foreach (UIElement control in IconPanel.OrderedButtons.Cast<UIElement>().Concat(new UIElement[] { ClearButton, EmojiSettingsButton, OpenRenameButton, CompactToggle, PinToggle }))
            if (control.IsMouseCaptured) control.ReleaseMouseCapture();
        bar?.Motion.Interrupt("配置编辑切换；作废旧指针与延迟动作");
    }
    void OpenEmojiInput(bool recovery = false)
    {
        if (emojiPhase != EmojiEditPhase.Editing || IconPanel.Dragging || inputClosing) return;
        emojiPhase = EmojiEditPhase.Adding; recoveringConfig = recovery; composingEmoji = false;
        EmojiInputHeading.Text = recovery ? "配置需要恢复" : "添加一枚 emoji";
        EmojiInputText.Visibility = recovery ? Visibility.Collapsed : Visibility.Visible;
        EmojiInputPreview.Visibility = recovery ? Visibility.Collapsed : Visibility.Visible;
        EmojiInputText.Text = "";
        EmojiInputConfirm.Content = recovery ? "备份并恢复默认" : "添加";
        EmojiInputLayer.Visibility = Visibility.Visible; EmojiInputLayer.Opacity = 1;
        UpdateEmojiInput(); UpdateEmojiInputPlacement(); EnableActions(false);
        if (SystemParameters.ClientAreaAnimation)
            EmojiInputLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (closed || emojiPhase != EmojiEditPhase.Adding || inputClosing) return; Activate(); if (recovery) EmojiInputCancel.Focus(); else EmojiInputText.Focus(); });
    }
    void EmojiInputChanged(object sender, TextChangedEventArgs e) { if (emojiPhase == EmojiEditPhase.Adding) UpdateEmojiInput(); }
    void UpdateEmojiInput()
    {
        if (emojiPhase != EmojiEditPhase.Adding) return;
        if (recoveringConfig) { EmojiInputHint.Text = emojiStore.LoadError + "\n只有明确选择恢复才备份原件；取消不覆盖原配置。"; EmojiInputConfirm.IsEnabled = true; return; }
        var text = EmojiInputText.Text; bool valid = EmojiCatalog.TryIdentity(text, out var identity);
        var duplicate = emojiDraft?.Items.FirstOrDefault(i => EmojiCatalog.TryIdentity(i.Item.Token, out var k) && k == identity);
        EmojiInputHint.Text = duplicate is not null ? duplicate.Deleted ? "列表已有此项，确认将撤销其待删除状态。" : "列表已有此项或其资格变体，不能重复添加。" : EmojiCatalog.Explain(text);
        EmojiInputConfirm.Content = duplicate?.Deleted == true ? "恢复" : "添加";
        EmojiInputConfirm.IsEnabled = !composingEmoji && valid && (duplicate is null || duplicate.Deleted);
        EmojiInputPreview.Source = null;
        if (valid) try { EmojiInputPreview.Source = NativeEmoji.RenderFitted(text, panels.Scale); }
            catch { EmojiInputHint.Text = "系统字形渲染失败，无法预览；本次未添加。"; EmojiInputConfirm.IsEnabled = false; }
    }
    async void ConfirmEmojiInput(object sender, RoutedEventArgs e)
    {
        if (emojiPhase != EmojiEditPhase.Adding || inputClosing || composingEmoji || !EmojiInputConfirm.IsEnabled || emojiDraft is null) return;
        string? id = null;
        try
        {
            if (recoveringConfig) { emojiStore.PermitRecovery(); emojiDraft = new(PrefixSnapshot.Default); }
            else emojiDraft.Add(EmojiInputText.Text, out id!);
        }
        catch (Exception ex) { EmojiInputHint.Text = "操作失败：" + ex.Message; return; }
        await CloseEmojiInput(); if (closed) return; RefreshFullItems();
        if (id is not null) _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { IconPanel.Reveal(id); fullEmojiButtons[id].Focus(); });
    }
    async void CancelEmojiInput(object sender, RoutedEventArgs e) { if (emojiPhase == EmojiEditPhase.Adding && !inputClosing) await CloseEmojiInput(); }
    async Task CloseEmojiInput()
    {
        inputClosing = true; EmojiInputConfirm.IsEnabled = EmojiInputCancel.IsEnabled = false;
        if (SystemParameters.ClientAreaAnimation)
        { EmojiInputLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }); await Task.Delay(160); }
        if (closed) return;
        if (EmojiInputLayer.IsKeyboardFocusWithin) { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, null); }
        EmojiInputLayer.Visibility = Visibility.Collapsed; EmojiInputLayer.BeginAnimation(OpacityProperty, null);
        panels.ReserveOverlayBounds(null);
        emojiPhase = EmojiEditPhase.Editing; inputClosing = false; EmojiInputCancel.IsEnabled = true;
        EnableActions(false); addEmojiButton?.Focus();
    }
    internal void InstallEmojiFixture(IReadOnlyList<string> tokens)
    {
        if (demoHandle == 0 || TestDataRoot is null || EditingEmoji || operation) throw new InvalidOperationException("数量注入只允许隔离夹具。");
        emojiConfig = PrefixSnapshot.Default.WithItems(tokens.Select((t, i) => new EmojiItem("fixture-" + i, t)));
        emojiStore.Save(emojiConfig); Interlocked.Increment(ref emojiGeneration);
        RefreshFullItems(); bar!.SetItems(emojiConfig.Items); EnableActions(false);
    }
    internal void InjectEmojiSaveFailure(bool enabled)
    {
        if (demoHandle == 0 || TestDataRoot is null) throw new InvalidOperationException("故障注入只允许隔离夹具。");
        emojiStore.BeforeReplace = enabled ? _ => throw new IOException("夹具模拟替换失败") : null;
    }
    internal void StartReviewDiagnostics()
    {
        if (demoHandle == 0 || TestDataRoot is null) return;
        var inputs = new List<string>();
        PreviewMouseLeftButtonDown += (_, e) => { inputs.Add("鼠标按下：" + (e.OriginalSource as FrameworkElement)?.Name); if (inputs.Count > 20) inputs.RemoveAt(0); };
        PreviewKeyDown += (_, e) => { inputs.Add("按键：" + e.Key + "/" + e.SystemKey); if (inputs.Count > 20) inputs.RemoveAt(0); };
        var monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        monitor.Tick += (_, _) =>
        {
            if (closed) { monitor.Stop(); return; }
            var p = Mouse.GetPosition(this); var hit = InputHitTest(p) as DependencyObject;
            while (hit is not null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
            var summary = new { phase = emojiPhase.ToString(), fullHit = panels.FullContent.IsHitTestVisible, shellHit = panels.Shell.IsHitTestVisible,
                active = IsActive, operation, compact, native = panels.PhysicalBounds, mouse = p, hit = (hit as FrameworkElement)?.Name, settingsHover = EmojiSettingsButton.IsMouseOver,
                settingsScreen = EmojiSettingsButton.PointToScreen(new Point(15, 15)), focused = (Keyboard.FocusedElement as FrameworkElement)?.Name, inputs = inputs.ToArray() };
            File.WriteAllText(Path.Combine(TestDataRoot, "review-input.json"), System.Text.Json.JsonSerializer.Serialize(summary), new System.Text.UTF8Encoding(true));
        };
        monitor.Start(); Closed += (_, _) => monitor.Stop();
    }
}
