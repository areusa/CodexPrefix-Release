using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace CodexPrefix;

public partial class MainWindow
{
    bool updateBusy;
    bool updateChecking;
    UpdateCheckResult? availableUpdate;
    string? updateSource = "GitHub";
    CancellationTokenSource? updateCancellation;
    bool IsUpdateBusy => updateBusy;
    string UpdatesRoot => Path.Combine(dataFolder, "updates");

    void InitializeUpdater()
    {
        UpdateVersionText.Text = UpdateService.ResolveCurrentVersion().ToDisplayString();
        UpdateActionButton.Content = ActionGlyph("RefreshUpdateGeometry", 12);
        AutomationProperties.SetName(UpdateActionButton, "检查或安装更新");
        var menu = new ContextMenu();
        var select = new MenuItem { Header = "选择新版发布目录" };
        select.Click += async (_, _) =>
        {
            try { if (ChooseUpdateSource()) await CheckUpdate(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { UpdateStateText.Text = "来源未保存"; UpdateStateText.ToolTip = ex.Message; }
        };
        menu.Items.Add(select);
        var online = new MenuItem { Header = "使用公开发布仓检查更新" };
        online.Click += async (_, _) => { if (!updateBusy && !updateChecking && !EditingEmoji) { updateSource = "GitHub"; SaveUpdateSource(); availableUpdate = null; await CheckUpdate(); } };
        menu.Items.Add(online); UpdateActionButton.ContextMenu = menu;
        if (demoHandle != 0 || isCustomDataDir || TestDataRoot is not null)
        { UpdateStateText.Text = "隔离模式"; UpdateActionButton.IsEnabled = false; return; }
        try
        {
            string file = Path.Combine(dataFolder, "update-source.json");
            if (File.Exists(file)) updateSource = JsonSerializer.Deserialize<string>(File.ReadAllText(file, Encoding.UTF8));
        }
        catch { log.Write("更新来源", "读取失败，保留原文件；需要重新选择。"); }
        UpdateStateText.Text = updateSource == "GitHub" ? "公开仓更新" : "本地更新";
        _ = StartupUpdateCheck();
    }
    async Task StartupUpdateCheck()
    {
        try { await Task.Delay(1500, lifetime.Token); if (!closed && !operation && !updateBusy) await CheckUpdate(); }
        catch (OperationCanceledException) { }
    }
    IUpdateTransport CreateUpdateTransport() => updateSource == "GitHub"
        ? new GitHubRawUpdateTransport(UpdateService.ResolveCurrentVersion().ToString())
        : new LocalUpdateTransport(updateSource ?? throw new InvalidOperationException("请先选择更新来源。"));
    void SaveUpdateSource()
    {
        try
        {
            Directory.CreateDirectory(dataFolder);
            string file = Path.Combine(dataFolder, "update-source.json"), temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(updateSource), new UTF8Encoding(false));
            File.Move(temporary, file, true);
        }
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write("更新来源", "当前来源已启用，但偏好未能保存。"); }
    }
    bool ChooseUpdateSource()
    {
        if (updateBusy || updateChecking || operation || EditingEmoji || demoHandle != 0 || isCustomDataDir) return false;
        using var chooser = new System.Windows.Forms.FolderBrowserDialog { Description = "选择新版 CodexPrefix 的完整 release 文件夹", UseDescriptionForTitle = true };
        if (chooser.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;
        string selected = Path.GetFullPath(chooser.SelectedPath);
        if (string.Equals(selected.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        { UpdateStateText.Text = "请选择另一份新版目录"; return false; }
        updateSource = selected;
        SaveUpdateSource();
        availableUpdate = null;
        return true;
    }
    async void ClickUpdateAction(object sender, RoutedEventArgs e)
    {
        if (demoHandle != 0 || isCustomDataDir || closed || operation || profileSwitching || updateChecking) return;
        if (updateBusy) { updateCancellation?.Cancel(); return; }
        if (EditingEmoji) { EditorStatus("请先保存或取消列表草稿，再执行更新。", true); return; }
        try
        {
            if (updateSource is null && !ChooseUpdateSource()) return;
            if (availableUpdate is { HasUpdate: true }) await ApplyAvailableUpdate();
            else await CheckUpdate();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { UpdateStateText.Text = "更新未完成"; UpdateStateText.ToolTip = ex.Message; }
    }
    async Task CheckUpdate()
    {
        if (updateChecking || updateBusy || closed || updateSource is null) return;
        updateChecking = true; UpdateStateText.Text = "检查中…"; UpdateActionButton.IsEnabled = false;
        try
        {
            using var source = CreateUpdateTransport();
            using var service = new UpdateService(transport: source);
            availableUpdate = await service.CheckForUpdateAsync(true, lifetime.Token);
            if (!closed) UpdateStateText.Text = availableUpdate.HasUpdate ? "发现 " + availableUpdate.Remote.ToDisplayString() : "已是最新";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or UpdateHttpException or OperationCanceledException)
        { availableUpdate = null; UpdateStateText.Text = "检查失败"; UpdateStateText.ToolTip = ex.Message; }
        finally { updateChecking = false; if (!closed) { UpdateActionButton.IsEnabled = true; EnableActions(current is not null); } }
    }
    async Task ApplyAvailableUpdate()
    {
        if (availableUpdate is not { HasUpdate: true } found || updateSource is null || EditingEmoji || updateBusy) return;
        RegisterProfileHistory(emojiConfig);
        updateBusy = true; updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        ++contextVersion; ++visualEpoch; Interlocked.Increment(ref emojiGeneration); current = null;
        EnableActions(false); UpdateStateText.Text = "校验更新…"; UpdateActionButton.ToolTip = "取消更新准备";
        try
        {
            using var source = CreateUpdateTransport();
            using var service = new UpdateService(transport: source);
            var fresh = await service.CheckForUpdateAsync(true, updateCancellation.Token);
            if (fresh.Manifest != found.Manifest) throw new InvalidDataException("来源版本已变化，请重新检查。");
            var staged = await service.DownloadAndVerifyAsync(fresh.Manifest, AppContext.BaseDirectory, UpdatesRoot, null, updateCancellation.Token);
            updateCancellation.Token.ThrowIfCancellationRequested();
            UpdateStateText.Text = "重启更新…";
            UpdateApplier.LaunchHelper(staged, AppContext.BaseDirectory, UpdatesRoot, null);
            exitRequested = true;
            Close();
        }
        catch (OperationCanceledException) { if (!closed) UpdateStateText.Text = "已取消"; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Net.Http.HttpRequestException or UpdateHttpException)
        { if (!closed) { UpdateStateText.Text = "更新失败"; UpdateStateText.ToolTip = ex.Message; } }
        finally
        {
            updateBusy = false; updateCancellation?.Dispose(); updateCancellation = null;
            if (!closed) { UpdateActionButton.ToolTip = "检查或安装更新"; EnableActions(false); }
        }
    }
    void DisposeUpdater() { updateCancellation?.Cancel(); }
}
