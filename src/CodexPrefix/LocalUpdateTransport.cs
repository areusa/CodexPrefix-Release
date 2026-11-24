using System.IO;
using System.Text;

namespace CodexPrefix;

// 私有交付目录：仅从用户选择的 release 中读取白名单文件，不上传配置或凭据。
internal sealed class LocalUpdateTransport(string sourceDirectory) : IUpdateTransport
{
    string Resolve(string url)
    {
        var uri = new Uri(url);
        var marker = "/release/";
        int start = uri.AbsolutePath.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("更新来源路径不合法。");
        var relative = Uri.UnescapeDataString(uri.AbsolutePath[(start + marker.Length)..]);
        if (!UpdateService.IsSafeAllowedRelativePath(relative)) throw new InvalidDataException("更新来源包含未授权文件。");
        string path = Path.GetFullPath(Path.Combine(sourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新来源路径越界。");
        return path;
    }
    public Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        => File.ReadAllTextAsync(Resolve(url), new UTF8Encoding(false, true), cancellationToken);
    public async Task DownloadFileAsync(string url, string destinationPath, Action<long, long?>? onProgress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new FileStream(Resolve(url), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        onProgress?.Invoke(source.Length, source.Length);
    }
    public void Dispose() { }
}
