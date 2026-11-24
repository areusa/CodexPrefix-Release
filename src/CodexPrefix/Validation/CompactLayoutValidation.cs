using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexPrefix;

// 只测量和渲染本程序的组件；不截取桌面，也不控制外部窗口。
internal static class CompactLayoutValidation
{
    static Rect[] Bounds(CompactBar bar) => bar.ActionButtons.Select(button => new Rect(button.TranslatePoint(new Point(), bar.Frame), button.RenderSize)).ToArray();
    internal static bool Balanced(CompactBar bar)
    {
        bar.UpdateLayout();
        var frame = bar.Frame;
        var buttons = bar.ActionButtons;
        var bounds = Bounds(bar);
        double scale = VisualTreeHelper.GetDpi(bar).DpiScaleX;
        bool Near(double a, double b) => Math.Abs(a - b) <= 1.01 / scale;
        if (bounds.Length != (bar.Attached ? 10 : 11)) return false;
        for (int i = 0; i < bounds.Length; i++)
        {
            if (!Near(bounds[i].Width, CompactMetrics.ActionSize) || !Near(bounds[i].Height, CompactMetrics.ActionSize) || !Near(bounds[i].Top, 5) || !Near(frame.ActualHeight - bounds[i].Bottom, 5)) return false;
            if (i > 0 && !Near(bounds[i].Left - bounds[i - 1].Right, AutomationProperties.GetName(buttons[i]) == "清除前缀" ? CompactMetrics.SeparatorSlot : CompactMetrics.Gap)) return false;
        }
        return Near(bounds[0].Left, 7) && Near(frame.ActualWidth - bounds[^1].Right, 7);
    }
    static void Layout(CompactBar bar)
    {
        var frame = bar.Frame;
        frame.Measure(new Size(bar.DesignWidth, CompactBar.BarHeight));
        frame.Arrange(new Rect(0, 0, bar.DesignWidth, CompactBar.BarHeight));
        frame.UpdateLayout();
    }
    internal static void Run(Action<bool, string> check)
    {
        var app = Application.Current;
        var originalMode = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var bar = new CompactBar(_ => Task.CompletedTask, () => { }, () => { });
        try
        {
            bar.RenderIcons(); bar.SetAttached(true); Layout(bar);
            check(CompactBar.AttachedWidth == 344 && CompactBar.LostWidth == 376 && CompactBar.BarHeight == 40, "小条外框必须由内容尺寸合计，不能预留不明尾部空白");
            check(Balanced(bar), "吸附模式的尺寸、组内间距与四边留白应符合统一规范");
            var common = (Style)app.FindResource("CompactActionButtonStyle");
            check(bar.AllowsTransparency && bar.Background == Brushes.Transparent && bar.Frame is SmoothBorder, "小条采用透明分层窗口与平滑底框，而非不透明矩形窗口");
            foreach (var button in bar.ActionButtons.Where(button => button.Tag is not string))
            {
                check(button.Content is Viewbox { Width: CompactMetrics.GlyphSize, Height: CompactMetrics.GlyphSize, Child: Canvas { Width: 24, Height: 24 } }, "辅助图标必须缩放原始 24 单位画板到 16 DIP，而非拉伸路径包围盒");
            }
            foreach (var button in bar.ActionButtons)
            {
                check(ReferenceEquals(button.Style, common), "所有小条按钮必须显式使用同一紧凑样式");
                check(button.Padding == new Thickness(0) && button.Margin == new Thickness(0), "间距由布局容器负责，按钮不得私带不对称边距");
                button.ApplyTemplate();
                var body = (Border)button.Template.FindName("BaseFrame", button);
                var hover = (Border)button.Template.FindName("InteractionFrame", button);
                check(body.CornerRadius == CompactMetrics.ButtonRadius && hover.CornerRadius == body.CornerRadius && body.RenderSize == hover.RenderSize, "普通底框与交互底框的圆角和大小必须一致");
            }
            bar.SetActions(true, false); bar.SetOpenEnabled(true); Layout(bar);
            var before = Bounds(bar);
            check(bar.CloseButton.Template.Triggers.OfType<MultiTrigger>().Any(trigger => trigger.Conditions.Cast<System.Windows.Condition>().Any(condition => condition.Property == UIElement.IsMouseOverProperty)), "普通 WPF 鼠标悬停必须直接驱动模板触发器");
            check(bar.CloseButton.Template.Triggers.OfType<MultiTrigger>().Any(trigger => trigger.Conditions.Cast<System.Windows.Condition>().Any(condition => condition.Property == System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty)), "按下必须直接驱动模板触发器");
            CompactMetrics.SetPreviewInteraction(bar.CloseButton, "Hover");
            CompactMetrics.SetPreviewInteraction(bar.CloseButton, "Pressed");
            Layout(bar);
            check(before.SequenceEqual(Bounds(bar)), "悬停和按下不得缩放按钮或改变命中区");
            CompactMetrics.SetPreviewInteraction(bar.CloseButton, "");
            bar.SetActions(false, false); bar.SetOpenEnabled(false);
            check(bar.ActionButtons.Where(button => !button.IsEnabled).All(button => button.Opacity == .2), "普通不可用项应统一降低透明度且保持禁用");
            bar.OpenButton.Style = (Style)app.FindResource("CompactFeedbackButtonStyle");
            bar.OpenButton.Content = new TextBlock { Text = "!", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            Layout(bar);
            check(ReferenceEquals(bar.OpenButton.Template, common.Setters.OfType<Setter>().Single(setter => setter.Property == Control.TemplateProperty).Value), "受阻样式只能继承模板，不能重新定义几何");
            check(bar.OpenButton.Opacity == 1 && !bar.OpenButton.IsEnabled && Balanced(bar), "受阻入口保持清晰但不可点击，不改变尺寸与留白");
            bar.SetAttached(false); Layout(bar);
            check(Balanced(bar), "失联拖动区同样使用标准方形点击区，展开后边距仍平衡");
        }
        finally { bar.Close(); app.ShutdownMode = originalMode; }
    }
    internal static async Task ExportPreview()
    {
        var bar = new CompactBar(_ => Task.CompletedTask, () => { }, () => { });
        VisualTreeHelper.SetRootDpi((Visual)bar.Content, new DpiScale(1.5, 1.5));
        var strips = new List<(string Label, BitmapSource Image)>();
        try
        {
            bar.RenderIcons(1.5); bar.SetAttached(true); Layout(bar);
            var openIcon = bar.OpenButton.Content;
            async Task Capture(string label)
            {
                Layout(bar);
                await Task.Delay(170);
                var image = new RenderTargetBitmap((int)(bar.DesignWidth * 1.5), (int)(CompactBar.BarHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                image.Render(bar.Frame); image.Freeze();
                strips.Add((label, image));
            }
            bar.SetActions(true, false); bar.SetOpenEnabled(false);
            await Capture("就绪：统一 30×30 点击区");
            var emoji = bar.ActionButtons.First(button => button.Tag is string);
            CompactMetrics.SetPreviewInteraction(emoji, "Hover");
            await Capture("emoji 悬停：与辅助按钮使用同一底框");
            CompactMetrics.SetPreviewInteraction(emoji, "");
            CompactMetrics.SetPreviewInteraction(bar.CloseButton, "Hover");
            await Capture("关闭悬停：尺寸、圆角与边距保持一致");
            CompactMetrics.SetPreviewInteraction(bar.CloseButton, "");
            bar.SetActions(false, true); bar.SetOpenEnabled(false);
            await Capture("处理中：统一禁用透明度，不改变布局");
            bar.SetActions(false, false);
            bar.OpenButton.Style = (Style)Application.Current.FindResource("CompactFeedbackButtonStyle");
            bar.OpenButton.Content = new TextBlock { Text = "!", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            await Capture("受阻：仅覆盖语义色和禁用时可见性");
            bar.OpenButton.Style = (Style)Application.Current.FindResource("CompactActionButtonStyle");
            bar.OpenButton.Content = openIcon;
            bar.SetAttached(false);
            await Capture("失联：拖动区同为 30×30，间距仍为 2");
            const int width = 408, height = 456;
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 36)), null, new Rect(0, 0, width, height));
                for (int i = 0; i < strips.Count; i++)
                {
                    var label = new FormattedText(strips[i].Label, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 11, new SolidColorBrush(Color.FromRgb(205, 205, 214)), 1.5);
                    dc.DrawText(label, new Point(16, 10 + i * 74));
                    dc.DrawImage(strips[i].Image, new Rect(16, 29 + i * 74, strips[i].Image.Width, strips[i].Image.Height));
                }
            }
            var board = new RenderTargetBitmap(width * 3 / 2, height * 3 / 2, 144, 144, PixelFormats.Pbgra32);
            board.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(board));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "compact-layout-preview.png")); encoder.Save(file);
        }
        finally { bar.Close(); }
    }
}
