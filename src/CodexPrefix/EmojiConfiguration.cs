using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexPrefix;

// 原始 token 负责写入，资格变体身份只用于去重；历史识别始终精确匹配。
internal static class EmojiCatalog
{
    internal const string Version = "17.0";
    internal const string DataHash = "1D8A944F88D7952F7EF7C5167FEF3C67995BCAE24543949710231B03A201ACDA";
    static readonly Dictionary<string, string> identities = Load();
    static Dictionary<string, string> Load()
    {
        using var stream = typeof(EmojiCatalog).Assembly.GetManifestResourceStream("CodexPrefix.Unicode.emoji-test.txt")!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        if (Convert.ToHexString(SHA256.HashData(bytes.ToArray())) != DataHash) throw new InvalidDataException("内嵌 emoji 数据校验失败。");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? canonical = null;
        foreach (var line in Encoding.UTF8.GetString(bytes.ToArray()).Split('\n'))
        {
            if (line.StartsWith('#') || !line.Contains(';')) continue;
            var fields = line.Split(';', 2);
            var status = fields[1].Split('#')[0].Trim();
            var token = string.Concat(fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ConvertFromUtf32(int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
            if (status == "fully-qualified") canonical = token;
            if (status is "fully-qualified" or "minimally-qualified" or "unqualified") result.Add(token, canonical ?? token);
        }
        result.Add("○", "○");
        return result;
    }
    internal static bool TryIdentity(string? token, out string identity)
    {
        identity = "";
        return token is not null && identities.TryGetValue(token, out identity!);
    }
    internal static IReadOnlyList<string> Samples => identities.Keys.ToArray();
    internal static string Explain(string text) => text.Length == 0 ? "请输入一枚 emoji；可使用 Win + . 打开系统选择器。"
        : TryIdentity(text, out _) ? "合法序列。预览使用本机字体；新版本组合可能显示为多个图形，请确认可辨识。"
        : "只接受一枚完整 emoji（Unicode 17.0）或兼容符号 ○；不能包含空白、文字或孤立组件。";
}

internal sealed record EmojiItem(string Id, string Token);

internal sealed class PrefixSnapshot
{
    internal long Revision { get; }
    internal ReadOnlyCollection<EmojiItem> Items { get; }
    internal ReadOnlyCollection<string> History { get; }
    internal PrefixSnapshot(long revision, IEnumerable<EmojiItem> items, IEnumerable<string> history)
    {
        Revision = revision; Items = Array.AsReadOnly(items.ToArray());
        History = Array.AsReadOnly(history.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray());
    }
    internal static PrefixSnapshot Default => new(0, PrefixRules.Icons.Select((s, i) => new EmojiItem("default-" + i, s)), PrefixRules.Icons);
    internal static PrefixSnapshot MergeHistory(PrefixSnapshot active, params PrefixSnapshot[] others) =>
        new(active.Revision, active.Items, PrefixRules.Icons.Concat(active.History).Concat(active.Items.Select(i => i.Token))
            .Concat(others.SelectMany(o => o.History.Concat(o.Items.Select(i => i.Token)))));
    internal PrefixSnapshot WithItems(IEnumerable<EmojiItem> items) => new(Revision + 1, items, History.Concat(items.Select(i => i.Token)));
}

internal sealed class EmojiDraftItem(EmojiItem item, bool added = false)
{
    internal EmojiItem Item { get; } = item;
    internal bool Added { get; } = added;
    internal bool Deleted { get; set; }
}

internal sealed class EmojiDraft(PrefixSnapshot source)
{
    internal List<EmojiDraftItem> Items { get; } = source.Items.Select(i => new EmojiDraftItem(i)).ToList();
    internal bool Add(string token, out string id)
    {
        if (!EmojiCatalog.TryIdentity(token, out var identity)) throw new InvalidDataException(EmojiCatalog.Explain(token));
        var existing = Items.FirstOrDefault(x => EmojiCatalog.TryIdentity(x.Item.Token, out var key) && key == identity);
        if (existing is not null) { existing.Deleted = false; id = existing.Item.Id; return false; }
        var item = new EmojiItem(Guid.NewGuid().ToString("N"), token); Items.Add(new(item, true)); id = item.Id; return true;
    }
    internal void Move(string id, int slot)
    {
        int old = Items.FindIndex(x => x.Item.Id == id); if (old < 0) return;
        var item = Items[old]; Items.RemoveAt(old); Items.Insert(Math.Clamp(slot, 0, Items.Count), item);
    }
    internal void RestoreOrder(IReadOnlyList<string> order)
    { var positions = order.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index); Items.Sort((a, b) => positions[a.Item.Id].CompareTo(positions[b.Item.Id])); }
    internal PrefixSnapshot Commit() => source.WithItems(Items.Where(i => !i.Deleted).Select(i => i.Item));
}

internal sealed class EmojiConfigurationStore(string folder, string fileName = "emoji-settings.json")
{
    sealed record Document(int SchemaVersion, string UnicodeVersion, long Revision, EmojiItem[] Items, string[] History);
    internal string FilePath => Path.Combine(folder, fileName);
    internal bool Exists => File.Exists(FilePath);
    internal bool RecoveryRequired { get; private set; }
    internal string? LoadError { get; private set; }
    internal Action<string>? BeforeReplace;
    static readonly JsonSerializerOptions json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly UTF8Encoding utf8 = new(true, true);
    internal PrefixSnapshot Load()
    {
        RecoveryRequired = false; LoadError = null;
        if (!File.Exists(FilePath)) return PrefixSnapshot.Default;
        try { return Decode(File.ReadAllText(FilePath, utf8)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { RecoveryRequired = true; LoadError = "配置读取失败，原文件已保留：" + ex.Message; return PrefixSnapshot.Default; }
    }
    internal static PrefixSnapshot Decode(string text)
    {
        var d = JsonSerializer.Deserialize<Document>(text, json) ?? throw new InvalidDataException("配置为空。");
        if (d.SchemaVersion != 1 || d.UnicodeVersion != EmojiCatalog.Version || d.Revision < 0 || d.Items is null || d.History is null) throw new InvalidDataException("配置版本或结构不受支持。");
        var ids = new HashSet<string>(StringComparer.Ordinal); var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in d.Items)
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) || !EmojiCatalog.TryIdentity(item.Token, out var key) || !tokens.Add(key)) throw new InvalidDataException("列表含重复身份、重复 ID 或非法序列。");
        if (d.History.Any(t => !EmojiCatalog.TryIdentity(t, out _)) || d.History.Distinct(StringComparer.Ordinal).Count() != d.History.Length) throw new InvalidDataException("历史前缀无效或重复。");
        if (PrefixRules.Icons.Concat(d.Items.Select(i => i.Token)).Except(d.History, StringComparer.Ordinal).Any()) throw new InvalidDataException("历史前缀登记缺失。");
        return new(d.Revision, d.Items, d.History);
    }
    internal void PermitRecovery()
    {
        if (!RecoveryRequired) return;
        // 用户明确点击恢复后才备份原件并允许替换；不会静默覆盖损坏文件。
        File.Copy(FilePath, FilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        RecoveryRequired = false; LoadError = null;
    }
    internal void Save(PrefixSnapshot snapshot)
    {
        if (RecoveryRequired) throw new InvalidOperationException("请先备份并恢复损坏的配置。");
        Directory.CreateDirectory(folder);
        var text = JsonSerializer.Serialize(new Document(1, EmojiCatalog.Version, snapshot.Revision, snapshot.Items.ToArray(), snapshot.History.ToArray()), json);
        Decode(text);
        string temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { using var writer = new StreamWriter(stream, utf8, 1024, true); writer.Write(text); writer.Flush(); stream.Flush(true); }
            Decode(File.ReadAllText(temporary, utf8)); BeforeReplace?.Invoke(temporary);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak", true); else File.Move(temporary, FilePath);
            // 原子替换成功即提交。调用方在同一 UI 调度内发布，不存在取消后回滚已提交文件。
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
