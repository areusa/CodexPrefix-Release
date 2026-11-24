using System.Text;

namespace CodexPrefix;

internal static class SelfTests
{
    internal static string Run()
    {
        var report = new StringBuilder();
        var count = 0;
        void Equal(string expected, string actual)
        {
            if (actual != expected) throw new InvalidOperationException($"预期「{expected}」，实际「{actual}」");
            count++;
        }
        try
        {
            foreach (var first in PrefixRules.Icons)
            {
                Equal(first + " 材质整理", PrefixRules.Apply("材质整理", first));
                Equal(first + " 材质整理", PrefixRules.Apply(first + " 材质整理", first));
                Equal("材质整理", PrefixRules.Apply(first + " 材质整理", null));
                foreach (var second in PrefixRules.Icons)
                    Equal(second + " 材质整理", PrefixRules.Apply(first + " 材质整理", second));
            }
            Equal("原名", PrefixRules.Apply("原名", null));
            Equal("📄  正文  尾部 ", PrefixRules.Apply("💭  正文  尾部 ", "📄"));
            Equal("文中 💭 图标 ✌️", PrefixRules.Apply("文中 💭 图标 ✌️", null));
            Equal("○", PrefixRules.Apply("○", null));
            Equal("💭\u00a0正文", PrefixRules.Apply("💭\u00a0正文", null));
            Equal("✌ 正文", PrefixRules.Apply("✌ 正文", null));
            Equal("📄 💭正文", PrefixRules.Apply("💭正文", "📄"));
            foreach (var icon in PrefixRules.Icons) Equal(icon, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(icon)));
            void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); count++; }
            CompatibilityValidation.Run(Check);
            UpgradeValidation.Run(Check);
            var binding = new EditorBinding((nint)101, 999, "原生框A", "输入框A", "保存A", "同名会话");
            Check(Recognition.IsSameBinding(binding, binding with { }), "相同绑定应通过");
            Check(!Recognition.IsSameBinding(binding, binding with { EditorId = "输入框B" }), "同名但不同输入框不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { ScopeId = "原生框B" }), "不同重命名框不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { Window = (nint)102 }), "同名但不同窗口不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { ProcessId = 998 }), "不同进程不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { SaveId = "保存B" }), "保存操作替换后不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { OriginalValue = "用户刚修改的内容" }), "内容变化后不能通过");
            Check(Recognition.IsRenameLabel("重命名聊天"), "应识别中文语义");
            Check(Recognition.IsRenameLabel("Rename thread…"), "应识别英文省略号");
            foreach (var text in new[] { "名称", "搜索", "同名会话", "Conversation", "Draggable item codex:thread:local:abc" }) Check(!Recognition.IsRenameLabel(text), "不能按输入值或播报推断重命名框");
            Check(Recognition.IsUnambiguousRenameScope(true, true, 1, 1, true), "唯一完整范围应通过");
            Check(!Recognition.IsUnambiguousRenameScope(false, true, 1, 1, true), "非对话框必须拒绝");
            Check(!Recognition.IsUnambiguousRenameScope(true, false, 1, 1, true), "没有重命名语义必须拒绝");
            Check(!Recognition.IsUnambiguousRenameScope(true, true, 2, 1, true), "多个输入框必须拒绝");
            Check(!Recognition.IsUnambiguousRenameScope(true, true, 1, 2, true), "多个保存按钮必须拒绝");
            Check(!Recognition.IsUnambiguousRenameScope(true, true, 1, 0, true), "没有保存按钮必须拒绝");
            Check(!Recognition.IsUnambiguousRenameScope(true, true, 1, 1, false), "截断扫描必须拒绝");
            Check(!Recognition.IsCurrentMarker("class=data-selected current=false"), "不能将 CSS 或假值作为选中");
            Check(Recognition.IsCurrentMarker("readonly=true;current=page"), "应识别显式 Aria 状态，但它不证明业务身份");
            foreach (var text in new[] { "", "  ", "标题\n第二行", "标题\r第二行", new string('字', 2049) }) Check(!Recognition.IsValidTitle(text), "无效文本必须拒绝");
            Check(Recognition.IsValidTitle("💭 同名会话 ✌️"), "应保留完整字符");
            Check(Recognition.IsTrustedHostDocument(true, true), "主文档及应用结构证据同时存在才可通过");
            Check(!Recognition.IsTrustedHostDocument(false, true), "嵌套网页不能因仿造类名通过");
            Check(!Recognition.IsTrustedHostDocument(true, false), "只有文档标题不能证明应用上下文");
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                foreach (var icon in PrefixRules.Icons)
                {
                    var image = NativeEmoji.Render(icon, scale);
                    var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
                    image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                    Check(image.PixelWidth == (int)Math.Ceiling(28 * scale), "彩色字形应匹配显示缩放");
                    Check(Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] > 0), "字形不能渲染为空");
                    Check(pixels[3] == 0, "字形边角应透明");
                    if (icon != "○") Check(Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] > 100 && Math.Max(pixels[i * 4], Math.Max(pixels[i * 4 + 1], pixels[i * 4 + 2])) - Math.Min(pixels[i * 4], Math.Min(pixels[i * 4 + 1], pixels[i * 4 + 2])) > 20), "应存在系统原生彩色字形像素");
                }
            var pin = new System.Windows.Controls.CheckBox { Style = (System.Windows.Style)System.Windows.Application.Current.FindResource(typeof(System.Windows.Controls.CheckBox)), IsChecked = true };
            pin.ApplyTemplate();
            var pinBorder = (System.Windows.Controls.Border)pin.Template.FindName("PinBox", pin);
            Check(pin.FocusVisualStyle is null, "置顶开关不应出现系统焦点装饰框");
            Check(pinBorder.BorderThickness == new System.Windows.Thickness(0), "置顶开关不应绘制外边框");
            Check(pin.Template.Triggers.OfType<System.Windows.Trigger>().Any(x => x.Property == System.Windows.UIElement.IsMouseOverProperty), "置顶开关应有与相邻按钮一致的 hover 背景");
            Check(pin.Template.Triggers.OfType<System.Windows.Trigger>().Any(x => x.Property == System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty), "置顶开关应有按压反馈");
            Check(Recognition.IsMenuLabel("更多操作") && Recognition.IsMenuLabel("More options"), "应识别明确的菜单入口");
            Check(!Recognition.IsMenuLabel("同名会话") && !Recognition.IsMenuLabel("搜索"), "不能把会话标题或搜索当作菜单入口");
            Check(OpenRenameBlockedException.Explain("SIDEBAR_TRUNCATED").Contains("侧边栏识别未完成"), "应明确显示真实截断原因而非只显示感叹号");
            Check(OpenRenameBlockedException.Explain("MENU_BUTTON_UNPROVEN").Contains("菜单操作"), "应区分条目扫描与菜单能力失败");
            Check(OpenRenameBlockedException.Explain("未知错误").Contains("手动右键"), "未知错误也应保留保守手动指引");
            Check(CodexBridge.IsTransientOpenError(new System.Runtime.InteropServices.COMException("测试", unchecked((int)0x80040201))), "控件失效应允许只读重新发现");
            Check(!CodexBridge.IsTransientOpenError(new System.Runtime.InteropServices.COMException("测试", unchecked((int)0x80070005))), "权限拒绝不能当作控件重建重试");
            Check(!CodexBridge.IsTransientOpenError(new InvalidOperationException("身份变化")), "身份变化不能当作瞬态错误忽略");
            Check(System.Runtime.InteropServices.Marshal.SizeOf<RenameShortcut.Input>() == 40, "Windows x64 INPUT 布局必须正确");
            Check(string.Join(",", RenameShortcut.Sequence().Select(i => i.Data.Keyboard.Key)) == "17,18,82,82,18,17", "快捷键只发送 Ctrl Alt R 并逆序释放");
            Check(RenameShortcut.Sequence().Take(3).All(i => i.Data.Keyboard.Flags == 0) && RenameShortcut.Sequence().Skip(3).All(i => i.Data.Keyboard.Flags == 2), "不能遗漏快捷键释放或包含文本输入");
            Check(!pin.Template.Triggers.OfType<System.Windows.Trigger>().Any(x => x.Property == System.Windows.UIElement.IsKeyboardFocusedProperty), "置顶模板不应按焦点绘制选中框");
            CompactLayoutValidation.Run(Check);
            EmojiValidation.Rules(Check);
            FeedbackValidation.Rules(Check);
            MotionValidation.Rules(Check);
            MotionSamplingValidation.Rules(Check);
            MotionValidation.Offscreen(Check);
            SharedLayoutValidation.Run(Check);
            CornerValidation.Run(Check);
            IconValidation.Run(Check);
            Check(WindowIdentity.IsCodexPackage("OpenAI.Codex_2p2nqsd0c76g0", "ChatGPT.exe"), "应识别官方 Codex 安装身份");
            Check(!WindowIdentity.IsCodexPackage("OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0", "ChatGPT.exe"), "不能把同名 ChatGPT 进程当作 Codex");
            Check(!WindowIdentity.IsCodexPackage("OpenAI.Codex_wrong", "ChatGPT.exe"), "发布者标识不符必须拒绝");
            Check(!WindowIdentity.IsCodexPackage("OpenAI.Codex_2p2nqsd0c76g0", "untrusted.exe"), "包内非主应用进程必须拒绝");
            foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2, 3 })
                foreach (int origin in new[] { -2560, 0, 2560 })
                {
                    var frame = new TrackingNative.Rect(origin + 100, 50, origin + 1500, 950);
                    var work = new TrackingNative.Rect(origin, 0, origin + 1920, 1080);
                    var placed = WindowTracker.Place(frame, work, scale, 304, 40);
                    Check(Math.Abs(placed.Left + placed.Right - frame.Left - frame.Right) <= 1, "小条应按物理像素居中，兼容负屏幕坐标");
                    Check(placed.Right - placed.Left == (int)Math.Round(304 * scale), "小条宽度应按宿主 DPI 换算");
                    Check(placed.Top >= work.Top && placed.Bottom <= work.Bottom, "小条顶边应保留在工作区内");
                }
            var tiny = WindowTracker.Place(new(-2000, -8, -1850, 900), new(-1920, 0, 0, 1040), 1.5, 304, 40);
            Check(tiny.Left >= -1920 && tiny.Top >= 0 && tiny.Right <= 0, "极窄和最大化边界不得把小条推到屏幕之外");
            report.AppendLine($"通过：{count} 项规则、身份隔离、UTF-8、彩色字形、置顶样式及 Windows 图标测试。");
            report.AppendLine("真实 Codex 重命名框：未直接操作。独立桌面端到端结果另见验收记录。");
        }
        catch (Exception ex) { report.AppendLine("失败：" + ex.Message); }
        return report.ToString();
    }
}
