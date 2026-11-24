using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Animation;

namespace CodexPrefix;

public partial class MainWindow
{
    DefaultEmojiStore defaultStore = null!;
    EmojiConfigurationStore historyStore = null!;
    PrefixSnapshot personalConfig = PrefixSnapshot.Default;
    PrefixSnapshot defaultConfig = PrefixSnapshot.Default;
    PrefixSnapshot registeredHistory = PrefixSnapshot.Default;
    bool defaultProfile;
    bool profileSwitching;

    void InitializeProfiles()
    {
        emojiStore = new(dataFolder);
        personalConfig = emojiStore.Load();
        var appFolder = demoHandle != 0 || isCustomDataDir || TestDataRoot is not null ? Path.Combine(dataFolder, "isolated-app") : AppContext.BaseDirectory;
        defaultStore = new(dataFolder, appFolder);
        defaultConfig = defaultStore.Load();
        historyStore = new(dataFolder, "prefix-history.json");
        registeredHistory = historyStore.Load();
        defaultProfile = !emojiStore.Exists;
        // Existing personal lists always remain the migration default.
        emojiConfig = ActiveProfileSnapshot();
    }
    PrefixSnapshot ActiveProfileSnapshot() => PrefixSnapshot.MergeHistory(defaultProfile ? defaultConfig : personalConfig,
        personalConfig, defaultConfig, registeredHistory);

    void RegisterProfileHistory(PrefixSnapshot next)
    {
        var merged = PrefixSnapshot.MergeHistory(registeredHistory, next, emojiConfig, defaultConfig, personalConfig);
        if (historyStore.RecoveryRequired) throw new InvalidOperationException("历史登记损坏，原文件已保留，请先恢复后再保存模板。");
        if (historyStore.Exists && merged.History.SequenceEqual(registeredHistory.History)) return;
        // History commits first. If saving the list fails, extra registrations are safe;
        // no newly used token can disappear after a restart or a template update.
        historyStore.Save(new(registeredHistory.Revision + 1, Array.Empty<EmojiItem>(), merged.History));
        registeredHistory = merged;
    }
    PrefixSnapshot SavePersonalSnapshot(PrefixSnapshot draft)
    {
        var next = new PrefixSnapshot(personalConfig.Revision + 1, draft.Items,
            PrefixSnapshot.MergeHistory(draft, emojiConfig, personalConfig, defaultConfig, registeredHistory).History);
        RegisterProfileHistory(next);
        emojiStore.Save(next);
        personalConfig = next; defaultProfile = false; PersistProfilePreference();
        return ActiveProfileSnapshot();
    }
    void PersistProfilePreference()
    {
        try
        {
            Directory.CreateDirectory(dataFolder);
            string temporary = settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(new Settings(Topmost, defaultProfile ? "default" : "personal")), new System.Text.UTF8Encoding(false));
                File.Move(temporary, settingsPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write("配置偏好", "列表已生效，但活动模式偏好未能保存。"); }
    }
    void RefreshProfileUi()
    {
        if (ProfileToggleButton is null || defaultStore is null) return;
        ProfileToggleButton.Content = ActionGlyph("ProfileSwitchGeometry", 14);
        ProfileToggleButton.ToolTip = defaultProfile ? "当前默认模板 · 切换至个人列表" : "当前个人列表 · 切换至默认模板（个人列表保留）";
        AutomationProperties.SetName(ProfileToggleButton, (string)ProfileToggleButton.ToolTip);
        ProfileToggleButton.IsEnabled = !operation && !EditingEmoji && !profileSwitching && modeTransition is null && !IsUpdateBusy;
        SaveDefaultButton.Content = ActionGlyph("BuiltinDefaultGeometry", 14);
        SaveDefaultButton.Visibility = EditingEmoji ? Visibility.Visible : Visibility.Collapsed;
        SaveDefaultButton.IsEnabled = emojiPhase == EmojiEditPhase.Editing && !IconPanel.Dragging && !IsUpdateBusy;
    }
    async void ClickProfileToggle(object sender, RoutedEventArgs e)
    {
        if (operation || EditingEmoji || compact || modeTransition is not null || profileSwitching || IsUpdateBusy) return;
        if (!defaultProfile && (emojiStore.RecoveryRequired || historyStore.RecoveryRequired))
        { ShowStatus(new(StatusKind.NeedsAction, "配置需要恢复，暂不切换模板")); return; }
        profileSwitching = true;
        CancelEditorPointers(); ++contextVersion; ++visualEpoch; Interlocked.Increment(ref emojiGeneration); current = null;
        EnableActions(false); FullEmojiViewport.IsHitTestVisible = false;
        try
        {
            // Preserve seen defaults before switching or before a future installed-template update.
            RegisterProfileHistory(emojiConfig);
            if (SystemParameters.ClientAreaAnimation)
            {
                FullEmojiViewport.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                await Task.Delay(120);
            }
            if (closed) return;
            defaultProfile = !defaultProfile; PersistProfilePreference();
            emojiConfig = ActiveProfileSnapshot();
            FullEmojiViewport.ScrollTo(0, false);
            RefreshFullItems(snapImmediate: true); bar?.SetItems(emojiConfig.Items);
            ShowStatus(new(StatusKind.Completed, defaultProfile ? "已切换默认模板，个人列表保留" : "已切换个人列表"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { ShowStatus(new(StatusKind.Error, "切换未完成：" + ex.Message)); }
        finally
        {
            FullEmojiViewport.BeginAnimation(OpacityProperty, null);
            if (!closed && SystemParameters.ClientAreaAnimation)
                FullEmojiViewport.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else FullEmojiViewport.Opacity = 1;
            FullEmojiViewport.IsHitTestVisible = true;
            profileSwitching = false; RefreshProfileUi(); EnableActions(false);
        }
    }
    void ClickSaveDefault(object sender, RoutedEventArgs e)
    {
        if (emojiPhase != EmojiEditPhase.Editing || emojiDraft is null || IconPanel.Dragging || IsUpdateBusy) return;
        try
        {
            var next = PrefixSnapshot.MergeHistory(emojiDraft.Commit(), emojiConfig, registeredHistory, defaultConfig);
            RegisterProfileHistory(next);
            defaultStore.Save(next); defaultConfig = next;
            // The checkmark still saves the personal list; this explicit action saves the template.
            defaultProfile = true; PersistProfilePreference(); emojiConfig = ActiveProfileSnapshot();
            FinishEmojiEdit(); bar?.SetItems(emojiConfig.Items);
            EditorStatus("本机默认模板已保存，个人列表未改变。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { EditorStatus("保存模板失败，草稿保留：" + ex.Message, true); }
    }
}
