namespace CodexPrefix;

public static class PrefixRules
{
    public static readonly string[] Icons = ["💦", "💭", "❓", "📄", "✌️", "○"];

    public static string Apply(string title, string? icon)
        => Apply(title, icon, PrefixSnapshot.Default);

    internal static string Apply(string title, string? icon, PrefixSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (icon is not null && !snapshot.Items.Any(i => i.Token == icon)) throw new ArgumentException("不支持的前缀");
        // 只识别完整的约定前缀加一个普通空格；正文及其他空白逐字保留。
        var body = title;
        foreach (var candidate in snapshot.History)
            if (title.StartsWith(candidate + " ", StringComparison.Ordinal))
            {
                body = title[(candidate.Length + 1)..];
                break;
            }
        return icon is null ? body : icon + " " + body;
    }

    internal static bool CanChange(string title, string? icon, PrefixSnapshot snapshot)
    {
        if (icon is not null && !snapshot.Items.Any(i => i.Token == icon)) return false;
        var result = Apply(title, icon, snapshot);
        return result != title && Recognition.IsValidTitle(result);
    }
}
