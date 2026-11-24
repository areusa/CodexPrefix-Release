using System.Windows;

namespace CodexPrefix;

// 小条的单一尺寸来源；完整窗口不继承这套紧凑布局。
public static class CompactMetrics
{
    public const double ActionSize = 30, Gap = 2, GlyphSize = 16, EmojiSize = 24;
    public const double FrameBorder = 1, HorizontalPadding = 6, VerticalPadding = 4;
    public const double SeparatorSlot = 14, SeparatorHeight = 20, ActionCorner = 8, FrameCorner = 9;
    public const int ActionCount = 10;
    public const double Height = ActionSize + 2 * (FrameBorder + VerticalPadding);
    public const double AttachedWidth = ActionCount * ActionSize + (ActionCount - 2) * Gap + SeparatorSlot + 2 * (FrameBorder + HorizontalPadding);
    public const double LostWidth = AttachedWidth + ActionSize + Gap;
    public static Thickness Padding => new(HorizontalPadding, VerticalPadding, HorizontalPadding, VerticalPadding);
    public static CornerRadius ButtonRadius => new(ActionCorner);
    // 只供组件渲染验收驱动状态；实际鼠标使用模板的 IsMouseOver / IsPressed 触发器。
    public static readonly DependencyProperty PreviewInteractionProperty = DependencyProperty.RegisterAttached("PreviewInteraction", typeof(string), typeof(CompactMetrics), new PropertyMetadata(""));
    public static string GetPreviewInteraction(DependencyObject target) => (string)target.GetValue(PreviewInteractionProperty);
    public static void SetPreviewInteraction(DependencyObject target, string value) => target.SetValue(PreviewInteractionProperty, value);
}
