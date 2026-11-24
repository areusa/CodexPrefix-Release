using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexPrefix;

internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<SemanticVersion>
{
    internal static bool TryParse(string? raw, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        int plus = text.IndexOf('+');
        if (plus >= 0)
        {
            if (plus == text.Length - 1) return false;
            text = text[..plus];
        }

        string? pre = null;
        int dash = text.IndexOf('-');
        if (dash >= 0)
        {
            if (dash == text.Length - 1) return false;
            pre = text[(dash + 1)..];
            text = text[..dash];
            foreach (var part in pre.Split('.'))
            {
                if (part.Length == 0 || !part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
                if (part.All(char.IsAsciiDigit) && part.Length > 1 && part[0] == '0') return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        if (!TryParseCorePart(parts[0], out int major) || !TryParseCorePart(parts[1], out int minor) || !TryParseCorePart(parts[2], out int patch))
            return false;

        version = new SemanticVersion(major, minor, patch, pre);
        return true;
    }

    static bool TryParseCorePart(string s, out int value)
    {
        value = 0;
        if (s.Length == 0 || !s.All(char.IsAsciiDigit)) return false;
        if (s.Length > 1 && s[0] == '0') return false;
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(SemanticVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;

        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        int len = Math.Min(left.Length, right.Length);
        for (int i = 0; i < len; i++)
        {
            bool leftNum = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out long lv);
            bool rightNum = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out long rv);
            if (leftNum && rightNum)
            {
                c = lv.CompareTo(rv);
                if (c != 0) return c;
            }
            else if (leftNum != rightNum)
            {
                return leftNum ? -1 : 1;
            }
            else
            {
                c = string.CompareOrdinal(left[i], right[i]);
                if (c != 0) return c;
            }
        }
        return left.Length.CompareTo(right.Length);
    }

    public override string ToString() => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
    internal string ToDisplayString() => "v" + ToString();
}

internal sealed record ReleaseManifest(string Name, SemanticVersion Version, string Platform, string Executable, string Sha256);

internal sealed record UpdateCheckResult(bool HasUpdate, SemanticVersion Current, SemanticVersion Remote, ReleaseManifest Manifest);

internal sealed record UpdateDownloadProgress(int FileIndex, int FileCount, string CurrentFile, long BytesReceived, long? TotalBytes, int Percent);

internal sealed record StagedFileEntry(string RelativePath, string Sha256);

internal sealed record StageManifest(string Version, StagedFileEntry[] Files);

internal sealed record StagedUpdate(SemanticVersion Version, string StageDir, string StagedExePath, IReadOnlyList<StagedFileEntry> Files);

internal sealed record StartupUpdateContext(string? UpdatedFrom, string? CleanupDir, string? UpdateFailed);

internal sealed class UpdateHttpException(HttpStatusCode statusCode, string message) : Exception(message)
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
}

internal interface IUpdateTransport : IDisposable
{
    Task<string> GetStringAsync(string url, CancellationToken cancellationToken);
    Task DownloadFileAsync(string url, string destinationPath, Action<long, long?>? onProgress, CancellationToken cancellationToken);
}

internal sealed class GitHubRawUpdateTransport : IUpdateTransport
{
    static readonly int[] RetryDelaysMs = [600, 1500];
    readonly HttpClient client;
    readonly string userAgent;

    internal GitHubRawUpdateTransport(string currentVersion)
    {
        userAgent = "CodexPrefix/" + currentVersion;
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    public Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        => ExecuteWithRetryAsync(async attemptCt =>
        {
            using var request = NewRequest(url);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCt);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            ThrowIfFailed(response);
            return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }, cancellationToken);

    public Task DownloadFileAsync(string url, string destinationPath, Action<long, long?>? onProgress, CancellationToken cancellationToken)
        => ExecuteWithRetryAsync(async attemptCt =>
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var request = NewRequest(url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCt).ConfigureAwait(false);
            ThrowIfFailed(response);
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(attemptCt).ConfigureAwait(false);
            await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536];
            long received = 0;
            onProgress?.Invoke(0, total);
            while (true)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCt);
                readCts.CancelAfter(TimeSpan.FromSeconds(30));
                int read = await source.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), attemptCt).ConfigureAwait(false);
                received += read;
                onProgress?.Invoke(received, total);
            }
            await target.FlushAsync(attemptCt).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    HttpRequestMessage NewRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return req;
    }

    static void ThrowIfFailed(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new UpdateHttpException(response.StatusCode, $"远端返回 HTTP {(int)response.StatusCode}");
    }

    static async Task<T> ExecuteWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < RetryDelaysMs.Length && IsTransient(ex))
            {
                await Task.Delay(RetryDelaysMs[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    static bool IsTransient(Exception ex) => ex switch
    {
        UpdateHttpException http => http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        HttpRequestException => true,
        OperationCanceledException => true,
        IOException => true,
        _ => false,
    };

    public void Dispose() => client.Dispose();
}

internal sealed class UpdateService : IDisposable
{
    internal const string RepositoryOwner = "areusa";
    internal const string RepositoryName = "CodexPrefix-Release";
    internal const string ReleaseExeName = "CodexPrefix.exe";
    internal const string DefaultEmojisFileName = "default-emojis.json";

    internal static readonly IReadOnlySet<string> AllowedReleaseFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "CodexPrefix.exe",
        "default-emojis.json",
        "使用说明.md",
        "THIRD-PARTY-NOTICES.txt",
        "version.json",
        "SHA256SUMS.txt",
        "licenses/MingCute-LICENSE.txt",
        "licenses/Unicode-LICENSE.txt",
        "licenses/Runtime-LICENSE.txt",
        "licenses/Runtime-NOTICES.txt",
        "licenses/WindowsDesktop-LICENSE.txt",
    };

    readonly IUpdateTransport transport;
    readonly bool ownsTransport;
    readonly string rawBaseRoot;

    internal SemanticVersion CurrentVersion { get; }

    internal UpdateService(SemanticVersion? currentVersion = null, IUpdateTransport? transport = null, string? rawBaseRoot = null)
    {
        CurrentVersion = currentVersion ?? ResolveCurrentVersion();
        this.transport = transport ?? throw new InvalidOperationException("请先选择更新来源。");
        ownsTransport = transport is null;
        this.rawBaseRoot = (rawBaseRoot ?? $"https://raw.githubusercontent.com/{RepositoryOwner}/{RepositoryName}").TrimEnd('/');
    }

    internal static SemanticVersion ResolveCurrentVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemanticVersion.TryParse(info, out var parsedInfo)) return parsedInfo;
        var asmVer = assembly.GetName().Version;
        if (asmVer is not null) return new SemanticVersion(asmVer.Major, asmVer.Minor, Math.Max(0, asmVer.Build));
        return new SemanticVersion(0, 7, 0);
    }

    internal string BuildManifestUrl(bool bypassCache)
    {
        var url = $"{rawBaseRoot}/main/release/version.json";
        return bypassCache ? url + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : url;
    }

    internal string BuildReleaseFileUrl(string gitRef, string relativePath, bool bypassCache = false)
    {
        var encoded = string.Join("/", relativePath.Split('/').Select(Uri.EscapeDataString));
        var url = $"{rawBaseRoot}/{gitRef}/release/{encoded}";
        return bypassCache ? url + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : url;
    }

    internal static bool CanOfferUpdate(SemanticVersion current, SemanticVersion remote) =>
        remote.CompareTo(current) > 0 && (current.PreRelease is not null || remote.PreRelease is null);

    internal async Task<UpdateCheckResult> CheckForUpdateAsync(bool bypassCache, CancellationToken cancellationToken)
    {
        var json = await transport.GetStringAsync(BuildManifestUrl(bypassCache), cancellationToken).ConfigureAwait(false);
        var manifest = ParseManifest(json);
        bool hasUpdate = CanOfferUpdate(CurrentVersion, manifest.Version);
        return new UpdateCheckResult(hasUpdate, CurrentVersion, manifest.Version, manifest);
    }

    internal async Task<StagedUpdate> DownloadAndVerifyAsync(
        ReleaseManifest manifest,
        string targetDir,
        string updatesRoot,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!CanWriteDirectory(targetDir))
            throw new UnauthorizedAccessException("当前程序所在目录只读，无法原位替换更新。");

        string tagRef = manifest.Version.ToDisplayString();
        string resolvedRef = tagRef;
        string checksumText;
        try
        {
            checksumText = await transport.GetStringAsync(BuildReleaseFileUrl(tagRef, "SHA256SUMS.txt"), cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidDataException("更新版本 tag 尚未完整发布，已停止；不会混用 main 分支文件。", ex);
        }

        var checksums = ParseChecksums(checksumText);
        if (!checksums.TryGetValue(ReleaseExeName, out var exeHashInSums) || !string.Equals(exeHashInSums, manifest.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("远端校验和清单与版本清单中的主程序 SHA-256 不一致。");

        bool fullDir = IsFullReleaseDirectory(targetDir);
        var filesToStage = SelectFilesToDownload(targetDir, fullDir, checksums);

        Directory.CreateDirectory(updatesRoot);
        var stageDir = Path.Combine(updatesRoot, $"stage-{manifest.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDir);

        try
        {
            var stagedEntries = new List<StagedFileEntry>(filesToStage.Count);
            for (int i = 0; i < filesToStage.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relPath = filesToStage[i];
                string expectedSha = checksums[relPath];
                string destPath = Path.Combine(stageDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                string url = BuildReleaseFileUrl(resolvedRef, relPath);
                int index = i;

                await transport.DownloadFileAsync(url, destPath, (bytes, total) =>
                {
                    int pct = relPath == ReleaseExeName && total > 0
                        ? Math.Clamp((int)(bytes * 99 / total.Value), 0, 99)
                        : Math.Clamp((int)((index + (total > 0 ? (double)bytes / total.Value : 0)) * 99 / filesToStage.Count), 0, 99);
                    progress?.Report(new UpdateDownloadProgress(index + 1, filesToStage.Count, relPath, bytes, total, pct));
                }, cancellationToken).ConfigureAwait(false);

                string actualSha = ComputeSha256Hex(destPath);
                if (!string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
                    throw new InvalidDataException($"下载文件 SHA-256 校验失败：{relPath}");

                if (relPath == ReleaseExeName && !HasPeHeader(destPath))
                    throw new InvalidDataException("下载的主程序文件不是有效的 Windows 可执行文件。");

                stagedEntries.Add(new StagedFileEntry(relPath, actualSha));
            }

            progress?.Report(new UpdateDownloadProgress(filesToStage.Count, filesToStage.Count, ReleaseExeName, 1, 1, 100));

            if (fullDir && !stagedEntries.Any(e => e.RelativePath == "SHA256SUMS.txt"))
            {
                string stagedSumsPath = Path.Combine(stageDir, "SHA256SUMS.txt");
                byte[] sumsBytes = Encoding.UTF8.GetBytes(checksumText);
                File.WriteAllBytes(stagedSumsPath, sumsBytes);
                stagedEntries.Add(new StagedFileEntry("SHA256SUMS.txt", Convert.ToHexString(SHA256.HashData(sumsBytes)).ToLowerInvariant()));
            }

            var stageManifest = new StageManifest(manifest.Version.ToString(), stagedEntries.ToArray());
            var stageManifestPath = Path.Combine(stageDir, "stage-manifest.json");
            File.WriteAllText(stageManifestPath, JsonSerializer.Serialize(stageManifest), new UTF8Encoding(false));

            return new StagedUpdate(manifest.Version, stageDir, Path.Combine(stageDir, ReleaseExeName), stagedEntries.AsReadOnly());
        }
        catch
        {
            TryDeleteDirectory(stageDir);
            throw;
        }
    }

    internal static List<string> SelectFilesToDownload(string targetDir, bool isFullReleaseDir, IReadOnlyDictionary<string, string> remoteChecksums)
    {
        var list = new List<string> { ReleaseExeName };
        if (isFullReleaseDir)
        {
            foreach (var kv in remoteChecksums)
            {
                if (kv.Key == ReleaseExeName) continue;
                if (kv.Key is "version.json" or "SHA256SUMS.txt")
                {
                    list.Add(kv.Key);
                    continue;
                }
                string localPath = Path.Combine(targetDir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(localPath) || !string.Equals(ComputeSha256Hex(localPath), kv.Value, StringComparison.Ordinal))
                    list.Add(kv.Key);
            }
            return list;
        }

        if (remoteChecksums.TryGetValue(DefaultEmojisFileName, out var defaultHash))
        {
            string localDefaultPath = Path.Combine(targetDir, DefaultEmojisFileName);
            if (File.Exists(localDefaultPath) && !string.Equals(ComputeSha256Hex(localDefaultPath), defaultHash, StringComparison.Ordinal))
                list.Add(DefaultEmojisFileName);
        }
        return list;
    }

    internal static ReleaseManifest ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("版本清单格式无效。");

        string name = GetRequiredString(root, "name");
        string verStr = GetRequiredString(root, "version");
        string platform = GetRequiredString(root, "platform");
        string executable = GetRequiredString(root, "executable");
        string sha256 = GetRequiredString(root, "sha256");

        if (!string.Equals(name, "CodexPrefix", StringComparison.Ordinal))
            throw new InvalidDataException("远端版本清单应用名称不匹配。");
        if (!string.Equals(platform, "win-x64", StringComparison.Ordinal))
            throw new InvalidDataException("远端版本清单运行平台不受支持。");
        if (!string.Equals(executable, ReleaseExeName, StringComparison.Ordinal))
            throw new InvalidDataException("远端版本清单主程序名称不匹配。");
        if (!SemanticVersion.TryParse(verStr, out var version))
            throw new InvalidDataException("远端版本号格式无效。");
        if (!IsLowerSha256Hex(sha256))
            throw new InvalidDataException("远端版本清单缺少有效的 SHA-256 摘要。");

        return new ReleaseManifest(name, version, platform, executable, sha256);
    }

    internal static Dictionary<string, string> ParseChecksums(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("校验和清单为空。");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Length < 67 || line[64] != ' ' || line[65] != ' ')
                throw new InvalidDataException("校验和清单行格式无效。");

            var hash = line[..64];
            var relPath = line[66..];
            if (!IsLowerSha256Hex(hash))
                throw new InvalidDataException("校验和清单摘要无效。");
            if (!IsSafeAllowedRelativePath(relPath))
                throw new InvalidDataException($"校验和清单包含未授权路径：{relPath}");
            if (!result.TryAdd(relPath, hash))
                throw new InvalidDataException($"校验和清单存在重复文件项：{relPath}");
        }

        if (result.Count == 0 || !result.ContainsKey(ReleaseExeName))
            throw new InvalidDataException("校验和清单缺少主程序摘要。");
        return result;
    }

    internal static bool IsSafeAllowedRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Contains("..")) return false;
        if (path.Any(char.IsControl)) return false;
        return AllowedReleaseFiles.Contains(path);
    }

    internal static bool IsFullReleaseDirectory(string targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir)) return false;
        return File.Exists(Path.Combine(targetDir, "version.json"))
            || File.Exists(Path.Combine(targetDir, "SHA256SUMS.txt"))
            || Directory.Exists(Path.Combine(targetDir, "licenses"));
    }

    internal static bool CanWriteDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        string probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    internal static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static bool IsLowerSha256Hex(string value)
        => value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    internal static bool HasPeHeader(string filePath)
    {
        Span<byte> magic = stackalloc byte[2];
        using var stream = File.OpenRead(filePath);
        return stream.Read(magic) == 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z';
    }

    internal static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    static string GetRequiredString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"版本清单缺少字段：{propertyName}");
        return prop.GetString() ?? throw new InvalidDataException($"版本清单字段为空：{propertyName}");
    }

    public void Dispose()
    {
        if (ownsTransport) transport.Dispose();
    }
}
