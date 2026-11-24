using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexPrefix;

public partial class MainWindow : Window
{
    readonly AutomationWorker worker;
    readonly LocalLog log;
    readonly CancellationTokenSource lifetime = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    readonly string settingsPath;
    readonly string dataFolder;
    readonly object fullOpenIcon;
    bool operation;
    bool scanning;
    bool closed;
    Task<ProbeResult>? scanTask;
    EditorBinding? current;
    DateTime messageUntil;
    string? holdBinding;
    long visualEpoch;
    nint ownHandle;
    WorkStatus? displayed;
    CompactBar? bar;
    WindowTracker? tracker;
    readonly nint demoHandle;
    bool compact;
    long contextVersion;
    WindowIdentity? previousTarget;
    ModeTransition? modeTransition;
    bool closingTransition;
    internal bool MotionTransitionActive => modeTransition is not null;
    internal string LastMotionTransition { get; private set; } = "未开始";
    internal int LateRecoveryCount { get; private set; }
    internal long LastLateProbeMilliseconds { get; private set; }
    internal CompactBar? ActiveBar => compact ? bar : null;
    internal WindowTracker? ActiveTracker => tracker;
    internal bool DemoLostHost { get; init; }
    internal bool OpenBlocked { get; private set; }
    internal bool IsWorking => operation;
    internal WorkStatus? CurrentStatus => displayed;
    internal EditorBinding? CurrentBinding => current;
    internal event Action<WorkStatus>? StatusChanged;
    internal Task ConfigureDemoOpen(Action<CodexBridge> configure)
    {
        if (demoHandle == 0) throw new InvalidOperationException("时序注入只允许独立夹具。");
        return worker.Run(bridge => { configure(bridge); return true; });
    }
    internal Task RefreshDemo()
    {
        if (demoHandle == 0) throw new InvalidOperationException("手动触发扫描只允许独立夹具。");
        return Scan();
    }
    internal async Task PauseDemoScanning()
    {
        if (demoHandle == 0) throw new InvalidOperationException("暂停自动扫描只允许独立夹具。");
        // 先让 Loaded 初始化完成，再停止定时器；等在途扫描退出后才安装故障注入。
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        timer.Stop();
        var wait = Stopwatch.StartNew();
        while (scanning && wait.ElapsedMilliseconds < 15000) await Task.Delay(20);
        if (scanning) throw new TimeoutException("夹具初始扫描未能结束，不能开始旧扫描重叠实验。");
    }
    string openBlockedDetail = "";
    FeedbackContext? openFeedbackContext;

    internal readonly bool isCustomDataDir;
    public MainWindow(MockWindow? mock) : this(mock, null) { }
    internal MainWindow(MockWindow? mock, string? customDataDir)
    {
        InitializeComponent();
        fullOpenIcon = OpenRenameButton.Content;
        isCustomDataDir = mock is null && !string.IsNullOrWhiteSpace(customDataDir);
        dataFolder = isCustomDataDir ? Path.GetFullPath(customDataDir!) : mock is not null && TestDataRoot is not null ? TestDataRoot : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPrefix", mock is null ? "" : "demo-v02");
        settingsPath = Path.Combine(dataFolder, "settings.json");
        log = new LocalLog(Path.Combine(dataFolder, "logs"));
        worker = new AutomationWorker(mock is null ? 0 : new WindowInteropHelper(mock).Handle, log);
        demoHandle = mock is null ? 0 : new WindowInteropHelper(mock).Handle;
        InitializeEmojiConfiguration();
        try
        {
            if (File.Exists(settingsPath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath, Encoding.UTF8));
                Topmost = settings?.Topmost ?? true;
                PinToggle.IsChecked = Topmost;
                defaultProfile = !emojiStore.Exists || string.Equals(settings?.ActiveProfile, "default", StringComparison.Ordinal);
                emojiConfig = ActiveProfileSnapshot(); RefreshFullItems();
            }
        }
        catch { log.Write("配置", "读取失败，使用默认置顶"); }
        InitializeLivePanels();
        SourceInitialized += (_, _) => { ownHandle = new WindowInteropHelper(this).Handle; Native.RoundCorners(ownHandle); HwndSource.FromHwnd(ownHandle).AddHook(EmojiWindowMessage); ApplyFullWindowActivationStyle(); };
        SizeChanged += (_, _) => Native.RoundCorners(ownHandle);
        timer.Tick += async (_, _) => await Scan();
        Loaded += async (_, _) =>
        {
            panels.ReserveInitialEnvelope();
            UpdateFullEmojiLayout();
            RenderEmoji();
            bar!.RenderIcons(); bar.Motion.Start();
            ShowStatus(new(StatusKind.Processing, "正在确认原生重命名框…"));
            if (emojiStore.LoadError is not null) ShowStatus(new(StatusKind.NeedsAction, "emoji 配置读取失败：点击设置可备份恢复", emojiStore.LoadError));
            InitializeUpdater();
            timer.Start();
            await Scan();
        };
        Closing += (_, e) =>
        {
            if (assistance is not null && !exitRequested) { e.Cancel = true; SuspendAssistance("用户关闭浮窗"); return; }
            tracker?.Dispose(); tracker = null;
        };
        Closed += (_, _) => { closed = true; DisposeUpdater(); DisposeAssistance(); modeTransition?.Finish("工具关闭"); lifetime.Cancel(); timer.Stop(); tracker?.Dispose(); tracker = null; bar?.Close(); bar = null; worker.Dispose(); if (mock is not null) mock.Close(); };
        RefreshProfileUi();
        EnableActions(false);
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) { IconPanel.CancelGesture(); InterruptTransition("DPI 重建"); bar?.Motion.Interrupt("DPI 重建"); if (!preparingMode && !panels.Relocating) UpdateFullEmojiLayout(); RenderEmoji(); bar?.RenderIcons(); Native.RoundCorners(ownHandle); }
    }
    void RenderEmoji()
    {
        // 无边框标题位置按实际 DPI 选取光学尺寸，而不是始终缩小同一张大图。
        if (TryFindResource("AppIcon") is BitmapFrame { Decoder: not null } appIcon)
        {
            var physicalSize = (int)Math.Ceiling(16 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            HeaderAppIcon.Source = appIcon.Decoder.Frames.FirstOrDefault(frame => frame.PixelWidth >= physicalSize) ?? appIcon.Decoder.Frames[^1];
        }
        RefreshFullEmojiVisuals();
    }
    Action<WorkStatus> Progress(long epoch) => status => Dispatcher.BeginInvoke(() =>
    {
        if (!closed && !EditingEmoji && epoch == visualEpoch) ShowStatus(status);
    });
    async Task<T> Observe<T>(Task<T> task, long epoch)
    {
        if (await Task.WhenAny(task, Task.Delay(3500)) != task && !closed && epoch == visualEpoch)
            ShowStatus(new(StatusKind.Processing, "原生界面响应较慢，正在等待…", "请求有等待上限；超时将终止隔离进程，不自动重试写入。"));
        return await task;
    }
    async Task Scan()
    {
        if (profileSwitching || IsUpdateBusy || !AssistanceAvailable || operation || OpenBlocked || scanning || closed || EditingEmoji || panels.Relocating) return;
        scanning = true;
        var epoch = ++visualEpoch;
        var version = contextVersion;
        var tracked = tracker?.Target;
        var useTracking = compact;
        var toolWindow = ownHandle;
        try
        {
            var initial = displayed is null || displayed.Kind == StatusKind.Processing;
            scanTask = worker.Probe(new(toolWindow, useTracking, tracked), initial ? Progress(epoch) : null, WorkToken);
            var result = await Observe(scanTask, epoch);
            if (closed || EditingEmoji || contextVersion != version || operation || OpenBlocked || epoch != visualEpoch) return;
            current = result.Binding;
            TargetText.Text = current?.OriginalValue ?? "等待绑定原生重命名框";
            TargetText.ToolTip = current?.OriginalValue ?? (result.ReferenceTitle is null ? "同名会话不会按标题匹配。" : "主会话参考标题（不作为身份依据）：" + result.ReferenceTitle);
            if (!operation)
            {
                if (DateTime.UtcNow > messageUntil || (current is not null && current.Key + "|" + current.OriginalValue != holdBinding)) ShowStatus(result.Status);
                EnableActions(current is not null);
            }
        }
        catch (Exception ex)
        {
            if (closed || EditingEmoji || contextVersion != version || operation || OpenBlocked || epoch != visualEpoch) return;
            current = null;
            EnableActions(false);
            log.Write("识别异常", ex.GetType().Name);
            if (!operation && DateTime.UtcNow > messageUntil)
                ShowStatus(new(StatusKind.NeedsAction, "请打开重命名框；仍不识别可诊断", ex.Message));
        }
        finally { scanning = false; }
    }
    async Task Rename(string? icon)
    {
        if (profileSwitching || IsUpdateBusy || !AssistanceAvailable || operation || OpenBlocked || EditingEmoji || panels.Relocating || current is null || (compact && (tracker?.CanOperate != true || current.Window != tracker.Target?.Handle))) return;
        if (!PrefixRules.CanChange(current.OriginalValue, icon, emojiConfig)) return;
        try { RegisterProfileHistory(emojiConfig); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { ShowStatus(new(StatusKind.Error, "历史登记未保存，本次未改名：" + ex.Message)); return; }
        var rules = emojiConfig; long generation = Interlocked.Read(ref emojiGeneration);
        var target = current;
        operation = true;
        EnableActions(false);
        var epoch = ++visualEpoch;
        ShowStatus(new(StatusKind.Processing, "正在核对输入框身份…"));
        var tracked = tracker?.Target;
        var useTracking = compact;
        var toolWindow = ownHandle;
        try
        {
            if (scanTask is not null) { try { await scanTask; } catch { } }
            var result = await Observe(worker.Rename(new(toolWindow, useTracking, tracked), target, icon, rules,
                () => AssistanceAvailable && Interlocked.Read(ref emojiGeneration) == generation, Progress(epoch), WorkToken), epoch);
            if (closed) return;
            ++visualEpoch;
            TargetText.Text = result.Name;
            ShowStatus(result.Status);
            current = null;
        }
        catch (Exception ex)
        {
            ++visualEpoch;
            current = null;
            log.Write("操作中止", ex.GetType().Name);
            if (!closed) ShowStatus(new(StatusKind.Error, ex is AutomationWriteUncertainException
                ? "写入结果待核对，请关闭原输入框后重开" : "操作已停止，请检查原生输入框", ex.Message));
        }
        finally
        {
            holdBinding = target.Key + "|" + target.OriginalValue;
            messageUntil = DateTime.UtcNow.AddSeconds(displayed?.Kind is StatusKind.Submitted or StatusKind.Completed ? 2 : 8);
            operation = false;
            EnableActions(false);
        }
        await Scan();
    }
    void EnableActions(bool enabled)
    {
        RefreshProfileUi();
        if (!AssistanceAvailable) { enabled = false; bar?.SetActions(false, false); bar?.SetOpenEnabled(false); return; }
        if (IsUpdateBusy || profileSwitching)
        {
            IconPanel.IsEnabled = EmojiSettingsButton.IsEnabled = ClearButton.IsEnabled = OpenRenameButton.IsEnabled = CompactToggle.IsEnabled = DiagnosticButton.IsEnabled = CloseToolButton.IsEnabled = false;
            bar?.SetActions(false, false); bar?.SetOpenEnabled(false); return;
        }
        if (EditingEmoji)
        {
            bool editable = emojiPhase == EmojiEditPhase.Editing;
            foreach (var b in fullEmojiButtons.Values) b.IsEnabled = editable;
            IconPanel.IsEnabled = editable; ClearButton.IsEnabled = EmojiSettingsButton.IsEnabled = editable && !IconPanel.Dragging;
            OpenRenameButton.IsEnabled = CompactToggle.IsEnabled = PinToggle.IsEnabled = DiagnosticButton.IsEnabled = CloseToolButton.IsEnabled = false;
            OpenRenameButton.BeginAnimation(OpacityProperty, null); OpenRenameButton.Opacity = .2; HeaderAppIcon.Opacity = .3;
            TargetText.Opacity = StatusBorder.Opacity = .3;
            bar?.SetActions(false, false); bar?.SetOpenEnabled(false); return;
        }
        TargetText.Opacity = StatusBorder.Opacity = HeaderAppIcon.Opacity = 1; PinToggle.IsEnabled = DiagnosticButton.IsEnabled = CloseToolButton.IsEnabled = true;
        EmojiSettingsButton.IsEnabled = !profileSwitching && !operation && modeTransition is null;
        enabled &= !operation && !OpenBlocked && !panels.Relocating && (!compact || (tracker?.CanOperate == true && current?.Window == tracker.Target?.Handle));
        IconPanel.IsEnabled = true;
        foreach (var button in fullEmojiButtons.Values)
        {
            button.IsEnabled = enabled && current is not null && PrefixRules.CanChange(current.OriginalValue, (string)button.Tag, emojiConfig);
            AutomationProperties.SetHelpText(button, enabled && !button.IsEnabled ? "当前前缀无需重复修改，或结果为空/超过标题长度限制。" : "");
        }
        ClearButton.IsEnabled = enabled && current is not null && PrefixRules.CanChange(current.OriginalValue, null, emojiConfig);
        CompactToggle.IsEnabled = !operation;
        bar?.SetActions(enabled, operation);
        bar?.SetItemStates(enabled ? current?.OriginalValue : null, emojiConfig);
        OpenRenameButton.IsEnabled = !operation && !OpenBlocked && !panels.Relocating && current is null;
        OpenRenameButton.Cursor = OpenRenameButton.IsEnabled ? Cursors.Hand : Cursors.Arrow;
        bar?.SetOpenEnabled(!operation && !OpenBlocked && !panels.Relocating && current is null);
    }
    async void ClickOpenRename(object sender, RoutedEventArgs e) => await RequestOpenRename();
    void FullOpenFeedback(bool failed, bool fade = false)
    {
        if (EditingEmoji) return;
        if (fade)
        {
            OpenRenameButton.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            return;
        }
        OpenRenameButton.BeginAnimation(OpacityProperty, null); OpenRenameButton.ClearValue(OpacityProperty);
        OpenRenameButton.Style = (Style)FindResource(failed ? "CompactFeedbackButtonStyle" : "CompactActionButtonStyle");
        OpenRenameButton.Content = failed ? new TextBlock { Text = "!", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White } : fullOpenIcon;
        OpenRenameButton.ToolTip = failed ? "打开受阻：" + openBlockedDetail : "打开当前会话的重命名框";
    }
    internal async Task RequestOpenRename()
    {
        if (profileSwitching || IsUpdateBusy || !AssistanceAvailable || operation || OpenBlocked || closed || EditingEmoji) return;
        while (panels.Relocating)
        {
            try { await panels.PlacementCompletion.WaitAsync(WorkToken); } catch (OperationCanceledException) { return; }
            if (profileSwitching || IsUpdateBusy || !AssistanceAvailable || operation || OpenBlocked || closed || EditingEmoji) return;
        }
        var origin = FeedbackContext.Foreground();
        var feedback = new FeedbackContext(origin, compact ? tracker?.Target : demoHandle != 0 ? WindowIdentity.Read(demoHandle, false) : origin is null ? null : WindowIdentity.Read(origin.Handle));
        operation = true; current = null; EnableActions(false);
        var epoch = ++visualEpoch;
        var version = contextVersion;
        var tracked = tracker?.Target;
        var tracking = compact;
        var toolWindow = ownHandle;
        ShowStatus(new(StatusKind.Processing, "正在打开重命名框…"));
        try
        {
            if (scanTask is not null) { try { await scanTask; } catch { } }
            current = null;
            var result = await Observe(worker.Open(new(toolWindow, tracking, tracked), Progress(epoch), WorkToken), epoch);
            if (closed || contextVersion != version) return;
            current = result.Binding;
            TargetText.Text = current?.OriginalValue ?? "等待绑定原生重命名框";
            ShowStatus(result.Status);
        }
        catch (Exception ex)
        {
            if (closed) return;
            ++visualEpoch; current = null; OpenBlocked = true;
            // 后台动作已结束。反馈冷却只封锁重试入口，不再锁住模式切换。
            operation = false;
            openBlockedDetail = OpenRenameBlockedException.Explain(ex is OpenRenameBlockedException blocked ? blocked.Code : "UIA_EXCEPTION");
            openFeedbackContext = feedback;
            log.Write("打开入口反馈", "已阻断并锁定入口 3 秒；错误类型=" + ex.GetType().Name + ";详细阶段和代码见打开重命名失败记录。");
            ShowStatus(new(StatusKind.Error, "打开受阻 · 请手动打开重命名框", openBlockedDetail));
            FullOpenFeedback(true);
            bar?.OpenFeedback(true, detail: openBlockedDetail, context: feedback);
            EnableActions(false);
            var feedbackClock = Stopwatch.StartNew();
            var recoveryTarget = (ex as OpenRenameBlockedException)?.RecoveryTarget;
            string requestId = (ex as OpenRenameBlockedException)?.RequestId ?? "";
            while (feedbackClock.ElapsedMilliseconds < 3000)
            {
                await Task.Delay(150);
                if (closed) return;
                if (recoveryTarget is null || (compact && tracker?.Target != recoveryTarget)) continue;
                try
                {
                    var probeClock = Stopwatch.StartNew();
                    var recovered = await worker.Recover(new(toolWindow, tracking, tracked), recoveryTarget, WorkToken);
                    if (closed) return;
                    if (EditingEmoji || recovered?.Binding is null || (compact && tracker?.Target != recoveryTarget) || !recoveryTarget.IsCurrent() || !Native.BelongsToWindow(Native.GetForegroundWindow(), recoveryTarget.Handle)) continue;
                    ++visualEpoch;
                    current = recovered.Binding; OpenBlocked = false;
                    TargetText.Text = current.OriginalValue;
                    StatusBorder.BeginAnimation(OpacityProperty, null); StatusBorder.Opacity = 1;
                    bar?.OpenFeedback(false);
                    FullOpenFeedback(false);
                    ShowStatus(recovered.Status);
                    LateRecoveryCount++;
                    LastLateProbeMilliseconds = probeClock.ElapsedMilliseconds;
                    log.Write("打开晚到恢复", $"请求={requestId};反馈后={feedbackClock.ElapsedMilliseconds}ms;本次只读核验={LastLateProbeMilliseconds}ms;同一宿主内只读识别成功，立即撤销提示；未补发按键或写入。");
                    return;
                }
                catch (Exception recoveryError)
                {
                    if (closed) return;
                    log.Write("打开晚到检查", $"请求={requestId};异常={recoveryError.GetType().Name};只读检查已停止。");
                    recoveryTarget = null;
                }
            }
            if (closed) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            if (!EditingEmoji) StatusBorder.BeginAnimation(OpacityProperty, fade);
            FullOpenFeedback(true, true);
            feedback.BeginFade();
            bar?.OpenFeedback(true, true, openBlockedDetail);
            await Task.Delay(200);
            if (closed) return;
            if (!EditingEmoji) { StatusBorder.BeginAnimation(OpacityProperty, null); StatusBorder.Opacity = 1; }
            bar?.OpenFeedback(false);
            FullOpenFeedback(false);
            ShowStatus(new(StatusKind.NeedsAction, "点击编辑图标打开 · 或手动打开重命名框", "自动打开尚未完成真实 Codex 兼容验收；受阻时请手动打开，不会按标题猜测。"));
        }
        finally
        {
            OpenBlocked = false; operation = false;
            openFeedbackContext = null;
            if (!closed) EnableActions(current is not null);
        }
    }
    void ToggleCompact(object sender, RoutedEventArgs e) => SetCompact(!compact);
    void TrackingChanged()
    {
        if (!compact || tracker is null) return;
        if (assistance is { Active: false }) { tracker.Dispose(); tracker = null; return; }
        if (previousTarget != tracker.Target)
        {
            previousTarget = tracker.Target; ++contextVersion; ++visualEpoch; current = null;
            log.Write("小条追踪", previousTarget is null ? "目标丢失，已禁用改名并展开拖动区" : "已核验并绑定原生窗口；不读取会话身份");
        }
        EnableActions(current is not null);
        bar?.UpdateTip(tracker.Target);
        if (!preparingMode && modeTransition is not null && tracker.VisualInvalidReason is { } reason) InterruptTransition(reason);
    }
    async void ClearPrefix(object sender, RoutedEventArgs e) { if (EditingEmoji) SaveEmojiEdit(); else await Rename(null); }
    void ShowStatus(WorkStatus status)
    {
        if (closed || (EditingEmoji && !writingEditorStatus) || status == displayed) return;
        displayed = status;
        StatusChanged?.Invoke(status);
        bar?.SetStatus(status);
        StatusText.Text = status.Text;
        StatusText.ToolTip = status.Detail ?? status.Text;
        StatusText.FontWeight = status.Kind is StatusKind.NeedsAction or StatusKind.Error ? FontWeights.SemiBold : FontWeights.Normal;
        var (foreground, background, border) = status.Kind switch
        {
            StatusKind.NeedsAction => ("#FFD38A", "#322718", "#84602E"),
            StatusKind.Ready => ("#B6E77C", "#202C1D", "#405332"),
            StatusKind.Completed => ("#B6E77C", "#202C1D", "#405332"),
            StatusKind.Error => ("#FFADB4", "#341F26", "#80424E"),
            _ => ("#ACD5FF", "#1D2834", "#385472")
        };
        var duration = new Duration(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 160 : 0));
        void Animate(SolidColorBrush brush, string target)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation((Color)ColorConverter.ConvertFromString(target), duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        Animate((SolidColorBrush)StatusText.Foreground, foreground);
        Animate((SolidColorBrush)StatusBorder.Background, background);
        Animate((SolidColorBrush)StatusBorder.BorderBrush, border);
        StatusActivity.Visibility = status.Kind == StatusKind.Processing ? Visibility.Visible : Visibility.Collapsed;
        if (SystemParameters.ClientAreaAnimation)
            StatusText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    void TogglePin(object sender, RoutedEventArgs e)
    {
        if (EditingEmoji) return;
        Topmost = PinToggle.IsChecked == true;
        try
        {
            Directory.CreateDirectory(dataFolder);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Settings(Topmost, defaultProfile ? "default" : "personal")), new UTF8Encoding(true));
        }
        catch { ShowStatus(new(StatusKind.NeedsAction, "置顶已切换，但设置未能保存")); }
    }
    void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (EditingEmoji) return;
        if (e.OriginalSource is Visual visual)
        {
            DependencyObject? node = visual;
            while (node is not null)
            {
                if (node is Button or CheckBox) return;
                node = VisualTreeHelper.GetParent(node);
            }
        }
        if (e.LeftButton == MouseButtonState.Pressed) { InterruptTransition("手动拖动"); bar?.Motion.SetDragging(true); try { DragMove(); } finally { bar?.Motion.SetDragging(false); } }
    }
    void CloseWindow(object sender, RoutedEventArgs e) => Close();
    void ShowLicenses(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (var resource in typeof(App).Assembly.GetManifestResourceNames().Where(x => x.Contains("NOTICES", StringComparison.OrdinalIgnoreCase) || x.Contains("LICENSE", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text.AppendLine(reader.ReadToEnd()).AppendLine();
        }
        var background = (Brush)Application.Current.FindResource("BackgroundBrush");
        new Window { Owner = this, Icon = Icon, Title = "第三方许可", Width = 640, Height = 500, Background = background, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = text.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = background, Foreground = Brushes.White, Padding = new Thickness(16) } }.ShowDialog();
    }
    async void SaveDiagnostic(object sender, RoutedEventArgs e)
    {
        if (operation || OpenBlocked || EditingEmoji) return;
        operation = true;
        EnableActions(false);
        var epoch = ++visualEpoch;
        ShowStatus(new(StatusKind.Processing, "正在保存本地诊断…"));
        try
        {
            if (scanTask is not null) { try { await scanTask; } catch { } }
            var text = await Observe(worker.Diagnostic(new(ownHandle, compact, tracker?.Target), WorkToken), epoch);
            var folder = Path.Combine(dataFolder, "diagnostics");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "uia-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            File.WriteAllText(path, text, new UTF8Encoding(true));
            ShowStatus(new(StatusKind.Completed, "诊断已保存 · 仅保存在本地", "文件可能包含会话名称，分享前请检查。\n" + path));
        }
        catch (Exception ex) { ShowStatus(new(StatusKind.Error, "诊断保存失败", ex.Message)); }
        finally { messageUntil = DateTime.UtcNow.AddSeconds(8); operation = false; await Scan(); }
    }
    record Settings(bool Topmost, string ActiveProfile = "personal");
}
