namespace CodexPrefix;

// 仅重放结构证据，不创建窗口、不连接 UIA，也不读写真实会话。
internal static class CompatibilityValidation
{
    internal static void Run(Action<bool, string> check)
    {
        bool Modal(bool same = true, bool verified = true, bool owned = true,
            string? css = "codex-dialog z-50", bool dialog = true, string? aria = "modal=true") =>
            Recognition.IsTrustedModalDocument(same, verified, owned, css, dialog, aria);

        check(!Recognition.IsTrustedHostDocument(true, false), "复现旧规则：模态隐藏背景后，同一主文档仍被拒绝");
        check(Modal(), "冷启动已打开弹窗：官方窗口、同一主文档及完整模态证据允许继续检查");
        check(Recognition.IsTrustedHostDocument(true, true), "未隐藏的背景结构仍使用原有宿主核验");
        check(!Modal(same: false), "嵌套网页仿造相同 modal、类名和标题仍须拒绝");
        check(!Modal(verified: false), "非官方程序仿造相同结构仍须拒绝");
        check(!Modal(owned: false), "同进程其他原生窗口不能借用当前目标");
        check(!Modal(dialog: false), "普通面板不能凭类名冒充对话框");
        foreach (var css in new string?[] { null, "", "codex-dialog-fake", "prefix-codex-dialog", "Codex-Dialog", "codex-dialogue" })
            check(!Modal(css: css), "应用模态类必须是完整且大小写一致的 CSS token");
        foreach (var aria in new string?[] { null, "", "modal=false", "notmodal=true", "modal=trueish", "label=modal=true", "modal=true;modal=false" })
            check(!Modal(aria: aria), "缺失、伪造或互相矛盾的模态属性必须拒绝");
        foreach (var aria in new[] { "modal=true", "modal:true", "modal = true;readonly=false", "readonly=false;modal=true", "modal=true;", "MODAL=TRUE" })
            check(Modal(css: " z-50\tcodex-dialog\ncompact ", aria: aria), "ARIA 属性分隔与 CSS 空白不应破坏结构识别");

        // 背景隐藏仅改变宿主证据，不能绕过后续输入范围校验。
        bool Accept(string title, int edits, int saves, bool complete) => Modal() &&
            Recognition.IsUnambiguousRenameScope(true, Recognition.IsRenameLabel(title), edits, saves, complete);
        check(Accept("重命名聊天", 1, 1, true), "当前包的中文对话框结构可识别");
        check(Accept("Rename chat", 1, 1, true), "当前包的英文对话框结构可识别");
        foreach (var title in new[] { "聊天标题", "Chat title", "搜索", "名称" })
            check(!Accept(title, 1, 1, true), "输入框标签不能替代对话框重命名语义");
        foreach (var edits in new[] { 0, 2, 3 }) check(!Accept("重命名聊天", edits, 1, true), "模态兼容不能绕过唯一输入框要求");
        foreach (var saves in new[] { 0, 2, 3 }) check(!Accept("重命名聊天", 1, saves, true), "模态兼容不能绕过唯一保存操作要求");
        check(!Accept("重命名聊天", 1, 1, false), "候选局部树截断必须拒绝");
        var expected = new EditorBinding((nint)41, 42, "scope", "edit", "save", "中文 ✌️ 空格");
        check(!Recognition.IsSameBinding(expected, expected with { ScopeId = "recreated" }), "文档或弹窗重建后不能复用原绑定");
        check(!Recognition.IsSameBinding(expected, expected with { OriginalValue = "用户已修改" }), "新模态入口仍须拒绝过期内容写入");
        // 2026-11-04 实测：DOM 的 aria-modal 未出现在 UIA AriaProperties，改由 WindowPattern 暴露。
        const string actualAria = "readonly=true;expanded=false;multiline=false;multiselectable=false;required=false;hasactions=false";
        bool NativeModal(string? aria, bool? windowModal, bool same = true, bool verified = true, bool owned = true, string css = "codex-dialog z-50", bool dialog = true) =>
            Recognition.IsTrustedModalDocument(same, verified, owned, css, dialog, aria, windowModal);
        check(!Modal(aria: actualAria), "复现 0.5.5 误拒绝：真实 ARIA 字符串不含 modal");
        check(NativeModal(actualAria, true), "真实 provider 的候选 WindowPattern.IsModal 证明模态");
        check(!NativeModal(actualAria, false) && !NativeModal(actualAria, null), "不支持或明确非模态不能当作模态");
        check(!NativeModal("modal=false", true), "明确 ARIA 非模态不能被 WindowPattern 覆盖");
        check(!NativeModal("modal=true;modal=false", true), "冲突属性拒绝");
        check(NativeModal("modal=true", null), "原有 ARIA 模态通路仍有效");
        check(!NativeModal(actualAria, true, same: false), "WindowPattern 模态不能越过嵌套文档边界");
        check(!NativeModal(actualAria, true, verified: false), "WindowPattern 模态不能跳过官方窗口核验");
        check(!NativeModal(actualAria, true, owned: false), "WindowPattern 模态不能借用另一 HWND");
        check(!NativeModal(actualAria, true, css: "codex-dialog-overlay"), "遮罩层不能借用模态模式");
        check(!NativeModal(actualAria, true, dialog: false), "模式不能替代候选对话框语义");
    }
}
