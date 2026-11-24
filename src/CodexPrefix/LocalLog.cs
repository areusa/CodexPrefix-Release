using System.IO;
using System.Text;

namespace CodexPrefix;

public sealed class LocalLog(string folder)
{
    internal string Folder => folder;
    readonly Dictionary<string, string> previous = new();
    readonly object gate = new();
    public void Write(string stage, string message)
    {
        // 自动日志只记录流程，不记录会话标题、正文或从页面中碰到的 ID。
        lock (gate)
        {
            if (previous.TryGetValue(stage, out var last) && last == message) return;
            previous[stage] = message;
            try
            {
                Directory.CreateDirectory(folder);
                using var writer = new StreamWriter(Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd") + ".log"), true, new UTF8Encoding(true));
                writer.WriteLine($"{DateTimeOffset.Now:O} [{stage}] {message}");
            }
            catch { /* 日志不可写不能改变安全决策。 */ }
        }
    }
}
