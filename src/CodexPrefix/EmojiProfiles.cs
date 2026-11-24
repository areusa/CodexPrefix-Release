using System.IO;
using System.Text;

namespace CodexPrefix;

internal sealed class DefaultEmojiStore(string dataFolder, string appFolder)
{
    readonly EmojiConfigurationStore local = new(dataFolder, "default-template.json");
    internal string? LoadError { get; private set; }
    internal PrefixSnapshot Load()
    {
        if (local.Exists)
        {
            var value = local.Load();
            LoadError = local.LoadError;
            return value;
        }
        try
        {
            var runtime = Path.Combine(appFolder, "default-emojis.json");
            if (File.Exists(runtime)) return EmojiConfigurationStore.Decode(File.ReadAllText(runtime, new UTF8Encoding(false, true)));
            using var stream = typeof(DefaultEmojiStore).Assembly.GetManifestResourceStream("CodexPrefix.Defaults.emoji-defaults.json")!;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            return EmojiConfigurationStore.Decode(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { LoadError = "默认模板读取失败，使用基础列表：" + ex.Message; return PrefixSnapshot.Default; }
    }
    internal void Save(PrefixSnapshot snapshot)
    {
        // 仅保存用户目录中的本机模板，不写回程序文件或向上查找源码仓库。
        local.Load();
        local.Save(snapshot);
    }
}
