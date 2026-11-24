using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexPrefix;

// 直接检查产品的共用外壳，而不是仅检查独立 CompactBar 窗口。
internal static class SharedLayoutValidation
{
    internal static void Run(Action<bool, string> check)
    {
        var app = Application.Current; var previous = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var rows = new List<object>(); var assertions = new List<(bool Pass, string Text)>();
        try
        {
            foreach (double dpi in new[] { 1d, 1.25, 1.5, 2d })
            {
                var window = new Window();
                var bar = new CompactBar(_ => Task.CompletedTask, () => { }, () => { });
                try
                {
                    var host = new LivePanelHost(window, new SmoothBorder { BorderThickness = new Thickness(1), Child = new Grid() }, bar);
                    bar.SetAttached(true); bar.Motion.Start();
                    host.Apply(new Rect(96, 32, 344, 40), 0, 1, 9, Transform.Identity, true);
                    var root = host.Root; VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
                    root.Measure(new Size(536, 104)); root.Arrange(new Rect(0, 0, 536, 104)); root.UpdateLayout();
                    foreach (bool deform in new[] { false, true })
                    {
                        double hoverScale = MotionParameters.Get("hoverScale");
                        var p = bar.Motion.Physics; p.Hover.Snap(hoverScale);
                        p.X.Snap(deform ? 12 : 0); p.Y.Snap(deform ? 1 : 0);
                        p.StretchX.Snap(deform ? .03 : 0); p.StretchY.Snap(deform ? -.02 : 0); p.Skew.Snap(deform ? 3 : 0);
                        bar.Motion.ApplyPose(); root.UpdateLayout();
                        (Vector X, Vector Y) Axes(Visual visual)
                        {
                            var t = visual.TransformToAncestor(root); var origin = t.Transform(new Point());
                            return (t.Transform(new Point(1, 0)) - origin, t.Transform(new Point(0, 1)) - origin);
                        }
                        var shell = Axes(host.Shell); var content = Axes(host.MiniContent);
                        rows.Add(new { dpi, deform, shellScaleX = shell.X.Length, contentScaleX = content.X.Length, shellScaleY = shell.Y.Length, contentScaleY = content.Y.Length });
                        assertions.Add((((shell.X - content.X).Length + (shell.Y - content.Y).Length) < 1e-9, "共用外壳与 mini 内容必须只经历同一次整体缩放、剪切，不得重复变换"));
                        assertions.Add((host.MiniContent.RenderTransform.Value.IsIdentity, "共用宿主后旧 mini 根容器必须清除整体变换"));
                        if (!deform) assertions.Add((Math.Abs(content.X.Length - hoverScale) < 1e-9, $"实际内容 hover 必须为 {hoverScale:0.00} 倍，不得重复叠加缩放"));
                    }
                }
                finally { bar.Close(); window.Close(); }
            }
        }
        finally { app.ShutdownMode = previous; }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shared-layout-validation.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
        foreach (var result in assertions) check(result.Pass, result.Text);
    }
}
