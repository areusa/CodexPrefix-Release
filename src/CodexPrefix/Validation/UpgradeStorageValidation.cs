using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;

namespace CodexPrefix;

internal static class UpgradeStorageValidation
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexPrefix-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var personal = new EmojiConfigurationStore(Path.Combine(root, "user"));
            var original = PrefixSnapshot.Default.WithItems([new("person-id", "👩‍💻"), new("variant-id", "✌️")]);
            personal.Save(original);
            var originalBytes = File.ReadAllBytes(personal.FilePath);
            string appFolder = Path.Combine(root, "app"); Directory.CreateDirectory(appFolder);
            File.WriteAllText(Path.Combine(appFolder, "unrelated.old"), "keep");
            var template = new DefaultEmojiStore(Path.Combine(root, "user"), appFolder);
            var initial = template.Load();
            check(initial.Items.Count > 0, "无本机模板时读取授权内嵌默认，不创建个人配置替代品");
            var nextDefault = PrefixSnapshot.Default.WithItems([new("default-local", "🚀")]);
            template.Save(nextDefault);
            check(File.ReadAllBytes(personal.FilePath).SequenceEqual(originalBytes), "保存本机默认模板不得改写个人列表的任何字节");
            check(!File.Exists(Path.Combine(appFolder, "default-emojis.json")), "模板编辑不写程序目录或源码资源");
            var combined = PrefixSnapshot.MergeHistory(template.Load(), personal.Load());
            check(PrefixRules.Apply("👩‍💻 中文  空格", "🚀", combined) == "🚀 中文  空格", "默认模板仍能精确替换个人历史前缀并保留内部空格");
            var history = new EmojiConfigurationStore(Path.Combine(root, "user"), "prefix-history.json");
            history.Save(new(1, Array.Empty<EmojiItem>(), combined.History));
            template.Save(PrefixSnapshot.Default.WithItems([new("other", "🔥")]));
            var afterRestart = PrefixSnapshot.MergeHistory(new DefaultEmojiStore(Path.Combine(root, "user"), appFolder).Load(), history.Load());
            check(PrefixRules.Apply("🚀 历史默认", null, afterRestart) == "历史默认", "模板被替换并重新创建 store 后，独立历史登记仍能识别旧 token");
            check(personal.Load().Items.Select(i => i.Id).SequenceEqual(original.Items.Select(i => i.Id)), "稳定 ID 跨模板保存和重新加载保持不变");
            personal.BeforeReplace = _ => throw new IOException("模拟替换失败");
            bool failed = false;
            try { personal.Save(original.WithItems([])); } catch (IOException) { failed = true; }
            check(failed && File.ReadAllBytes(personal.FilePath).SequenceEqual(originalBytes), "个人列表替换失败保留原文件，不能提前提交草稿");
            string localTemplate = Path.Combine(root, "user", "default-template.json");
            File.WriteAllText(localTemplate, "{broken", new UTF8Encoding(false));
            var damaged = new DefaultEmojiStore(Path.Combine(root, "user"), appFolder);
            damaged.Load(); failed = false;
            try { damaged.Save(nextDefault); } catch (InvalidOperationException) { failed = true; }
            check(failed && File.ReadAllText(localTemplate) == "{broken", "损坏模板必须保留并阻止静默覆盖");
            foreach (int count in new[] { 0, 1, 6, 7, 12, 40 })
            {
                var panel = new EmojiGridPanel();
                var buttons = Enumerable.Range(0, count).Select(i => new Button { Uid = "cell-" + i }).ToArray();
                panel.SetButtons(buttons); panel.Measure(new(230, 200)); panel.Arrange(new(0, 0, 230, 200));
                panel.SetButtons(buttons.Reverse(), true); panel.Measure(new(230, 200)); panel.Arrange(new(0, 0, 230, 200));
                check(!panel.HasMovingCells && panel.OrderedButtons.Count == count, "模板切换直接落槽、残余速度归零，数量=" + count);
            }
            VerifyUpdater(root, check);
            VerifyOffscreenLayouts(root, check);
        }
        finally { Directory.Delete(root, true); }
    }
    static void VerifyOffscreenLayouts(string root, Action<bool, string> check)
    {
        var app = System.Windows.Application.Current;
        var previous = app.ShutdownMode;
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        try
        {
            var samples = EmojiCatalog.Samples.GroupBy(t => { EmojiCatalog.TryIdentity(t, out var id); return id; }).Select(g => g.First()).Take(40).ToArray();
            foreach (int count in new[] { 0, 1, 6, 7, 12, 40 })
            {
                string data = Path.Combine(root, "layout-" + count);
                new EmojiConfigurationStore(data).Save(PrefixSnapshot.Default.WithItems(samples.Take(count).Select((t, i) => new EmojiItem("test-" + i, t))));
                // Never Show/Activate this window and never start its scanner or tray.
                var window = new MainWindow(null, data);
                try
                {
                    window.UpdateFullEmojiLayout();
                    var host = window.Panels;
                    host.Apply(new System.Windows.Rect(0, 0, host.FullSize.Width, host.FullSize.Height), 1, 0, 14, System.Windows.Media.Transform.Identity, true);
                    host.Root.Measure(host.FullSize); host.Root.Arrange(new System.Windows.Rect(new System.Windows.Point(), host.FullSize)); host.Root.UpdateLayout();
                    var viewport = (EmojiViewport)window.FindName("FullEmojiViewport");
                    var update = (System.Windows.FrameworkElement)window.FindName("UpdateVersionText");
                    var point = update.TranslatePoint(new System.Windows.Point(), host.FullContent);
                    check(viewport.Height == EmojiLayout.FullViewport(count) && point.Y >= 0 && point.Y + update.ActualHeight <= host.FullContent.Height,
                        "未显示窗口的真实 XAML 布局保持列表视口和底部操作行，数量=" + count);
                    check(System.Windows.Media.VisualTreeHelper.GetParent((System.Windows.FrameworkElement)window.FindName("EmojiInputLayer")) == host.Root,
                        "添加浮层保持位于共享根画布，不受列表一行高度裁切，数量=" + count);
                    check(window.CurrentBinding is null, "离屏布局验证不启动真实宿主绑定，数量=" + count);
                }
                finally { window.Close(); }
            }
        }
        finally { app.ShutdownMode = previous; }
    }
    static void VerifyUpdater(string root, Action<bool, string> check)
    {
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "installed"), updates = Path.Combine(root, "updates");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        var version = new SemanticVersion(0, 7, 1);
        var payloads = new Dictionary<string, byte[]>
        {
            [UpdateService.ReleaseExeName] = Encoding.ASCII.GetBytes("MZ-FAKE-NEW-EXECUTABLE-NOT-EXECUTED"),
            ["default-emojis.json"] = Encoding.UTF8.GetBytes("{}"),
        };
        string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        payloads["version.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { name = "CodexPrefix", version = version.ToString(), platform = "win-x64", executable = UpdateService.ReleaseExeName, sha256 = Sha(payloads[UpdateService.ReleaseExeName]) }));
        foreach (var item in payloads) File.WriteAllBytes(Path.Combine(source, item.Key), item.Value);
        string sums = string.Join("\n", payloads.Select(p => Sha(p.Value) + "  " + p.Key)) + "\n";
        File.WriteAllText(Path.Combine(source, "SHA256SUMS.txt"), sums, new UTF8Encoding(false));
        byte[] oldExe = Encoding.ASCII.GetBytes("MZ-ORIGINAL"), oldManifest = Encoding.UTF8.GetBytes("{\"version\":\"0.7.0\"}");
        File.WriteAllBytes(Path.Combine(target, UpdateService.ReleaseExeName), oldExe);
        File.WriteAllBytes(Path.Combine(target, "version.json"), oldManifest);
        File.WriteAllText(Path.Combine(target, "keep.old"), "unrelated");
        string config = Path.Combine(root, "user", "settings.json"); File.WriteAllText(config, "{\"Topmost\":false}");
        byte[] configBytes = File.ReadAllBytes(config);
        using var transport = new LocalUpdateTransport(source);
        using var service = new UpdateService(new SemanticVersion(0, 7, 0), transport);
        var found = service.CheckForUpdateAsync(true, default).GetAwaiter().GetResult();
        check(found.HasUpdate && found.Remote == version, "私有目录无需网络即可检查新版清单");
        var staged = service.DownloadAndVerifyAsync(found.Manifest, target, updates, null, default).GetAwaiter().GetResult();
        check(staged.Files.All(f => UpdateService.ComputeSha256Hex(Path.Combine(staged.StageDir, f.RelativePath)) == f.Sha256), "下载暂存完整文件白名单及全部校验和");
        string transaction = UpdateApplier.ApplyFiles(staged, target, updates);
        check(File.ReadAllBytes(Path.Combine(target, UpdateService.ReleaseExeName)).SequenceEqual(payloads[UpdateService.ReleaseExeName]), "真实文件替换事务安装新版字节（不执行假 EXE）");
        check(File.ReadAllBytes(config).SequenceEqual(configBytes) && File.ReadAllText(Path.Combine(target, "keep.old")) == "unrelated", "更新不改个人设置、不清理无关 .old 文件");
        UpdateApplier.RecoverInterrupted(target, updates);
        check(File.ReadAllBytes(Path.Combine(target, UpdateService.ReleaseExeName)).SequenceEqual(oldExe)
            && File.ReadAllBytes(Path.Combine(target, "version.json")).SequenceEqual(oldManifest)
            && !File.Exists(Path.Combine(target, "default-emojis.json")), "未完成健康握手的中断事务恢复全部旧文件并移除本事务新增文件");
        bool failed = false;
        try { UpdateApplier.ApplyFiles(staged, target, updates, name => { if (name == "version.json") throw new IOException("模拟中途替换失败"); }); }
        catch (IOException) { failed = true; }
        check(failed && File.ReadAllBytes(Path.Combine(target, UpdateService.ReleaseExeName)).SequenceEqual(oldExe), "中途文件写入失败立即补偿恢复原 EXE");
        string recovered = UpdateApplier.ApplyFiles(staged, target, updates);
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(recovered, "transaction.json")))!;
        File.WriteAllText(Path.Combine(recovered, "healthy.json"), JsonSerializer.Serialize(new { token = "wrong", version = journal.Version }));
        check(!UpdateApplier.HasValidHealthReceipt(recovered, journal), "伪造或陈旧 token 不能确认新版启动成功");
        File.WriteAllText(Path.Combine(recovered, "healthy.json"), JsonSerializer.Serialize(new { token = journal.Token, version = journal.Version }));
        check(UpdateApplier.HasValidHealthReceipt(recovered, journal), "健康回执需同时匹配事务、版本及全部安装字节");
        UpdateApplier.RecoverInterrupted(target, updates);
        check(JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(recovered, "transaction.json")))!.Phase == "Committed", "助手在收到健康回执后中断不会错误回滚已健康新版");
        // Restore a test target directly for the following corruption case; the committed backup remains evidence.
        File.WriteAllBytes(Path.Combine(target, UpdateService.ReleaseExeName), oldExe);
        File.AppendAllText(Path.Combine(staged.StageDir, UpdateService.ReleaseExeName), "CORRUPTED"); failed = false;
        try { UpdateApplier.ApplyFiles(staged, target, updates); } catch (InvalidDataException) { failed = true; }
        check(failed && File.ReadAllBytes(Path.Combine(target, UpdateService.ReleaseExeName)).SequenceEqual(oldExe), "应用前复核能阻断暂存文件被改写且不触碰已安装版");
        foreach (var bad in new[] { "../outside.exe", "settings.json", "emoji-settings.json", "sub\\evil.exe", "extra.dll" })
            check(!UpdateService.IsSafeAllowedRelativePath(bad), "禁止更新越界或个人配置路径：" + bad);
        check(!UpdateApplier.IsSafeChild(root, updates) && !UpdateApplier.IsSafeChild(updates, updates), "更新根目录本身和上级均不允许作为子事务");
    }
}
