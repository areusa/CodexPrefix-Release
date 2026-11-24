using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.UIA3;

namespace CodexPrefix;

public sealed partial class CodexBridge(nint demoWindow, LocalLog log) : IDisposable
{
    readonly UIA3Automation automation = new();
    public nint OwnWindow { get; set; }
    internal bool TrackingMode { get; set; }
    internal WindowIdentity? TrackedTarget { get; set; }
    nint lastWindow;
    Scope? cached;
    readonly Dictionary<string, AutomationElement> blocked = new();
    string? lastIssue;
    DateTime nextMetadata;
    string? referenceTitle;
    string lastProbeLog = "";
    internal Action? DemoBeforeProbe { get; set; }
    sealed record Scope(AutomationElement Container, AutomationElement Editor, AutomationElement Save, EditorBinding Binding);
    sealed record TreeSlice(List<AutomationElement> Elements, bool Complete, long Milliseconds);

    nint ResolveWindow()
    {
        if (TrackingMode)
        {
            if (TrackedTarget is not { } target || !target.IsCurrent() || !Native.IsWindowVisible(target.Handle) || Native.IsIconic(target.Handle) || TrackingNative.Cloaked(target.Handle)) return 0;
            if (demoWindow == 0 && WindowIdentity.Read(target.Handle) != target) return 0;
            var active = Native.GetForegroundWindow();
            if (active != OwnWindow && !Native.BelongsToWindow(active, target.Handle)) return 0;
            if (lastWindow != target.Handle) { cached = null; referenceTitle = null; nextMetadata = DateTime.MinValue; }
            return lastWindow = target.Handle;
        }
        if (demoWindow != 0) return Native.IsWindow(demoWindow) ? demoWindow : 0;
        var foreground = Native.GetForegroundWindow();
        if (foreground == OwnWindow) return IsCodex(lastWindow) ? lastWindow : 0;
        if (!IsCodex(foreground)) return 0;
        var owner = Native.OwnerRoot(foreground);
        var result = IsCodex(owner) ? owner : foreground;
        if (lastWindow != result) { cached = null; referenceTitle = null; nextMetadata = DateTime.MinValue; }
        return lastWindow = result;
    }
    static bool IsCodex(nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return false;
        var cls = new StringBuilder(256);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString().Contains("Console", StringComparison.OrdinalIgnoreCase)) return false;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            return WindowIdentity.Read(hwnd) is not null;
        }
        catch { return false; }
    }
    static string Id(AutomationElement node) => string.Join(".", node.Properties.RuntimeId.Value);
    static string Name(AutomationElement node) => node.Properties.Name.ValueOrDefault ?? "";
    static string Class(AutomationElement node) => node.Properties.ClassName.ValueOrDefault ?? "";
    static bool Visible(AutomationElement node) => !node.Properties.IsOffscreen.ValueOrDefault;
    static bool Selected(AutomationElement element) =>
        (element.Patterns.SelectionItem.TryGetPattern(out var pattern) && pattern.IsSelected.Value) || Recognition.IsCurrentMarker(element.Properties.AriaProperties.ValueOrDefault);

    static bool IsSidebarRow(AutomationElement node) => node.Properties.ControlType.ValueOrDefault is ControlType.ListItem or ControlType.TreeItem || Class(node).Contains("sidebar-item", StringComparison.OrdinalIgnoreCase);
    // 只枚举条目结构，条目内部图标、文字及菜单延后到选中项内读取，避免装饰节点耗尽限额。
    TreeSlice SidebarRows(AutomationElement sidebar) => Slice(sidebar, 240, 650, 18, stopAt: IsSidebarRow);
    TreeSlice Slice(AutomationElement root, int limit, int milliseconds, int maxDepth = 18, bool stopDocuments = true, bool pruneContent = true, Func<AutomationElement, bool>? stopAt = null)
    {
        var nodes = new List<AutomationElement>();
        var queue = new Queue<(AutomationElement Node, int Depth)>();
        queue.Enqueue((root, 0));
        var watch = Stopwatch.StartNew();
        var complete = true;
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        while (queue.Count > 0)
        {
            if (nodes.Count >= limit || watch.ElapsedMilliseconds >= milliseconds) { complete = false; break; }
            var (node, depth) = queue.Dequeue();
            nodes.Add(node);
            var type = node.Properties.ControlType.ValueOrDefault;
            if (stopDocuments && depth > 0 && type == ControlType.Document) continue;
            var cls = pruneContent ? Class(node) : "";
            if (pruneContent && (cls.Contains("thread-scroll", StringComparison.OrdinalIgnoreCase) || cls.Contains("bili-", StringComparison.OrdinalIgnoreCase))) continue;
            if (depth > 0 && stopAt?.Invoke(node) == true) continue;
            var child = walker.GetFirstChild(node);
            if (depth >= maxDepth) { if (child is not null) complete = false; continue; }
            var siblings = 0;
            while (child is not null)
            {
                if (++siblings > limit || watch.ElapsedMilliseconds >= milliseconds) { complete = false; break; }
                queue.Enqueue((child, depth + 1));
                child = walker.GetNextSibling(child);
            }
        }
        return new TreeSlice(nodes, complete, watch.ElapsedMilliseconds);
    }

    List<AutomationElement> RelatedRoots(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var result = new List<AutomationElement> { automation.FromHandle(hwnd) };
        foreach (var popup in Native.OwnedWindows(hwnd, pid)) result.Add(automation.FromHandle(popup));
        return result;
    }
    bool IsDialog(AutomationElement node, nint main)
    {
        var role = node.Properties.AriaRole.ValueOrDefault;
        if (role == "dialog" || node.Properties.IsDialog.ValueOrDefault) return true;
        return node.Properties.ControlType.ValueOrDefault == ControlType.Window &&
            node.Properties.NativeWindowHandle.ValueOrDefault != main && Recognition.IsRenameLabel(Name(node));
    }
    Scope? InspectDialog(AutomationElement container, nint hwnd)
    {
        if (!Visible(container) || !container.Properties.IsEnabled.ValueOrDefault || !IsDialog(container, hwnd))
        { log.Write("识别拒绝-对话框", "候选不可见、禁用或不具备对话框语义。"); return null; }
        if (!IsHostScope(container, hwnd))
        { log.Write("识别拒绝-宿主", "候选未通过宿主文档或原生归属核验。"); return null; }
        var tree = Slice(container, 100, 650, 14, true, false);
        if (!tree.Complete) { lastIssue = "输入框读取不完整，请保存诊断"; log.Write("重命名框", "局部结构未完整读取，拒绝绑定"); return null; }
        var renameSemantic = Recognition.IsRenameLabel(Name(container)) || tree.Elements.Any(node =>
            (node.Properties.ControlType.ValueOrDefault == ControlType.Text || node.Properties.ControlType.ValueOrDefault == ControlType.Header)
            && Recognition.IsRenameLabel(Name(node)));
        if (!renameSemantic) { log.Write("识别拒绝-语义", "对话框没有明确的重命名标题。"); return null; }
        openDialogEvidence?.Add(Id(container));
        var edits = tree.Elements.Where(node => node.Properties.ControlType.ValueOrDefault == ControlType.Edit && Visible(node)
            && node.Properties.IsEnabled.ValueOrDefault && !node.Properties.IsPassword.ValueOrDefault
            && node.Patterns.Value.IsSupported && !node.Patterns.Value.Pattern.IsReadOnly.Value).ToList();
        // 原生“保存”在名称尚未改变时可以处于禁用状态；提交前再确认已启用。
        var saves = tree.Elements.Where(node => node.Properties.ControlType.ValueOrDefault == ControlType.Button && Visible(node)
            && Recognition.IsSaveLabel(Name(node)) && node.Patterns.Invoke.IsSupported).ToList();
        if (!Recognition.IsUnambiguousRenameScope(true, renameSemantic, edits.Count, saves.Count, tree.Complete))
        {
            lastIssue = edits.Count > 1 ? "重命名框含多个输入框，已禁用" : "未找到唯一编辑与保存操作，已禁用";
            log.Write("重命名框", $"编辑框数量={edits.Count}，保存操作数量={saves.Count}，拒绝歧义");
            return null;
        }
        var value = edits[0].Patterns.Value.Pattern.Value.Value;
        if (!Recognition.IsValidTitle(value)) { log.Write("识别拒绝-文本", "输入值不满足单行非空标题规则；未记录原文。"); return null; }
        var binding = new EditorBinding(hwnd, automation.FromHandle(hwnd).Properties.ProcessId.ValueOrDefault,
            Id(container), Id(edits[0]), Id(saves[0]), value);
        return new Scope(container, edits[0], saves[0], binding);
    }
    bool IsHostScope(AutomationElement container, nint hwnd)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var node = container;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (container.Properties.ProcessId.ValueOrDefault != pid) return false;
        // 独立原生 Window（含夹具）直接核对自身句柄；网页容器不得借祖先句柄走此分支。
        var containerHandle = container.Properties.NativeWindowHandle.ValueOrDefault;
        if (container.Properties.ControlType.ValueOrDefault == ControlType.Window && containerHandle != 0)
            return Native.BelongsToWindow(containerHandle, hwnd);
        for (var depth = 0; node is not null && depth < 28; depth++)
        {
            if (node.Properties.ProcessId.ValueOrDefault != pid) return false;
            if (node.Properties.ControlType.ValueOrDefault == ControlType.Document)
            {
                var main = MainDocument(automation.FromHandle(hwnd));
                if (main is null || Id(main) != Id(node))
                {
                    lastIssue = "已排除内置网页，请打开会话重命名";
                    log.Write("识别拒绝-文档", main is null ? "未完整定位主文档。" : "候选属于另一文档；不接管内置网页。");
                    return false;
                }
                // 当前原生模态框会对背景设 aria-hidden；不能把背景不可读当作弹窗不属于应用。
                // 冷启动也必须证明唯一主文档、官方窗口及原生祖先归属，不能凭类名接管内置网页。
                var verified = WindowIdentity.Read(hwnd) is not null;
                var native = node;
                bool ownedRoot = false;
                for (int parentDepth = 0; native is not null && parentDepth < 28; parentDepth++)
                {
                    if (native.Properties.ProcessId.ValueOrDefault != pid) break;
                    var handle = native.Properties.NativeWindowHandle.ValueOrDefault;
                    if (handle != 0) { ownedRoot = Native.BelongsToWindow(handle, hwnd); break; }
                    native = walker.GetParent(native);
                }
                // Chromium 将 aria-modal 映射到候选自己的 WindowPattern；ARIA 字符串可不含 modal。
                bool? nativeModal = null;
                if (container.Patterns.Window.TryGetPattern(out var modalPattern) && modalPattern.IsModal.TryGetValue(out var isModal)) nativeModal = isModal;
                var modal = Recognition.IsTrustedModalDocument(true, verified, ownedRoot, Class(container),
                    IsDialog(container, hwnd), container.Properties.AriaProperties.ValueOrDefault, nativeModal);
                if (modal) { log.Write("模态宿主核验", "官方窗口、唯一主文档、原生归属及应用模态结构通过；不依赖背景可见性。"); return true; }
                var shell = Slice(main, 220, 550, 16);
                var marker = shell.Elements.Any(element => Class(element).Contains("app-shell-left-panel", StringComparison.OrdinalIgnoreCase)
                    || Class(element).Contains("_MainContentSurface_", StringComparison.OrdinalIgnoreCase));
                if (!marker) log.Write("识别拒绝-应用结构", $"无可核验应用结构；官方窗口={verified};原生归属={ownedRoot};背景读取完整={shell.Complete}。");
                return Recognition.IsTrustedHostDocument(true, marker);
            }
            node = walker.GetParent(node);
        }
        return false;
    }
    Scope? FocusedDialog(nint hwnd)
    {
        var current = automation.FocusedElement();
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        for (var depth = 0; current is not null && depth < 24; depth++)
        {
            if (current.Properties.ProcessId.ValueOrDefault != pid)
            { log.Write("识别拒绝-焦点", "焦点祖先不属于目标进程，改用受限发现。"); return null; }
            if (IsDialog(current, hwnd))
            {
                // 同进程另一窗口的焦点不是当前目标，必须核对原生归属。
                var native = current;
                for (var parentDepth = 0; native is not null && parentDepth < 24; parentDepth++)
                {
                    var handle = native.Properties.NativeWindowHandle.ValueOrDefault;
                    if (handle != 0) return Native.BelongsToWindow(handle, hwnd) ? InspectDialog(current, hwnd) : null;
                    native = walker.GetParent(native);
                }
                return null;
            }
            current = walker.GetParent(current);
        }
        log.Write("识别拒绝-焦点", "焦点祖先中没有可核验对话框，改用受限发现。");
        return null;
    }
    // 到 Document 边界即停止，不进入内置浏览器网页或长对话正文。
    AutomationElement? MainDocument(AutomationElement root)
    {
        var shell = Slice(root, 100, 300, 12);
        var documents = shell.Elements.Where(x => x.Properties.ControlType.ValueOrDefault == ControlType.Document).ToArray();
        if (!shell.Complete || documents.Length != 1)
        {
            log.Write("主文档边界", $"完整={shell.Complete};顶层文档数量={documents.Length};不能任选文档。");
            return null;
        }
        return documents[0];
    }
    List<Scope> FindDialogs(nint hwnd)
    {
        var found = new List<Scope>();
        foreach (var root in RelatedRoots(hwnd))
        {
            if (IsDialog(root, hwnd))
            {
                var direct = InspectDialog(root, hwnd);
                if (direct is not null) found.Add(direct);
                continue;
            }
            var document = MainDocument(root);
            var slice = Slice(document ?? root, 260, 650, 18);
            foreach (var node in slice.Complete ? slice.Elements : [])
                if (IsDialog(node, hwnd))
                {
                    var scope = InspectDialog(node, hwnd);
                    if (scope is not null) found.Add(scope);
                }
            if (!slice.Complete) log.Write("局部探测", "受限读取已截断；只允许焦点直达或既有输入框，不进行自动会话绑定");
        }
        return found.GroupBy(x => x.Binding.Key).Select(x => x.First()).ToList();
    }

    void ReadMetadata(nint hwnd, Action<WorkStatus>? progress)
    {
        if (DateTime.UtcNow < nextMetadata) return;
        nextMetadata = DateTime.UtcNow.AddSeconds(8);
        progress?.Invoke(new(StatusKind.Processing, "正在读取主会话…"));
        var document = MainDocument(automation.FromHandle(hwnd));
        referenceTitle = document is null ? null : Name(document);
        log.Write("主会话", document is null ? "未取得主文档；真实会话 ID 未确认" : "已取得参考标题；无可信的当前会话 ID 来源，不进行标题映射");
        progress?.Invoke(new(StatusKind.Processing, "正在核对侧边栏…"));
        if (document is null) return;
        var structure = Slice(document, 180, 450, 14);
        var sidebar = structure.Elements.FirstOrDefault(x => Class(x).Contains("app-shell-left-panel", StringComparison.OrdinalIgnoreCase));
        if (sidebar is null) { log.Write("侧边栏", "未定位会话区域，转手动重命名模式"); return; }
        var rows = Slice(sidebar, 180, 450, 14);
        var selected = rows.Elements.Count(x => Class(x).Contains("sidebar-item", StringComparison.OrdinalIgnoreCase) && Selected(x));
        log.Write("侧边栏", $"局部读取节点={rows.Elements.Count}，完整={rows.Complete}，带选中标记的条目={selected}；未验证业务身份，禁用自动改名");
    }
    public ProbeResult Probe(Action<WorkStatus>? progress = null)
    {
        if (demoWindow != 0) DemoBeforeProbe?.Invoke();
        lastIssue = null;
        var hwnd = ResolveWindow();
        if (hwnd == 0)
        {
            cached = null;
            return new(null, null, new(StatusKind.NeedsAction, "请在 Codex 中打开重命名框"));
        }
        Scope? candidate = FocusedDialog(hwnd);
        if (candidate is null && cached is not null && cached.Binding.Window == hwnd)
        {
            try { candidate = InspectDialog(cached.Container, hwnd); } catch { cached = null; }
        }
        if (candidate is null)
        {
            ReadMetadata(hwnd, progress);
            var dialogs = FindDialogs(hwnd);
            if (dialogs.Count > 1) return new(null, referenceTitle, new(StatusKind.NeedsAction, "发现多个重命名框，请只保留一个"));
            candidate = dialogs.SingleOrDefault();
        }
        if (candidate is null)
        {
            cached = null;
            foreach (var pending in blocked.ToArray())
            {
                try { if (HasClosed(pending.Value)) blocked.Remove(pending.Key); }
                catch { /* 无法读取时保留封锁，不能把读取失败视为关闭。 */ }
            }
            LogProbe("等待手动打开原生重命名框");
            return new(null, referenceTitle, new(StatusKind.NeedsAction, lastIssue ?? "请先打开 Codex 的重命名框", "在目标会话上右键 → 重命名。若已经打开但仍禁用，请保存诊断。不会按同名标题查找。"));
        }
        cached = candidate;
        if (blocked.ContainsKey(candidate.Binding.Key))
            return new(null, referenceTitle, new(StatusKind.NeedsAction, "请先关闭此输入框，再重新打开", "上次操作已写入或尝试提交，禁止自动重复。请先检查原生界面。"));
        LogProbe("已绑定用户打开的唯一重命名框；不依赖会话标题或会话列表排序");
        return new(candidate.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀"));
    }
    void LogProbe(string message)
    {
        if (lastProbeLog == message) return;
        lastProbeLog = message; log.Write("识别结果", message);
    }
    internal static T? PollTransient<T>(Func<T?> read, Func<T, bool> accept, int timeoutMilliseconds, int delayMilliseconds, CancellationToken cancellation, Action<Exception>? onTransient = null) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { var result = read(); if (result is not null && accept(result)) return result; }
            catch (Exception ex) when (IsTransientOpenError(ex)) { onTransient?.Invoke(ex); }
            if (watch.ElapsedMilliseconds >= timeoutMilliseconds) return null;
            if (cancellation.WaitHandle.WaitOne(delayMilliseconds)) cancellation.ThrowIfCancellationRequested();
        }
    }
    Scope? ReadExpectedScope(EditorBinding expected, Scope reference)
    {
        Scope? found = null;
        try { found = InspectDialog(reference.Container, expected.Window); }
        catch (Exception ex) when (IsTransientOpenError(ex)) { }
        if (found is not null) return Recognition.IsSameBinding(expected, found.Binding) ? found : null;
        var dialogs = FindDialogs(expected.Window);
        if (dialogs.Count != 1 || !Recognition.IsSameBinding(expected, dialogs[0].Binding)) return null;
        return dialogs[0];
    }
    Scope Revalidate(EditorBinding expected, CancellationToken cancellation)
    {
        if (ResolveWindow() != expected.Window || cached is null) throw new InvalidOperationException("目标窗口已变化，请重新打开重命名框");
        var reference = cached;
        var current = PollTransient(() => ReadExpectedScope(expected, reference), _ => true, 600, 30, cancellation);
        if (current is null) throw new InvalidOperationException("输入框身份或内容已变化，本次未提交");
        return current;
    }
    internal RenameResult Rename(EditorBinding expected, string? icon, Action<WorkStatus> progress, CancellationToken cancellation = default, PrefixSnapshot? rules = null, Func<bool>? rulesCurrent = null)
    {
        progress(new(StatusKind.Processing, "正在核对输入框身份…"));
        var total = Stopwatch.StartNew();
        var scope = Revalidate(expected, cancellation);
        long validatedMs = total.ElapsedMilliseconds;
        rules ??= PrefixSnapshot.Default;
        if (rulesCurrent?.Invoke() == false) throw new InvalidOperationException("emoji 配置已经改变，本次未写入。");
        var newTitle = PrefixRules.Apply(expected.OriginalValue, icon, rules);
        if (!Recognition.IsValidTitle(newTitle)) throw new InvalidOperationException("处理后标题为空或无效，本次未提交");
        if (newTitle == expected.OriginalValue)
        {
            log.Write("重命名", "名称无需改变，未写入也未提交");
            return new(newTitle, new(StatusKind.Completed, "名称无需修改，可继续选择前缀"));
        }
        cancellation.ThrowIfCancellationRequested();
        if (rulesCurrent?.Invoke() == false) throw new InvalidOperationException("emoji 配置已经改变，本次未写入。");
        blocked[expected.Key] = scope.Container;
        progress(new(StatusKind.Processing, "正在修改名称…"));
        log.Write("重命名", "已校验窗口、输入框、保存控件及原始文本，开始写入");
        try { scope.Editor.Patterns.Value.Pattern.SetValue(newTitle); }
        catch (Exception ex) when (IsTransientOpenError(ex))
        { log.Write("写入瞬态", $"SetValue 已发送，禁止重放；错误码=0x{ex.HResult:X8}，仅核验回读。"); }
        var expectedAfter = expected with { OriginalValue = newTitle };
        var updated = PollTransient(() => ReadExpectedScope(expectedAfter, scope),
            candidate => candidate.Save.Properties.IsEnabled.ValueOrDefault, 1500, 30, cancellation,
            ex => log.Write("回读瞬态", $"错误码=0x{ex.HResult:X8}；只读重取，未重复写入。"));
        log.Write("改名时序", $"核验={validatedMs}ms;写入及回读={total.ElapsedMilliseconds - validatedMs}ms");
        if (updated is null || !Recognition.IsSameBinding(expectedAfter, updated.Binding))
            throw new InvalidOperationException("写入后内容或控件发生变化，未点击保存");
        if (!updated.Save.Properties.IsEnabled.ValueOrDefault) throw new InvalidOperationException("原生保存按钮仍不可用，未提交");
        if (ResolveWindow() != expected.Window) throw new InvalidOperationException("前台窗口已变化，未点击保存");
        cancellation.ThrowIfCancellationRequested();
        if (rulesCurrent?.Invoke() == false) throw new InvalidOperationException("emoji 配置已经改变，本次未保存。");
        progress(new(StatusKind.Processing, "正在提交原生重命名…"));
        updated.Save.Patterns.Invoke.Pattern.Invoke();
        progress(new(StatusKind.Processing, "正在确认提交状态…"));
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 1800)
        {
            if (HasClosed(updated.Container) || HasClosed(updated.Editor)) return Submitted(newTitle);
            Thread.Sleep(80);
        }
        log.Write("重命名", "已调用保存，但输入框未关闭；不自动重试");
        return new(newTitle, new(StatusKind.NeedsAction, "保存未确认，请检查原生输入框"));
    }
    RenameResult Submitted(string name)
    {
        cached = null;
        log.Write("重命名", "已调用保存且原输入框关闭；不声称已核实持久化会话名");
        return new(name, new(StatusKind.Submitted, "已提交，请核对原生会话名"));
    }
    static bool HasClosed(AutomationElement node)
    {
        try { return !Visible(node); }
        catch (ElementNotAvailableException) { return true; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80040201)) { return true; }
    }
    public string Diagnostic()
    {
        var output = new StringBuilder($"会话前缀 {typeof(App).Assembly.GetName().Version?.ToString(3)} · 局部只读诊断\n可能包含名称，仅本地保存。\n仅反映本次控件树；不按名称猜测会话，不等于持久化写入验收。\n");
        var hwnd = ResolveWindow();
        if (hwnd == 0) return output.AppendLine("没有可绑定的 Codex 窗口。先打开原生重命名框，再点击诊断。").ToString();
        void Dump(string label, AutomationElement root, int limit)
        {
            var slice = Slice(root, limit, 850, 18);
            output.AppendLine($"区域={label} 节点={slice.Elements.Count} 完整={slice.Complete} 耗时={slice.Milliseconds}ms");
            foreach (var node in slice.Elements)
            {
                var modal = node.Patterns.Window.TryGetPattern(out var windowPattern) && windowPattern.IsModal.TryGetValue(out var isModal)
                    ? isModal.ToString() : "未提供";
                output.AppendLine($"类型={node.Properties.ControlType.ValueOrDefault} | 标识={node.Properties.AutomationId.ValueOrDefault} | 名称={Name(node)} | 类={Class(node)} | 控件标识={Id(node)} | 角色={node.Properties.AriaRole.ValueOrDefault} | ARIA属性={node.Properties.AriaProperties.ValueOrDefault} | IsDialog={node.Properties.IsDialog.ValueOrDefault} | WindowPattern.IsModal={modal}");
            }
        }
        foreach (var root in RelatedRoots(hwnd))
        {
            Dump("原生窗口边界", root, 75);
            var document = MainDocument(root);
            if (document is not null)
            {
                Dump("主文档局部（不深入网页与正文）", document, 220);
                var shell = Slice(document, 260, 700, 18);
                foreach (var sidebar in shell.Elements.Where(node => Class(node).Contains("app-shell-left-panel", StringComparison.OrdinalIgnoreCase)))
                {
                    var rows = SidebarRows(sidebar);
                    output.AppendLine($"区域=侧边栏条目结构（不深入条目装饰） 节点={rows.Elements.Count} 完整={rows.Complete} 耗时={rows.Milliseconds}ms");
                    foreach (var node in rows.Elements.Where(IsSidebarRow))
                    {
                        output.AppendLine($"类型={node.Properties.ControlType.ValueOrDefault} | 类={Class(node)} | 选中={Selected(node)} | Aria={node.Properties.AriaProperties.ValueOrDefault} | 控件标识={Id(node)}");
                        if (Selected(node)) Dump("选中项内部的菜单能力", node, 60);
                    }
                }
            }
        }
        if (cached is not null) Dump("已绑定重命名框", cached.Container, 100);
        return output.ToString();
    }
    public void Dispose() => automation.Dispose();
}

internal static class Native
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    internal static void RoundCorners(nint hwnd, double radius = 14)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return;
        // 透明分层窗口由 WPF 的覆盖率抗锯齿描绘轮廓，不能再叠加二值 GDI 硬裁切。
        if ((TrackingNative.GetWindowLongPtrW(hwnd, -20).ToInt64() & 0x80000) != 0)
        {
            var noRound = 1; DwmSetWindowAttribute(hwnd, 33, ref noRound, sizeof(int));
            var previous = CreateRectRgn(0, 0, 0, 0);
            bool clipped = previous != 0 && GetWindowRgn(hwnd, previous) > 0;
            if (previous != 0) DeleteObject(previous);
            if (clipped) SetWindowRgn(hwnd, 0, true);
            return;
        }
        var value = 2; DwmSetWindowAttribute(hwnd, 33, ref value, sizeof(int));
        if (!TrackingNative.GetWindowRect(hwnd, out var bounds)) return;
        var diameter = (int)Math.Round(radius * 2 * Math.Max(96, TrackingNative.GetDpiForWindow(hwnd)) / 96d);
        var region = CreateRoundRectRgn(0, 0, bounds.Right - bounds.Left + 1, bounds.Bottom - bounds.Top + 1, diameter, diameter);
        var existing = CreateRectRgn(0, 0, 0, 0);
        bool same = existing != 0 && GetWindowRgn(hwnd, existing) > 0 && EqualRgn(existing, region);
        if (existing != 0) DeleteObject(existing);
        if (same) { DeleteObject(region); return; }
        if (region != 0 && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
        // 成功后 region 所有权交给系统；不再手工释放，防止重复释放。
    }
    [DllImport("gdi32.dll")] static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] static extern bool EqualRgn(nint first, nint second);
    [DllImport("user32.dll")] static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    delegate bool EnumWindowsProc(nint hwnd, nint parameter);
    internal static nint OwnerRoot(nint hwnd) => GetAncestor(hwnd, 3);
    internal static bool BelongsToWindow(nint hwnd, nint target)
    {
        for (var count = 0; hwnd != 0 && count < 16; count++)
        {
            if (hwnd == target || GetAncestor(hwnd, 2) == target) return true;
            hwnd = GetWindow(hwnd, 4);
        }
        return false;
    }
    internal static List<nint> OwnedWindows(nint target, uint processId)
    {
        var result = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd != target && IsWindowVisible(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == processId && BelongsToWindow(hwnd, target)) result.Add(hwnd);
            }
            return true;
        }, 0);
        return result;
    }
}
