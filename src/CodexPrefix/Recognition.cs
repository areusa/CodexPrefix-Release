namespace CodexPrefix;

public enum StatusKind { Processing, NeedsAction, Ready, Completed, Submitted, Error }
public sealed record WorkStatus(StatusKind Kind, string Text, string? Detail = null);
public sealed record EditorBinding(nint Window, int ProcessId, string ScopeId, string EditorId, string SaveId, string OriginalValue)
{
    public string Key => $"{Window}/{ProcessId}/{ScopeId}/{EditorId}/{SaveId}";
}
public sealed record ProbeResult(EditorBinding? Binding, string? ReferenceTitle, WorkStatus Status);
public sealed record RenameResult(string Name, WorkStatus Status);

public static class Recognition
{
    public static bool IsSameBinding(EditorBinding expected, EditorBinding actual) =>
        expected.Window == actual.Window && expected.ProcessId == actual.ProcessId &&
        expected.ScopeId == actual.ScopeId && expected.EditorId == actual.EditorId &&
        expected.SaveId == actual.SaveId && expected.OriginalValue == actual.OriginalValue;
    public static bool IsRenameLabel(string? label) => RenameLabels.Contains((label ?? "").Trim().TrimEnd('.', '…'), StringComparer.OrdinalIgnoreCase);
    public static bool IsSaveLabel(string? label) => SaveLabels.Contains((label ?? "").Trim().TrimEnd('.', '…'), StringComparer.OrdinalIgnoreCase);
    public static bool IsMenuLabel(string? label) => new[] { "More", "More options", "More actions", "Thread actions", "Conversation options", "Open menu", "更多", "更多选项", "更多操作", "会话操作", "聊天操作", "打开菜单", "打开会话菜单", "⋯", "..." }.Contains((label ?? "").Trim(), StringComparer.OrdinalIgnoreCase);
    internal static bool IsChatToolbar(string? name) => new[] { "聊天工具栏", "Chat toolbar", "Conversation toolbar" }.Contains(name?.Trim(), StringComparer.OrdinalIgnoreCase);
    internal static bool IsHeaderMenuButton(string? aria) => System.Text.RegularExpressions.Regex.IsMatch(aria ?? "", @"(?:^|[;,\s])haspopup\s*[=:]\s*menu(?:$|[;,\s])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    internal static bool IsRenameMenuLabel(string? name) => new[] { "重命名", "Rename", "Rename chat", "Rename conversation" }.Contains(name?.Trim().TrimEnd('…'), StringComparer.OrdinalIgnoreCase);
    public static bool IsValidTitle(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048 && !value.Contains('\r') && !value.Contains('\n');
    public static bool IsUnambiguousRenameScope(bool dialog, bool renameMeaning, int edits, int saves, bool complete) =>
        dialog && renameMeaning && edits == 1 && saves == 1 && complete;
    public static bool IsTrustedHostDocument(bool sameDocument, bool shellMarker) => sameDocument && shellMarker;
    internal static bool IsTrustedModalDocument(bool sameDocument, bool verifiedWindow, bool ownedNativeRoot,
        string? className, bool dialog, string? ariaProperties, bool? windowModal = null) =>
        sameDocument && verifiedWindow && ownedNativeRoot && dialog &&
        (className ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("codex-dialog", StringComparer.Ordinal) &&
        (windowModal == true || System.Text.RegularExpressions.Regex.IsMatch(ariaProperties ?? "", @"(?:^|[;,\s])modal\s*[=:]\s*true(?:$|[;,\s])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) &&
        !System.Text.RegularExpressions.Regex.IsMatch(ariaProperties ?? "", @"(?:^|[;,\s])modal\s*[=:]\s*false(?:$|[;,\s])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    public static bool IsCurrentMarker(string? aria) => System.Text.RegularExpressions.Regex.IsMatch(aria ?? "", @"(?:^|[;,\s])(?:selected\s*[=:]\s*true|current\s*[=:]\s*(?:true|page))(?:$|[;,\s])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // 不能使用泛化的“名称”或输入框文本推测重命名语义。
    static readonly string[] RenameLabels = ["Rename", "Rename chat", "Rename thread", "Rename conversation", "Edit title", "重命名", "重命名会话", "重命名聊天", "为聊天重命名", "修改聊天名称", "编辑标题"];
    static readonly string[] SaveLabels = ["Save", "Save changes", "Confirm", "Rename", "Done", "保存", "保存更改", "确定", "确认", "重命名", "完成"];
}
