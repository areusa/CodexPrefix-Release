using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexPrefix;

internal sealed record UpdateEntry(string RelativePath, bool Existed, string OldHash, string NewHash);
internal sealed record UpdateJournal(string TargetDirectory, string StageDirectory, string Version, string Token,
    string Phase, UpdateEntry[] Entries);

internal static class UpdateApplier
{
    static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string? Argument(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    static void WriteJournal(string directory, UpdateJournal journal)
    {
        string path = Path.Combine(directory, "transaction.json"), temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { using var writer = new StreamWriter(stream, Utf8, 4096, true); writer.Write(JsonSerializer.Serialize(journal)); writer.Flush(); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    static UpdateJournal ReadJournal(string directory)
        => JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(directory, "transaction.json"), Utf8))
            ?? throw new InvalidDataException("更新事务为空。");
    internal static bool IsSafeChild(string path, string root)
    {
        string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }
    static string Target(string directory, string relative)
    {
        if (!UpdateService.IsSafeAllowedRelativePath(relative)) throw new InvalidDataException("更新事务含非法文件。");
        string path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSafeChild(path, directory)) throw new InvalidDataException("更新文件越界。");
        RejectReparseAncestors(path);
        return path;
    }
    static void RejectReparseAncestors(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新路径包含重解析点，未执行替换。");
    }
    internal static string ApplyFiles(StagedUpdate staged, string targetDirectory, string updatesRoot, Action<string>? beforeReplace = null)
    {
        var targetRoot = Path.GetFullPath(targetDirectory);
        if (!IsSafeChild(staged.StageDir, updatesRoot)) throw new InvalidDataException("暂存目录不属于本次更新根目录。");
        if (IsSafeChild(staged.StageDir, targetRoot) || IsSafeChild(targetRoot, staged.StageDir)) throw new InvalidDataException("更新与目标目录不可嵌套。");
        RejectReparseAncestors(targetRoot); RejectReparseAncestors(staged.StageDir);
        if (!staged.Files.Any(e => e.RelativePath == UpdateService.ReleaseExeName) || staged.Files.Select(e => e.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != staged.Files.Count)
            throw new InvalidDataException("暂存清单缺少主程序或有重复文件。");
        var entries = new List<UpdateEntry>();
        foreach (var item in staged.Files)
        {
            var source = Target(staged.StageDir, item.RelativePath);
            if (!UpdateService.IsLowerSha256Hex(item.Sha256) || UpdateService.ComputeSha256Hex(source) != item.Sha256)
                throw new InvalidDataException("应用前暂存校验失败：" + item.RelativePath);
            var destination = Target(targetRoot, item.RelativePath);
            bool exists = File.Exists(destination);
            entries.Add(new(item.RelativePath, exists, exists ? UpdateService.ComputeSha256Hex(destination) : "", item.Sha256));
        }
        string transaction = Path.Combine(updatesRoot, "transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        var journal = new UpdateJournal(targetRoot, staged.StageDir, staged.Version.ToString(), Guid.NewGuid().ToString("N"), "Preparing", entries.ToArray());
        try
        {
            // Save every original before any target changes; a durable journal precedes replacement.
            foreach (var item in entries.Where(e => e.Existed))
            {
                string backup = Target(Path.Combine(transaction, "original"), item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(Target(targetRoot, item.RelativePath), backup);
                if (UpdateService.ComputeSha256Hex(backup) != item.OldHash) throw new IOException("更新前文件发生变化。");
            }
            journal = journal with { Phase = "Applying" }; WriteJournal(transaction, journal);
            foreach (var item in entries)
            {
                beforeReplace?.Invoke(item.RelativePath);
                InstallFile(Target(staged.StageDir, item.RelativePath), Target(targetRoot, item.RelativePath), item.NewHash);
            }
            WriteJournal(transaction, journal with { Phase = "AwaitingHealth" });
            return transaction;
        }
        catch
        {
            if (File.Exists(Path.Combine(transaction, "transaction.json"))) Rollback(transaction);
            throw;
        }
    }
    static void InstallFile(string source, string destination, string expectedHash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".update-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            using (var flush = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None)) flush.Flush(true);
            if (UpdateService.ComputeSha256Hex(temporary) != expectedHash) throw new IOException("替换前文件校验失败。");
            File.Move(temporary, destination, true);
            if (UpdateService.ComputeSha256Hex(destination) != expectedHash) throw new IOException("替换后文件校验失败。");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void Rollback(string transaction)
    {
        var journal = ReadJournal(transaction);
        if (journal.Phase is "Committed" or "RolledBack") return;
        foreach (var entry in journal.Entries.Reverse())
        {
            var destination = Target(journal.TargetDirectory, entry.RelativePath);
            if (File.Exists(destination))
            {
                var currentHash = UpdateService.ComputeSha256Hex(destination);
                if (currentHash != entry.NewHash && currentHash != entry.OldHash)
                    throw new IOException("目标已被外部修改，保留事务供恢复：" + entry.RelativePath);
            }
            if (entry.Existed)
                InstallFile(Target(Path.Combine(transaction, "original"), entry.RelativePath), destination, entry.OldHash);
            else if (File.Exists(destination))
            {
                if (UpdateService.ComputeSha256Hex(destination) != entry.NewHash) throw new IOException("新文件已被外部修改，停止自动删除：" + entry.RelativePath);
                File.Delete(destination);
            }
        }
        WriteJournal(transaction, journal with { Phase = "RolledBack" });
    }
    internal static void ConfirmHealth(string transaction, string token, string updatesRoot)
    {
        if (!IsSafeChild(transaction, updatesRoot)) throw new InvalidDataException("健康回执目录越界。");
        var journal = ReadJournal(transaction);
        if (journal.Phase != "AwaitingHealth" || journal.Token != token || journal.Version != UpdateService.ResolveCurrentVersion().ToString()
            || !string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), journal.TargetDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("新版健康握手不匹配。");
        var receipt = Path.Combine(transaction, "healthy.json");
        File.WriteAllText(receipt + ".tmp", JsonSerializer.Serialize(new { token, version = journal.Version }), Utf8);
        File.Move(receipt + ".tmp", receipt, true);
    }
    internal static bool HasValidHealthReceipt(string transaction, UpdateJournal journal)
    {
        string receipt = Path.Combine(transaction, "healthy.json");
        if (!File.Exists(receipt)) return false;
        using var health = JsonDocument.Parse(File.ReadAllText(receipt, Utf8));
        if (health.RootElement.GetProperty("token").GetString() != journal.Token || health.RootElement.GetProperty("version").GetString() != journal.Version) return false;
        return journal.Entries.All(e => File.Exists(Target(journal.TargetDirectory, e.RelativePath))
            && UpdateService.ComputeSha256Hex(Target(journal.TargetDirectory, e.RelativePath)) == e.NewHash);
    }
    internal static void RecoverInterrupted(string targetDirectory, string updatesRoot)
    {
        if (!Directory.Exists(updatesRoot)) return;
        foreach (var transaction in Directory.EnumerateDirectories(updatesRoot, "transaction-*", SearchOption.TopDirectoryOnly))
        {
            if (!File.Exists(Path.Combine(transaction, "transaction.json"))) continue;
            var journal = ReadJournal(transaction);
            if (!string.Equals(journal.TargetDirectory.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
            if (journal.Phase == "AwaitingHealth" && HasValidHealthReceipt(transaction, journal))
                WriteJournal(transaction, journal with { Phase = "Committed" });
            else if (journal.Phase is "Applying" or "AwaitingHealth") Rollback(transaction);
        }
    }
    internal static void LaunchHelper(StagedUpdate staged, string targetDirectory, string updatesRoot, string? customDataDir)
    {
        var launch = new ProcessStartInfo(staged.StagedExePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--apply-update", "--parent-pid", Environment.ProcessId.ToString(), "--parent-started", Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(),
            "--target-dir", targetDirectory, "--stage-dir", staged.StageDir, "--updates-root", updatesRoot }) launch.ArgumentList.Add(arg);
        if (customDataDir is not null) { launch.ArgumentList.Add("--data-dir"); launch.ArgumentList.Add(customDataDir); }
        _ = Process.Start(launch) ?? throw new IOException("更新助手未启动。");
    }
    internal static int Run(string[] args)
    {
        string? transaction = null;
        Process? restarted = null;
        string? targetRoot = Argument(args, "--target-dir");
        string? updatesRoot = Argument(args, "--updates-root");
        try
        {
            string stage = Argument(args, "--stage-dir") ?? throw new InvalidDataException("缺少暂存目录。");
            if (targetRoot is null || updatesRoot is null || !IsSafeChild(stage, updatesRoot)) throw new InvalidDataException("更新参数越界。");
            if (!int.TryParse(Argument(args, "--parent-pid"), out int parentPid) || !long.TryParse(Argument(args, "--parent-started"), out long parentStarted))
                throw new InvalidDataException("缺少父进程身份。");
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                if (parent.StartTime.ToUniversalTime().Ticks == parentStarted && !parent.WaitForExit(15000)) throw new IOException("原程序未退出，未替换文件。");
            }
            catch (ArgumentException) { }
            var manifest = JsonSerializer.Deserialize<StageManifest>(File.ReadAllText(Path.Combine(stage, "stage-manifest.json"), Utf8)) ?? throw new InvalidDataException("缺少暂存清单。");
            if (!SemanticVersion.TryParse(manifest.Version, out var version) || manifest.Files is null) throw new InvalidDataException("暂存版本无效。");
            using var updateLock = new FileStream(Path.Combine(updatesRoot, "apply.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            RecoverInterrupted(targetRoot, updatesRoot);
            transaction = ApplyFiles(new(version, stage, Path.Combine(stage, UpdateService.ReleaseExeName), manifest.Files), targetRoot, updatesRoot);
            var journal = ReadJournal(transaction);
            var launch = new ProcessStartInfo(Path.Combine(targetRoot, UpdateService.ReleaseExeName)) { UseShellExecute = false, WorkingDirectory = targetRoot };
            foreach (var arg in new[] { "--update-transaction", transaction, "--health-token", journal.Token }) launch.ArgumentList.Add(arg);
            if (Argument(args, "--data-dir") is { } custom) { launch.ArgumentList.Add("--data-dir"); launch.ArgumentList.Add(custom); }
            restarted = Process.Start(launch) ?? throw new IOException("新版未启动。");
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 30000)
            {
                if (HasValidHealthReceipt(transaction, journal)) { WriteJournal(transaction, journal with { Phase = "Committed" }); return 0; }
                if (restarted.HasExited) throw new IOException("新版在健康握手前退出。");
                Thread.Sleep(100);
            }
            throw new TimeoutException("新版未完成启动健康握手。");
        }
        catch (Exception ex)
        {
            try { if (restarted is { HasExited: false }) { restarted.Kill(true); restarted.WaitForExit(5000); } } catch { }
            try { if (transaction is not null) Rollback(transaction); }
            catch (Exception rollbackError) { new LocalLog(updatesRoot ?? Path.GetTempPath()).Write("更新恢复失败", rollbackError.Message); return 3; }
            new LocalLog(updatesRoot ?? Path.GetTempPath()).Write("更新失败", ex.Message);
            try
            {
                if (targetRoot is not null && File.Exists(Path.Combine(targetRoot, UpdateService.ReleaseExeName)))
                {
                    var restore = new ProcessStartInfo(Path.Combine(targetRoot, UpdateService.ReleaseExeName)) { UseShellExecute = false };
                    Process.Start(restore);
                }
            }
            catch { }
            return 1;
        }
        finally { restarted?.Dispose(); }
    }
}
