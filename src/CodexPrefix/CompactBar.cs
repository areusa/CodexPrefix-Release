using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CodexPrefix;

internal sealed class CompactBar : Window
{
    internal bool HideWithoutHost { get; set; }
    internal const double AttachedWidth = CompactMetrics.AttachedWidth, LostWidth = CompactMetrics.LostWidth, BarHeight = CompactMetrics.Height;
    readonly Border frame;
    readonly StackPanel icons;
    readonly EmojiViewport emojiViewport;
    readonly Func<string?, Task> renameAction;
    readonly Dictionary<string, Button> emojiButtons = [];
    double widthBudget = double.PositiveInfinity;
    internal Action<string>? LayoutNotice;
    readonly Button clear, restore, grip, openButton, exit;
    readonly StackPanel dragSlot;
    readonly object openIcon;
    internal Border Frame => frame;
    internal Window SurfaceWindow { get; private set; }
    internal Border MotionSurface { get; private set; }
    Func<TrackingNative.Rect>? hostBounds;
    internal Action<TrackingNative.Rect, double>? HostPlace;
    internal bool ViewActive { get; set; } = true;
    internal nint Handle => new WindowInteropHelper(SurfaceWindow).Handle;
    internal Rect StableInputBounds => SurfaceWindow == this ? new Rect(MotionPadX, MotionPadY, DesignWidth, BarHeight)
        : new Rect(Canvas.GetLeft(MotionSurface), Canvas.GetTop(MotionSurface), DesignWidth, BarHeight);
    internal new bool IsVisible => SurfaceWindow == this ? base.IsVisible : SurfaceWindow.IsVisible && ViewActive;
    internal void BindHost(Window host, Border surface, Func<TrackingNative.Rect> bounds)
    {
        // 原根容器不再承担整体动效，防止父子共用同一矩阵造成二次放大。
        if (!ReferenceEquals(MotionSurface, surface))
        {
            MotionSurface.ClearValue(RenderTransformProperty);
            MotionSurface.ClearValue(RenderTransformOriginProperty);
        }
        SurfaceWindow = host; MotionSurface = surface; hostBounds = bounds;
        Motion.ApplyPose();
    }
    internal new void Show() { if (SurfaceWindow == this) base.Show(); else SurfaceWindow.Show(); }
    internal new void Hide() { if (SurfaceWindow == this) base.Hide(); else SurfaceWindow.Hide(); }
    internal const double MotionPadX = 96, MotionPadY = 32;
    internal MiniMotionDriver Motion { get; private set; } = null!;
    internal double DesignWidth => EmojiLayout.MiniWidth(emojiButtons.Count, Attached) - EmojiLayout.MiniViewport(emojiButtons.Count) + EmojiViewportWidth;
    double EmojiViewportWidth => Math.Min(EmojiLayout.MiniViewport(emojiButtons.Count), Math.Max(0, widthBudget - EmojiLayout.MiniWidth(0, Attached) - (emojiButtons.Count == 0 ? 0 : 2)));
    internal EmojiViewport EmojiScroll => emojiViewport;
    Window? feedbackTip;
    TextBlock? tipText;
    bool feedbackActive;
    FeedbackContext? feedbackContext;
    WindowIdentity? tipTarget;
    string status = "请手动打开 Codex 的重命名框", tracking = "等待追踪 Codex";
    internal bool Attached { get; private set; }
    internal bool ActionsEnabled => icons.IsEnabled && (clear.IsEnabled || emojiButtons.Values.Any(b => b.IsEnabled));
    internal Button RestoreButton => restore;
    internal Button OpenButton => openButton;
    internal Button CloseButton => exit;
    internal IReadOnlyList<Button> ActionButtons => (Attached ? Enumerable.Empty<Button>() : new[] { grip }).Concat(new[] { openButton }).Concat(icons.Children.OfType<Button>()).Concat(new[] { clear, restore, exit }).ToArray();
    internal IReadOnlyList<Button> MotionButtons => (Attached ? Enumerable.Empty<Button>() : new[] { grip }).Concat(new[] { openButton }).Concat(icons.Children.OfType<Button>().Where((b, i) => i * 32 + 30 > emojiViewport.Offset && i * 32 < emojiViewport.Offset + EmojiViewportWidth)).Concat(new[] { clear, restore, exit }).ToArray();
    internal bool TipVisible => feedbackTip?.IsVisible == true;
    internal string TipText => tipText?.Text ?? "";
    internal Window? FeedbackTip => feedbackTip;
    internal event Action? Dragged;

    internal CompactBar(Func<string?, Task> rename, Action back, Action close, Func<Task>? open = null)
    {
        renameAction = rename;
        SurfaceWindow = this;
        Title = "会话前缀 · 小条";
        Icon = (ImageSource)Application.Current.FindResource("AppIcon");
        Width = LostWidth + MotionPadX * 2; Height = BarHeight + MotionPadY * 2; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = false;
        AllowsTransparency = true; Background = Brushes.Transparent;
        UseLayoutRounding = true; SnapsToDevicePixels = false;
        Left = 120; Top = 100;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        frame = new SmoothBorder { Width = LostWidth, Height = BarHeight, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(MotionPadX, MotionPadY, 0, 0), CornerRadius = new CornerRadius(CompactMetrics.FrameCorner), Background = (Brush)Application.Current.FindResource("BackgroundBrush"), BorderBrush = new SolidColorBrush(Color.FromRgb(52, 52, 58)), BorderThickness = new Thickness(CompactMetrics.FrameBorder), Padding = CompactMetrics.Padding, Child = row };
        MotionSurface = frame;
        Content = new Grid { ClipToBounds = false, Children = { frame } };
        grip = new Button { Style = (Style)Application.Current.FindResource("CompactActionButtonStyle"), Content = new TextBlock { Text = "⋮", FontSize = 18, Foreground = Brushes.Gray }, Cursor = Cursors.SizeAll, ToolTip = "未找到 Codex · 拖动小条" };
        System.Windows.Automation.AutomationProperties.SetName(grip, "丢失宿主时拖动小条");
        grip.PreviewMouseLeftButtonDown += (_, e) => { if (Attached) return; e.Handled = true; Motion.SetDragging(true); try { SurfaceWindow.DragMove(); } catch (InvalidOperationException) { } finally { Motion.SetDragging(false); } Dragged?.Invoke(); };
        dragSlot = new StackPanel { Orientation = Orientation.Horizontal };
        dragSlot.Children.Add(grip); dragSlot.Children.Add(new Border { Width = CompactMetrics.Gap });
        row.Children.Add(dragSlot);
        openButton = GlyphButton("EditGeometry", "打开当前会话的重命名框", async () => { if (open is not null) await open(); });
        openIcon = openButton.Content;
        row.Children.Add(openButton);
        icons = new StackPanel { Orientation = Orientation.Horizontal };
        emojiViewport = new EmojiViewport { Horizontal = true, Height = 30, Width = 190, Margin = new Thickness(2, 0, 0, 0), Child = icons };
        row.Children.Add(emojiViewport);
        row.Children.Add(new Border { Width = CompactMetrics.SeparatorSlot, Height = CompactMetrics.ActionSize, IsHitTestVisible = false, Child = new Border { Width = 1, Height = CompactMetrics.SeparatorHeight, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(58, 59, 66)) } });
        clear = GlyphButton("EraserGeometry", "清除前缀", async () => await rename(null));
        restore = GlyphButton("RestoreGeometry", "还原完整模式", () => Motion.ScheduleAction(back));
        exit = GlyphButton("CloseGeometry", "关闭工具", () => Motion.ScheduleAction(close), true);
        row.Children.Add(clear); row.Children.Add(new Border { Width = CompactMetrics.Gap });
        row.Children.Add(restore); row.Children.Add(new Border { Width = CompactMetrics.Gap }); row.Children.Add(exit);
        Motion = new MiniMotionDriver(this);
        emojiViewport.UserScrolling += () => Motion.Scroll();
        emojiViewport.OffsetChanged += () => Motion.Rebind();
        SetItems(PrefixSnapshot.Default.Items);
        Loaded += (_, _) => { RenderIcons(); Motion.Start(); };
        SourceInitialized += (_, _) => Native.RoundCorners(new WindowInteropHelper(this).Handle, 9);
        SizeChanged += (_, _) => Native.RoundCorners(new WindowInteropHelper(this).Handle, 9);
        IsVisibleChanged += (_, _) => { if (!IsVisible) { feedbackTip?.Hide(); Motion.Interrupt("小条隐藏"); } };
        Closed += (_, _) => { Motion.Dispose(); feedbackTip?.Close(); feedbackTip = null; };
        SetActions(false, false);
    }
    internal static Button GlyphButton(string geometry, string tip, Action click, bool filled = false)
    {
        // 保留 MingCute 的 24 单位画板与留白，不能把路径包围盒强行撑满显示尺寸。
        var color = new SolidColorBrush(Color.FromRgb(163, 163, 174));
        var path = new Path { Data = (Geometry)Application.Current.FindResource(geometry), Stroke = filled ? null : color, StrokeThickness = filled ? 0 : 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Fill = filled ? color : null, IsHitTestVisible = false, SnapsToDevicePixels = false };
        var canvas = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false, SnapsToDevicePixels = false };
        canvas.Children.Add(path);
        var button = new Button { Style = (Style)Application.Current.FindResource("CompactActionButtonStyle"), Content = new Viewbox { Width = CompactMetrics.GlyphSize, Height = CompactMetrics.GlyphSize, Child = canvas, IsHitTestVisible = false, SnapsToDevicePixels = false }, ToolTip = tip };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); if (IsLoaded) { Motion.Interrupt("DPI 重建"); RenderIcons(); Native.RoundCorners(new WindowInteropHelper(this).Handle, 9); } }
    internal void RenderIcons(double? renderScale = null)
    {
        foreach (var button in icons.Children.OfType<Button>())
        {
            string icon = (string)button.Tag;
            try { button.Content = new Image { Source = NativeEmoji.RenderFitted(icon, renderScale ?? VisualTreeHelper.GetDpi(frame).DpiScaleX), Width = CompactMetrics.EmojiSize, Height = CompactMetrics.EmojiSize, IsHitTestVisible = false }; }
            catch { button.Content = new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 19 }; }
        }
    }
    internal void SetAttached(bool attached)
    {
        Attached = attached; dragSlot.Visibility = attached ? Visibility.Collapsed : Visibility.Visible;
        Width = DesignWidth + MotionPadX * 2;
        Height = BarHeight + MotionPadY * 2;
        frame.Width = DesignWidth;
        Motion.Interrupt("宿主绑定变化");
        UpdateListWidth(); Motion.Rebind();
    }
    internal void FitWorkArea(double width) { if (Math.Abs(widthBudget - width) < .01) return; widthBudget = width; UpdateListWidth(); if (EmojiViewportWidth < EmojiLayout.MiniViewport(emojiButtons.Count)) LayoutNotice?.Invoke("工作区不足，mini emoji 视口已收窄；固定功能区保留。"); }
    void UpdateListWidth()
    {
        emojiViewport.Width = EmojiViewportWidth; emojiViewport.Margin = new Thickness(emojiButtons.Count == 0 ? 0 : 2, 0, 0, 0);
        frame.Width = DesignWidth;
        if (SurfaceWindow == this) Width = DesignWidth + MotionPadX * 2;
    }
    internal void SetItems(IReadOnlyList<EmojiItem> items)
    {
        Motion.Interrupt("配置列表发布");
        var anchor = icons.Children.OfType<Button>().ElementAtOrDefault((int)(emojiViewport.Offset / 32))?.Uid;
        foreach (var id in emojiButtons.Keys.Except(items.Select(i => i.Id)).ToArray()) emojiButtons.Remove(id);
        icons.Children.Clear();
        foreach (var item in items)
        {
            if (!emojiButtons.TryGetValue(item.Id, out var button))
            {
                button = new Button { Uid = item.Id, Style = (Style)Application.Current.FindResource("CompactActionButtonStyle"), Tag = item.Token, ToolTip = "设置前缀 " + item.Token };
                var own = button;
                button.Click += async (_, _) => { if (own.IsEnabled) await renameAction((string)own.Tag); };
                ToolTipService.SetShowOnDisabled(button, true);
                System.Windows.Automation.AutomationProperties.SetName(button, "设置前缀 " + item.Token);
                System.Windows.Automation.AutomationProperties.SetAutomationId(button, "MiniEmoji-" + item.Id);
                emojiButtons.Add(item.Id, button);
            }
            if (icons.Children.Count > 0) icons.Children.Add(new Border { Width = 2 });
            button.Tag = item.Token;
            icons.Children.Add(button);
        }
        UpdateListWidth(); RenderIcons();
        if (anchor is not null) { int index = items.ToList().FindIndex(i => i.Id == anchor); if (index >= 0) emojiViewport.ScrollTo(index * 32, false); }
        Motion.Rebind();
    }
    internal void SetItemStates(string? title, PrefixSnapshot snapshot)
    {
        foreach (var button in emojiButtons.Values) button.IsEnabled = title is not null && PrefixRules.CanChange(title, (string)button.Tag, snapshot);
        clear.IsEnabled = title is not null && PrefixRules.CanChange(title, null, snapshot);
    }
    // 对外几何始终指可见设计框；透明预留空间不参与宿主居中和浮层定位。
    internal TrackingNative.Rect VisualBounds
    {
        get
        {
            if (hostBounds is not null) return hostBounds();
            var hwnd = new WindowInteropHelper(this).Handle;
            var rect = TrackingNative.Bounds(hwnd);
            double scale = Math.Max(96, TrackingNative.GetDpiForWindow(hwnd)) / 96d;
            int x = rect.Left + (int)Math.Round(MotionPadX * scale), y = rect.Top + (int)Math.Round(MotionPadY * scale);
            return new(x, y, x + (int)Math.Round(DesignWidth * scale), y + (int)Math.Round(BarHeight * scale));
        }
    }
    internal void SetTrackingHint(string text) { tracking = text; frame.ToolTip = tracking + "\n" + status; }
    internal void SetActions(bool enabled, bool busy) { icons.IsEnabled = enabled; clear.IsEnabled = enabled; restore.IsEnabled = !busy; }
    internal void SetOpenEnabled(bool enabled) => openButton.IsEnabled = enabled;
    internal void OpenFeedback(bool failed, bool fade = false, string? detail = null, FeedbackContext? context = null)
    {
        if (fade)
        {
            // 渐隐只作用于当前可见内容，不重新初始化状态、不 Show 已隐藏的说明。
            feedbackContext?.BeginFade();
            var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            openButton.BeginAnimation(OpacityProperty, animation);
            if (feedbackTip?.IsVisible == true) feedbackTip.BeginAnimation(OpacityProperty, animation);
            return;
        }
        feedbackActive = failed;
        feedbackContext = failed ? context ?? new FeedbackContext(FeedbackContext.Foreground(), tipTarget) : null;
        ToolTipService.SetIsEnabled(frame, !failed);
        ToolTipService.SetIsEnabled(openButton, !failed);
        openButton.BeginAnimation(OpacityProperty, null);
        openButton.ClearValue(OpacityProperty);
        openButton.Style = (Style)Application.Current.FindResource(failed ? "CompactFeedbackButtonStyle" : "CompactActionButtonStyle");
        openButton.Content = failed ? new TextBlock { Text = "!", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : openIcon;
        openButton.ToolTip = failed ? "打开受阻，详情已写入本地日志；稍后自动恢复" : "打开当前会话的重命名框";
        if (failed)
        {
            if (feedbackTip is null)
            {
                var body = new StackPanel();
                body.Children.Add(new TextBlock { Text = "未能自动打开重命名框", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(255, 173, 180)), Margin = new Thickness(0, 0, 0, 5) });
                tipText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 226)) };
                body.Children.Add(tipText);
                feedbackTip = new Window { Title = "打开受阻说明", Owner = SurfaceWindow, Width = 310, Height = 92, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Focusable = false, IsHitTestVisible = false, Topmost = false, AllowsTransparency = true, Background = Brushes.Transparent, Content = new SmoothBorder { Background = new SolidColorBrush(Color.FromRgb(29, 29, 33)), Padding = new Thickness(12, 10, 12, 8), BorderBrush = new SolidColorBrush(Color.FromRgb(75, 59, 66)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Child = body } };
                feedbackTip.SourceInitialized += (_, _) =>
                {
                    var hwnd = new WindowInteropHelper(feedbackTip).Handle;
                    TrackingNative.SetWindowLongPtrW(hwnd, -20, (nint)(TrackingNative.GetWindowLongPtrW(hwnd, -20).ToInt64() | 0x080000A0));
                    Native.RoundCorners(hwnd, 9);
                };
            }
            tipText!.Text = detail ?? "请手动右键目标会话 → 重命名。3 秒后可重试；右侧可随时展开，详细错误已记录。";
            feedbackTip.BeginAnimation(OpacityProperty, null); feedbackTip.Opacity = 1;
            UpdateTip(tipTarget);
        }
        else feedbackTip?.Hide();
    }
    internal void UpdateTip(WindowIdentity? target)
    {
        tipTarget = target;
        if (feedbackTip is null) return;
        if (!feedbackActive || !IsVisible || feedbackContext?.Allows(target, FeedbackContext.Foreground()) != true) { feedbackTip.Hide(); return; }
        if (!feedbackTip.IsVisible && feedbackContext.Fading) return;
        if (!feedbackTip.IsVisible) feedbackTip.Show();
        var handle = Handle;
        var rect = VisualBounds;
        var work = TrackingNative.WorkArea(handle);
        double scale = Math.Max(96, TrackingNative.GetDpiForWindow(handle)) / 96d;
        int w = (int)Math.Round(310 * scale), h = (int)Math.Round(92 * scale);
        int x = Math.Clamp(rect.Left + (int)(6 * scale), work.Left, Math.Max(work.Left, work.Right - w));
        int y = rect.Bottom + (int)(6 * scale);
        if (y + h > work.Bottom) y = Math.Max(work.Top, rect.Top - h - (int)(6 * scale));
        var tipHandle = new WindowInteropHelper(feedbackTip).Handle;
        TrackingNative.SetWindowPos(tipHandle, 0, x, y, w, h, 0x14);
        Native.RoundCorners(tipHandle, 9);
    }
    internal void SetStatus(WorkStatus work)
    {
        status = work.Text + (work.Detail is null ? "" : "\n" + work.Detail);
        frame.ToolTip = tracking + "\n" + status;
        // 整个外框固定深灰；只有具体操作入口在受阻时展示强提示。
    }
}
