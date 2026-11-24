using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexPrefix;

internal static class EmojiValidation
{
    internal static void Rules(Action<bool, string> check)
    {
        foreach (var token in new[] { "💦", "✌", "✌️", "👍🏽", "👩‍💻", "👨‍👩‍👧‍👦", "🇨🇳", "1️⃣", "○", "🏴\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F" })
            check(EmojiCatalog.TryIdentity(token, out _), "完整 emoji 序列和既有兼容项可添加：" + token);
        foreach (var token in new[] { "", "a", "1", "#", " ", "💦 ", "💦💭", "💦\n", "🏽", "\uD800", "\u200D", "\uFE0F", "👩\u200D" })
            check(!EmojiCatalog.TryIdentity(token, out _), "非法输入不能静默截断或替换");
        EmojiCatalog.TryIdentity("✌", out var a); EmojiCatalog.TryIdentity("✌️", out var b);
        check(a == b, "资格变体只在配置去重身份中合并");
        var draft = new EmojiDraft(PrefixSnapshot.Default);
        draft.Add("👩‍💻", out var added);
        check(draft.Items[^1].Added && draft.Items[^1].Item.Id == added, "新增项保留稳定身份与草稿角标");
        draft.Move(added, 0); draft.Items[0].Deleted = true;
        check(!draft.Add("👩‍💻", out var recovered) && recovered == added && !draft.Items[0].Deleted, "重复待删项恢复原身份");
        draft.Items[0].Deleted = true; var saved = draft.Commit();
        check(saved.Items.All(i => i.Id != added) && !saved.History.Contains("👩‍💻"), "新增后待删除不登记历史");
        var use = new EmojiDraft(PrefixSnapshot.Default); use.Add("👩‍💻", out added); saved = use.Commit();
        var del = new EmojiDraft(saved); del.Items.Single(i => i.Item.Id == added).Deleted = true; var deleted = del.Commit();
        check(PrefixRules.Apply("👩‍💻 标题  ", "💭", deleted) == "💭 标题  ", "删除按钮后历史前缀仍能被替换");
        check(PrefixRules.Apply("👩‍💻 标题  ", null, deleted) == "标题  ", "历史清除逐字保留正文");
        check(!PrefixRules.CanChange("💭 标题", "💭", deleted) && PrefixRules.CanChange("正文 💭", "💭", deleted), "无效判定不把正文 emoji 当前缀");
        check(!PrefixRules.CanChange("💭 ", null, deleted), "清除后空白标题不可写入");
        check(!PrefixRules.CanChange(new string('字', 2048), "💭", deleted), "新序列不得使标题超过现有 UTF-16 限制");
        foreach (var n in new[] { 0, 1, 6, 7, 8, 9, 12, 13, 18, 19, 20, 100, 200 })
        {
            check(EmojiLayout.MiniViewport(n) >= 0 && EmojiLayout.MiniWidth(n, false) - EmojiLayout.MiniWidth(n, true) == 32, "变量数量的 mini 几何非负且失联槽独立");
            check(EmojiLayout.FullViewport(n) <= 134.5, "完整列表高度封顶，数量不缩小单元");
        }
        check(EmojiLayout.MiniViewport(6) == 190 && EmojiLayout.MiniViewport(8) == 254 && EmojiLayout.MiniViewport(9) == 271, "半格公式以按钮本体计算");
        check(EmojiLayout.FullViewport(18) == 113 && EmojiLayout.FullViewport(19) == 134.5, "添加入口跨越 18/19 边界");
        var wiggles = Enumerable.Range(0, 1200).Select(i => EmojiGridPanel.EditPose(17, i / 120d)).ToArray();
        check(wiggles.All(p => Math.Abs(p.Position.X) <= .8 && Math.Abs(p.Position.Y) <= .55 && Math.Abs(p.Angle) <= 2.4)
            && wiggles.Max(p => Math.Abs(p.Position.X)) > .5 && wiggles.Max(p => Math.Abs(p.Angle)) > 2.2,
            "编辑晃动含有限幅的位置变化，角度幅度明显高于旧值");
        check(Enumerable.Range(0, 1200).All(i => (EmojiGridPanel.EditPose(17, (i + 1) / 120d).Position - EmojiGridPanel.EditPose(17, i / 120d).Position).Length < .12)
            && EmojiGridPanel.EditPose(17, 1) != EmojiGridPanel.EditPose(18, 1), "编辑位置抖动平滑连续且不同项目错相，不按帧随机跳点");
        var folder = Path.Combine(Path.GetTempPath(), "CodexPrefix-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new EmojiConfigurationStore(folder); check(store.Load().Items.Count == 6, "无配置加载默认六项");
            store.Save(saved); var before = File.ReadAllBytes(store.FilePath);
            var loaded = new EmojiConfigurationStore(folder).Load();
            check(loaded.Items.Select(i => i.Id).SequenceEqual(saved.Items.Select(i => i.Id)) && loaded.History.Contains("👩‍💻"), "重启恢复排序、身份和完整历史");
            store.BeforeReplace = _ => throw new IOException("模拟原子替换失败");
            bool rejected = false; try { store.Save(deleted); } catch (IOException) { rejected = true; }
            check(rejected && before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "替换失败保留原文件，不能部分提交");
            check(!Directory.EnumerateFiles(folder, "*.tmp-*").Any(), "失败的本次临时文件清理完成");
            store.BeforeReplace = null;
            store.Save(deleted.WithItems(Array.Empty<EmojiItem>()));
            check(new EmojiConfigurationStore(folder).Load().Items.Count == 0, "有效空列表不是损坏配置");
            check(File.Exists(store.FilePath + ".bak"), "原子保存保留上个版本备份");
            foreach (var bad in new[] { "{", "{}", "{\"SchemaVersion\":99}", "{\"SchemaVersion\":1,\"UnicodeVersion\":\"17.0\",\"Revision\":0,\"Items\":[{\"Id\":\"a\",\"Token\":\"\\ud800\"}],\"History\":[]}" })
            {
                File.WriteAllText(store.FilePath, bad, new UTF8Encoding(true)); var invalid = new EmojiConfigurationStore(folder); invalid.Load();
                check(invalid.RecoveryRequired && File.ReadAllText(store.FilePath) == bad, "坏配置必须保留原件并要求显式恢复");
                rejected = false; try { invalid.Save(PrefixSnapshot.Default); } catch (InvalidOperationException) { rejected = true; }
                check(rejected, "普通保存不能覆盖未确认恢复的配置");
            }
            var recovery = new EmojiConfigurationStore(folder); recovery.Load(); recovery.PermitRecovery(); recovery.Save(PrefixSnapshot.Default);
            check(Directory.EnumerateFiles(folder, "*.invalid-*").Any() && !new EmojiConfigurationStore(folder).Load().Items.Except(PrefixSnapshot.Default.Items).Any(), "显式恢复先备份再保存默认配置");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        foreach (double dpi in new[] { 1d, 1.25, 1.5, 2 })
        {
            foreach (var token in new[] { "👩‍💻", "👨‍👩‍👧‍👦", "🇨🇳", "1️⃣", "👍🏽" })
            {
                var image = NativeEmoji.RenderFitted(token, dpi);
                check(image.IsFrozen && image.PixelWidth == (int)Math.Ceiling(24 * dpi), "复合序列先完整测量再适配槽位");
                check(ReferenceEquals(image, NativeEmoji.RenderFitted(token, dpi)), "相同序列/DPI 复用冻结字形缓存");
            }
        }
    }
}
