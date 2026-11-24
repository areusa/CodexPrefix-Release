using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.Exceptions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace CodexPrefix;

internal sealed class OpenRenameBlockedException(string code, string detail) : InvalidOperationException(detail)
{
    internal string Code { get; } = code;
    internal WindowIdentity? RecoveryTarget { get; init; }
    internal string RequestId { get; init; } = "";
    internal static string Explain(string code)
    {
        string reason = code switch
        {
            "SIDEBAR_TRUNCATED" or "ROW_TRUNCATED" => "侧边栏识别未完成，不能安全确认目标。",
            "SELECTION_UNPROVEN" or "SELECTION_CHANGED" => "未能确认唯一选中会话，不按名称猜测。",
            "MENU_BUTTON_UNPROVEN" or "RENAME_COMMAND_MISSING" or "RENAME_COMMAND_AMBIGUOUS" => "当前界面没有可确认的重命名菜单操作。",
            "FOREGROUND_BLOCKED" or "WINDOW_CHANGED" => "目标窗口未取得前台或已切换。",
            "MODIFIERS_HELD" => "请先松开键盘按键，再点击编辑按钮。",
            "SHORTCUT_BLOCKED" => "Windows 未允许发送重命名快捷键。",
            "EDITOR_BLOCKED" => "上次提交尚未确认，请关闭原输入框后重开。",
            "EDITOR_NOT_CONFIRMED" or "EDITOR_STABILIZING_TIMEOUT" => "尚未确认重命名框，仍在检查晚到的弹窗。",
            "MULTIPLE_EDITORS" => "存在多个输入范围，不能安全确认目标。",
            _ => "当前界面结构暂不兼容，自动打开已停止。"
        };
        return reason + "\n请手动右键会话 → 重命名；3 秒后可重试，右侧可展开。";
    }
}

public sealed partial class CodexBridge
{
    // 正式模式只触发当前聊天工具栏的原生菜单；快捷键只保留给旧时序夹具。
    internal Action<string, int>? DemoOpenProbe { get; set; }
    internal int DemoOpenWaitMilliseconds { get; set; } = 8000;
    HashSet<string>? openDialogEvidence;
    bool lastOpenHadDialog;
    internal static bool IsTransientOpenError(Exception ex) => ex is ElementNotAvailableException || ex is COMException { HResult: unchecked((int)0x80040201) };
    Scope? FindOpenEditor(nint hwnd)
    {
        var evidence = new HashSet<string>();
        openDialogEvidence = evidence;
        try
        {
        // 与常规识别使用同样的焦点祖先校验；全局局部扫描不能替代已证实的原生焦点范围。
        var focused = FocusedDialog(hwnd);
        var dialogs = FindDialogs(hwnd);
        if (focused is not null) dialogs.Add(focused);
        dialogs = dialogs.GroupBy(scope => scope.Binding.Key).Select(group => group.First()).ToList();
        if (dialogs.Count > 1 || evidence.Count > 1) throw new OpenRenameBlockedException("MULTIPLE_EDITORS", "存在多个重命名范围，拒绝猜测。");
        var editor = dialogs.SingleOrDefault();
        if (editor is not null && blocked.ContainsKey(editor.Binding.Key)) throw new OpenRenameBlockedException("EDITOR_BLOCKED", "已有未确认事务，必须关闭原输入框后重开，不能经打开入口解除封锁。");
        return editor;
        }
        finally { lastOpenHadDialog = evidence.Count > 0; openDialogEvidence = null; }
    }
    internal ProbeResult? RecoverOpen(WindowIdentity target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!target.IsCurrent() || !Native.BelongsToWindow(Native.GetForegroundWindow(), target.Handle)) return null;
        try
        {
            var editor = FindOpenEditor(target.Handle);
            cancellation.ThrowIfCancellationRequested();
            if (editor is null || !target.IsCurrent() || !Native.BelongsToWindow(Native.GetForegroundWindow(), target.Handle)) return null;
            cached = editor;
            return new(cached.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀"));
        }
        catch (Exception ex) when (IsTransientOpenError(ex)) { cached = null; return null; }
    }
    bool BelongsToDocument(AutomationElement element, AutomationElement document, nint hwnd)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var docId = Id(document);
        for (int depth = 0; element is not null && depth < 32; depth++, element = walker.GetParent(element))
        {
            if (element.Properties.ProcessId.ValueOrDefault != pid) return false;
            if (element.Properties.ControlType.ValueOrDefault == ControlType.Document) return Id(element) == docId;
        }
        return false;
    }
    List<AutomationElement> VisibleMenus(AutomationElement doc, nint hwnd) =>
        doc.FindAll(FlaUI.Core.Definitions.TreeScope.Descendants, automation.ConditionFactory.ByControlType(ControlType.Menu))
            .Where(n => Visible(n) && BelongsToDocument(n, doc, hwnd)).ToList();
    internal ProbeResult OpenFromTitleMenu(CancellationToken cancellation, Action<WorkStatus>? progress = null)
    {
        var watch = Stopwatch.StartNew();
        var hwnd = ResolveWindow();
        if (hwnd == 0 && !TrackingMode)
        {
            var candidates = WindowTracker.Candidates(h => WindowIdentity.Read(h)).Where(h => !Native.IsIconic(h) && !TrackingNative.Cloaked(h)).ToList();
            if (candidates.Count == 1) hwnd = candidates[0];
        }
        var identity = WindowIdentity.Read(hwnd) ?? throw new OpenRenameBlockedException("WINDOW_UNPROVEN", "请先激活唯一目标 Codex 窗口。");
        Native.SetForegroundWindow(hwnd);
        void Validate()
        {
            cancellation.ThrowIfCancellationRequested();
            if (!identity.IsCurrent() || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)
                || !Native.BelongsToWindow(Native.GetForegroundWindow(), hwnd))
                throw new OpenRenameBlockedException("WINDOW_CHANGED", "目标窗口或前台归属已变化，未继续操作。");
        }
        var focusWait = Stopwatch.StartNew();
        while (!Native.BelongsToWindow(Native.GetForegroundWindow(), hwnd) && focusWait.ElapsedMilliseconds < 500) Thread.Sleep(20);
        Validate(); lastWindow = hwnd;
        var existing = FindOpenEditor(hwnd);
        if (existing is not null) { cached = existing; return new(existing.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀")); }
        if (lastOpenHadDialog) throw new OpenRenameBlockedException("MULTIPLE_EDITORS", "已有重命名范围未就绪；不会另开菜单。");
        var doc = MainDocument(automation.FromHandle(hwnd)) ?? throw new OpenRenameBlockedException("MAIN_DOCUMENT_UNPROVEN", "未确认唯一主文档，未打开菜单。");
        var toolbars = doc.FindAll(FlaUI.Core.Definitions.TreeScope.Descendants, automation.ConditionFactory.ByControlType(ControlType.ToolBar))
            .Where(t => Visible(t) && Recognition.IsChatToolbar(Name(t)) && BelongsToDocument(t, doc, hwnd)).ToList();
        if (toolbars.Count != 1) throw new OpenRenameBlockedException("MENU_BUTTON_UNPROVEN", "未确认唯一聊天工具栏，未猜测标题或侧边栏。");
        var header = Slice(toolbars[0], 80, 500, 8, true, false);
        var buttons = header.Elements.Where(n => Visible(n) && n.Properties.IsEnabled.ValueOrDefault && n.Properties.ControlType.ValueOrDefault == ControlType.Button
            && n.Patterns.Invoke.IsSupported && Recognition.IsHeaderMenuButton(n.Properties.AriaProperties.ValueOrDefault)).ToList();
        if (!header.Complete || buttons.Count != 1) throw new OpenRenameBlockedException("MENU_BUTTON_UNPROVEN", "聊天工具栏未暴露唯一标题菜单按钮。");
        if (VisibleMenus(doc, hwnd).Count != 0) throw new OpenRenameBlockedException("MENU_ALREADY_OPEN", "请先关闭当前菜单，再点击编辑入口。");
        Validate();
        buttons[0].Patterns.Invoke.Pattern.Invoke();
        log.Write("原生菜单打开", "已触发唯一聊天工具栏菜单；正式入口未发送快捷键。");
        var menu = PollTransient(() =>
        {
            Validate();
            var menus = VisibleMenus(doc, hwnd);
            if (menus.Count > 1) throw new OpenRenameBlockedException("RENAME_COMMAND_AMBIGUOUS", "存在多个菜单，拒绝猜测。");
            return menus.SingleOrDefault();
        }, _ => true, 1500, 30, cancellation);
        if (menu is null) throw new OpenRenameBlockedException("RENAME_COMMAND_MISSING", "标题菜单未就绪；请手动打开重命名框。");
        var commands = Slice(menu, 70, 500, 6, true, false);
        var rename = commands.Elements.Where(n => n.Properties.ControlType.ValueOrDefault == ControlType.MenuItem && Visible(n)
            && n.Properties.IsEnabled.ValueOrDefault && Recognition.IsRenameMenuLabel(Name(n)) && n.Patterns.Invoke.IsSupported).ToList();
        if (!commands.Complete || rename.Count != 1) throw new OpenRenameBlockedException("RENAME_COMMAND_AMBIGUOUS", "菜单内没有唯一可用重命名项。");
        Validate(); rename[0].Patterns.Invoke.Pattern.Invoke();
        var found = PollTransient(() => { Validate(); return FindOpenEditor(hwnd); }, _ => true, 4000, 50, cancellation,
            ex => log.Write("菜单后瞬态", $"错误码=0x{ex.HResult:X8}；只重新识别原生弹窗。"));
        if (found is null) throw new OpenRenameBlockedException("EDITOR_NOT_CONFIRMED", "原生弹窗尚未就绪，未写入名称。") { RecoveryTarget = identity };
        cached = found;
        log.Write("原生菜单打开", $"标题菜单到原生弹窗成功；耗时={watch.ElapsedMilliseconds}ms；未写入名称。");
        return new(found.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀"));
    }
    internal ProbeResult OpenRename(CancellationToken cancellation, Action<WorkStatus>? progress = null)
    {
        if (demoWindow == 0) return OpenFromTitleMenu(cancellation, progress);
        string stage = "核验目标窗口";
        string requestId = Guid.NewGuid().ToString("N")[..8];
        var elapsed = Stopwatch.StartNew();
        void Trace(string text) => log.Write("打开时序", $"请求={requestId};耗时={elapsed.ElapsedMilliseconds}ms;阶段={stage};{text}");
        Trace("开始；尚未发送快捷键。");
        try
        {
            nint hwnd = ResolveWindow();
            if (hwnd == 0 && !TrackingMode && demoWindow == 0)
            {
                var candidates = WindowTracker.Candidates(h => WindowIdentity.Read(h)).Where(h => !Native.IsIconic(h) && !TrackingNative.Cloaked(h)).ToList();
                if (candidates.Count == 1) hwnd = candidates[0];
            }
            if (hwnd == 0) throw new OpenRenameBlockedException("WINDOW_UNPROVEN", "没有唯一且经过核验的目标窗口；请先激活目标 Codex。");
            var identity = WindowIdentity.Read(hwnd, demoWindow == 0) ?? throw new OpenRenameBlockedException("IDENTITY_LOST", "目标进程身份读取失败。");
            cancellation.ThrowIfCancellationRequested();
            if (!Native.BelongsToWindow(Native.GetForegroundWindow(), hwnd))
            {
                Native.SetForegroundWindow(hwnd);
                var activation = Stopwatch.StartNew();
                while (!Native.BelongsToWindow(Native.GetForegroundWindow(), hwnd) && activation.ElapsedMilliseconds < 400)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!identity.IsCurrent()) break;
                    Thread.Sleep(20);
                }
            }
            void Validate()
            {
                cancellation.ThrowIfCancellationRequested();
                if (!identity.IsCurrent())
                    throw new OpenRenameBlockedException("WINDOW_CHANGED", "目标窗口句柄或进程身份已变化，未继续操作。");
                if (!Native.BelongsToWindow(Native.GetForegroundWindow(), hwnd))
                    throw new OpenRenameBlockedException("WINDOW_CHANGED", "目标窗口身份仍有效，但前台归属已变化，未继续操作。");
            }
            if (!Native.BelongsToWindow(Native.GetForegroundWindow(), hwnd)) throw new OpenRenameBlockedException("FOREGROUND_BLOCKED", "Windows 未允许目标窗口取得前台，未发送快捷键。");
            lastWindow = hwnd;
            stage = "核验已有重命名框";
            Scope? ReadEditor(string phase, int attempt)
            {
                if (demoWindow != 0) DemoOpenProbe?.Invoke(phase, attempt);
                var editor = FindOpenEditor(hwnd);
                Validate();
                return editor;
            }
            Scope? existing;
            var preflight = Stopwatch.StartNew();
            int attempt = 0;
            while (true)
            {
                Validate();
                try
                {
                    existing = ReadEditor("发送前", ++attempt);
                    if (existing is not null || !lastOpenHadDialog) break;
                    if (preflight.ElapsedMilliseconds >= 2000) throw new OpenRenameBlockedException("EDITOR_STABILIZING_TIMEOUT", "已有重命名范围尚不完整或存在歧义，未发送快捷键。") { RecoveryTarget = identity, RequestId = requestId };
                    progress?.Invoke(new(StatusKind.Processing, "已有弹窗尚未就绪，正在确认…"));
                    if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested();
                }
                catch (Exception ex) when (IsTransientOpenError(ex))
                {
                    cached = null;
                    Trace($"瞬态控件失效;尝试={attempt};HResult=0x{ex.HResult:X8};重新获取原生控件。");
                    if (preflight.ElapsedMilliseconds >= 2000) throw new OpenRenameBlockedException("EDITOR_STABILIZING_TIMEOUT", "发送前界面持续重建，未发送快捷键。") { RecoveryTarget = identity, RequestId = requestId };
                    progress?.Invoke(new(StatusKind.Processing, "原生界面正在更新，等待稳定…"));
                    if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested();
                }
            }
            if (existing is not null) { cached = existing; return new(cached.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀")); }

            stage = "发送当前聊天的官方默认重命名快捷键";
            Validate();
            if (!RenameShortcut.KeysReleased()) throw new OpenRenameBlockedException("MODIFIERS_HELD", "检测到修饰键或 R 键尚未松开，未注入按键。");
            Validate();
            if (!RenameShortcut.Send()) throw new OpenRenameBlockedException("SHORTCUT_BLOCKED", "SendInput 未完成整组按键，已尝试释放注入按键；未继续操作。");
            log.Write("打开重命名", "已向核验后的前台宿主发送 Ctrl+Alt+R；尚未确认输入框，未写入名称。");
            Trace("快捷键已发送；本请求不再补发按键。");
            stage = "确认原生输入框已打开";
            var settle = Stopwatch.StartNew();
            attempt = 0;
            var waitMilliseconds = demoWindow != 0 ? DemoOpenWaitMilliseconds : 8000;
            while (settle.ElapsedMilliseconds < waitMilliseconds)
            {
                Validate();
                Scope? editor = null;
                try { editor = ReadEditor("发送后", ++attempt); }
                catch (Exception ex) when (IsTransientOpenError(ex))
                {
                    cached = null;
                    Trace($"瞬态控件失效;尝试={attempt};HResult=0x{ex.HResult:X8};仅重新识别，不判失败。");
                    progress?.Invoke(new(StatusKind.Processing, "弹窗正在更新，等待输入框就绪…"));
                }
                if (editor is not null)
                {
                    Validate();
                    cached = editor;
                    log.Write("打开重命名", "快捷键后已绑定唯一且语义明确的原生重命名框；未写入任何名称。");
                    Trace($"确认成功;发送后等待={settle.ElapsedMilliseconds}ms;尝试={attempt}。");
                    return new(cached.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀"));
                }
                if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested();
            }
            Trace($"等待上限到达;发送后等待={settle.ElapsedMilliseconds}ms;转入只读晚到检查。");
            throw new OpenRenameBlockedException("EDITOR_NOT_CONFIRMED", "已完成输入框就绪等待，暂未确认；将只读检查晚到弹窗，不补发快捷键或写入。") { RecoveryTarget = identity, RequestId = requestId };
        }
        catch (Exception ex)
        {
            string code = ex is OpenRenameBlockedException blocked ? blocked.Code : "UIA_EXCEPTION";
            string detail = ex is OpenRenameBlockedException ? ex.Message : "系统可访问性调用异常，未继续操作。";
            log.Write("打开重命名失败", $"请求={requestId};耗时={elapsed.ElapsedMilliseconds}ms;阶段={stage};代码={code};原因={detail};异常={ex.GetType().FullName};HResult=0x{ex.HResult:X8};调用栈={new StackTrace(ex, false)}");
            throw;
        }
    }
}

internal static class RenameShortcut
{
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputData { [FieldOffset(0)] public Keyboard Keyboard; [FieldOffset(0)] public Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Mouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    internal static Input[] Sequence() => new[] { Key(0x11), Key(0x12), Key(0x52), Key(0x52, true), Key(0x12, true), Key(0x11, true) };
    static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0u } } };
    internal static bool KeysReleased() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x52 }.All(key => (GetAsyncKeyState(key) & 0x8000) == 0);
    internal static bool Send()
    {
        var input = Sequence();
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) == input.Length) return true;
        // 部分注入失败时只释放本组按键，不补发按下，也不尝试第二次打开。
        var release = new[] { Key(0x52, true), Key(0x12, true), Key(0x11, true) };
        SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
        return false;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
}
