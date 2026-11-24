using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexPrefix;

// 六列目标槽位负责排序，二维弹簧只影响显示；不会用正在晃动的邻项反推槽位。
public sealed class EmojiGridPanel : Panel
{
    sealed class Cell(Button button)
    {
        internal readonly Button Button = button;
        internal readonly MotionSpring X = new(), Y = new(), Scale = new(1);
        internal readonly TranslateTransform Shift = new();
        internal readonly RotateTransform Wiggle = new();
        internal readonly TranslateTransform Jitter = new();
        internal readonly TransformGroup ContentMotion = new();
        internal readonly ScaleTransform FaceScale = new(1, 1);
        internal readonly int Seed = StableSeed(button.Uid);
        internal Point Target;
        internal bool Placed;
        static int StableSeed(string id) { unchecked { int value = 17; foreach (char c in id) value = value * 31 + c; return value & int.MaxValue; } }
    }
    readonly Dictionary<Button, Cell> cells = [];
    readonly List<Button> order = [];
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastFrame;
    bool rendering, editing, cancellingCapture;
    Button? candidate;
    Point down, grab;
    bool dragging;
    internal EmojiViewport? Viewport { get; set; }
    internal bool Editing { get => editing; set { if (editing == value) return; editing = value; CancelGesture(); Wake(); } }
    internal bool Dragging => dragging;
    internal bool HasMovingCells => cells.Values.Any(c => c.X.Moving || c.Y.Moving || c.Scale.Moving);
    internal string? DragId => dragging ? candidate?.Uid : null;
    internal event Action<string>? ToggleDeleted;
    internal event Action<string, int>? Reorder;
    internal event Action? DragBeginning;
    internal event Action<bool>? DragEnded;
    internal event Action? CancelEditing;
    internal IReadOnlyList<Button> OrderedButtons => order;
    public EmojiGridPanel()
    {
        Focusable = false;
        PreviewMouseLeftButtonDown += Down;
        PreviewMouseMove += Move;
        PreviewMouseLeftButtonUp += Up;
        LostMouseCapture += (_, _) => { if (!cancellingCapture && candidate is not null) CancelGesture(); };
        PreviewKeyDown += Key;
        Loaded += (_, _) => Wake(); Unloaded += (_, _) => { CancelGesture(); Stop(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CancelGesture(); Stop(); } else Wake(); };
    }
    internal void SetButtons(IEnumerable<Button> buttons, bool snapImmediate = false)
    {
        var next = buttons.ToArray();
        foreach (var removed in cells.Keys.Except(next).ToArray()) { Children.Remove(removed); cells.Remove(removed); }
        foreach (var button in next)
        {
            if (cells.ContainsKey(button)) continue;
            var cell = new Cell(button); cell.ContentMotion.Children.Add(cell.Wiggle); cell.ContentMotion.Children.Add(cell.Jitter);
            cells.Add(button, cell); button.RenderTransform = cell.Shift;
            Children.Add(button);
        }
        if (snapImmediate) SnapAll();
        var focused = Keyboard.FocusedElement as Button;
        for (int i = 0; i < next.Length; i++) if (!ReferenceEquals(Children[i], next[i])) { Children.Remove(next[i]); Children.Insert(i, next[i]); }
        if (focused is not null && next.Contains(focused) && !ReferenceEquals(Keyboard.FocusedElement, focused)) focused.Focus();
        order.Clear(); order.AddRange(next); InvalidateMeasure(); InvalidateArrange(); Wake();
    }
    internal void SnapAll()
    {
        foreach (var c in cells.Values)
        {
            c.Placed = false;
            c.X.V = c.Y.V = 0;
            c.Scale.Snap(1);
        }
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var button in order) button.Measure(new Size(35, 35));
        return new Size(EmojiLayout.FullListWidth, Math.Max(35, Math.Ceiling(order.Count / 6d) * 39 - 4));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < order.Count; i++)
        {
            var c = cells[order[i]]; c.Target = new Point(i % 6 * 39, i / 6 * 39);
            c.Button.Arrange(new Rect(0, 0, 35, 35));
            if (!c.Placed || !SystemParameters.ClientAreaAnimation) { c.X.Snap(c.Target.X); c.Y.Snap(c.Target.Y); c.Placed = true; }
            c.X.Target = c.Target.X; c.Y.Target = c.Target.Y;
            c.Shift.X = c.X.X; c.Shift.Y = c.Y.X;
        }
        Wake(); return finalSize;
    }
    Button? EventButton(object source)
    {
        var node = source as DependencyObject;
        while (node is not null && node != this) { if (node is Button b && cells.ContainsKey(b)) return b; node = VisualTreeHelper.GetParent(node); }
        return null;
    }
    void Down(object sender, MouseButtonEventArgs e)
    {
        var b = EventButton(e.OriginalSource);
        if (b is null || !b.IsEnabled || b.Uid == "add") return;
        if (!editing) return;
        candidate = b; down = e.GetPosition(this); grab = down - new Vector(cells[b].Shift.X, cells[b].Shift.Y);
        b.Focus(); CaptureMouse(); cells[b].Scale.Target = Math.Clamp(1 - MotionParameters.Get("pressDepth"), MotionParameters.Get("buttonMin"), 1);
        Wake(); e.Handled = true;
    }
    void Move(object sender, MouseEventArgs e)
    {
        if (candidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!dragging && (Math.Abs(p.X - down.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(p.Y - down.Y) >= SystemParameters.MinimumVerticalDragDistance))
        { dragging = true; DragBeginning?.Invoke(); SetZIndex(candidate, 100); cells[candidate].Scale.Target = 1; }
        if (dragging) PlaceDrag(p);
        e.Handled = true;
    }
    void PlaceDrag(Point p)
    {
        if (candidate is null) return;
        var c = cells[candidate]; c.Shift.X = p.X - grab.X; c.Shift.Y = p.Y - grab.Y;
        int col = Math.Clamp((int)Math.Floor(p.X / 39), 0, 5), row = Math.Max(0, (int)Math.Floor(p.Y / 39));
        double x = p.X - col * 39, y = p.Y - row * 39;
        if (x is >= 4 and <= 31 && y is >= 4 and <= 31)
        {
            int slot = Math.Clamp(row * 6 + col, 1, order.Count - 1);
            if (order.IndexOf(candidate) != slot) Reorder?.Invoke(candidate.Uid, slot - 1);
        }
        Wake();
    }
    void Up(object sender, MouseButtonEventArgs e)
    {
        if (candidate is null) return;
        var b = candidate; bool wasDrag = dragging;
        var viewportPoint = Viewport is null ? new Point() : e.GetPosition(Viewport);
        bool inside = Viewport is not null && new Rect(Viewport.RenderSize).Contains(viewportPoint);
        bool hit = !wasDrag && new Rect(0, 0, 35, 35).Contains(e.GetPosition(b));
        EndCapture();
        if (wasDrag) DragEnded?.Invoke(inside);
        else if (hit) { ToggleDeleted?.Invoke(b.Uid); cells[b].Scale.Target = 1; cells[b].Scale.V += 7.2 * MotionParameters.Get("clickStrength"); }
        Wake(); e.Handled = true;
    }
    void EndCapture()
    {
        if (candidate is not null)
        {
            var c = cells[candidate];
            if (dragging) { c.X.X = c.Shift.X; c.Y.X = c.Shift.Y; c.X.V = c.Y.V = 0; }
            SetZIndex(candidate, 0); c.Scale.Target = 1;
        }
        candidate = null; dragging = false; cancellingCapture = true; ReleaseMouseCapture(); cancellingCapture = false;
    }
    internal void CancelGesture()
    {
        bool wasDrag = dragging; EndCapture(); if (wasDrag) DragEnded?.Invoke(false); Wake();
    }
    void Key(object sender, KeyEventArgs e)
    {
        if (!editing) return;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key == System.Windows.Input.Key.Escape)
        { if (candidate is not null) CancelGesture(); else CancelEditing?.Invoke(); e.Handled = true; return; }
        var b = EventButton(e.OriginalSource); if (b is null) return;
        int index = order.IndexOf(b), delta = key switch { System.Windows.Input.Key.Left => -1, System.Windows.Input.Key.Right => 1, System.Windows.Input.Key.Up => -6, System.Windows.Input.Key.Down => 6, _ => 0 };
        if (delta == 0) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && b.Uid != "add") Reorder?.Invoke(b.Uid, Math.Clamp(index - 1 + delta, 0, order.Count - 2));
        else order[Math.Clamp(index + delta, 0, order.Count - 1)].Focus();
        Reveal(b.Uid); e.Handled = true;
    }
    internal void Reveal(string id)
    {
        int i = order.FindIndex(b => b.Uid == id); if (i >= 0) Viewport?.Reveal(i / 6 * 39, i / 6 * 39 + 35);
    }
    void Wake() { if (rendering || !IsVisible || !IsLoaded) return; rendering = true; lastFrame = clock.Elapsed.TotalSeconds; CompositionTarget.Rendering += Tick; }
    void Stop() { if (!rendering) return; rendering = false; CompositionTarget.Rendering -= Tick; }
    internal static (Vector Position, double Angle) EditPose(int seed, double seconds)
    {
        // 稳定种子叠加不同频率，形成连续的不规则错相；不按帧随机跳点。
        double phase = seed % 127, rate = seed % 9 * .12;
        return (new Vector(.8 * (.7 * Math.Sin(seconds * (7.1 + rate) + phase) + .3 * Math.Sin(seconds * (12.7 + rate) + phase * .63)),
            .55 * (.7 * Math.Sin(seconds * (6.3 + rate) + phase * 1.31) + .3 * Math.Sin(seconds * (11.3 + rate) + phase * .47))),
            2.4 * Math.Sin(seconds * (5.5 + rate) + phase));
    }
    void Tick(object? sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalSeconds, dt = now - lastFrame; lastFrame = now;
        if (!IsVisible || dt > .25) { CancelGesture(); foreach (var c in cells.Values) { c.X.Snap(); c.Y.Snap(); c.Scale.Snap(1); } }
        bool animate = SystemParameters.ClientAreaAnimation, active = IsVisible && Window.GetWindow(this)?.IsActive == true;
        if (dragging && candidate is not null && Viewport is not null)
        {
            var at = Mouse.GetPosition(Viewport); double speed = at.Y < 18 ? -180 : at.Y > Viewport.ActualHeight - 18 ? 180 : 0;
            if (speed != 0) { Viewport.ScrollTo(Viewport.Offset + speed * Math.Min(dt, .035), false); PlaceDrag(Mouse.GetPosition(this)); }
        }
        bool moving = false;
        foreach (var c in cells.Values)
        {
            bool visible = c.Target.Y + 35 >= (Viewport?.Offset ?? 0) && c.Target.Y <= (Viewport?.Offset ?? 0) + (Viewport?.ActualHeight ?? 1000);
            if (c.Button != candidate || !dragging)
            {
                if (animate && visible) { c.X.Step(Math.Min(dt, .035), 250, .68); c.Y.Step(Math.Min(dt, .035), 250, .68); }
                else { c.X.Snap(); c.Y.Snap(); }
                c.Shift.X = c.X.X; c.Shift.Y = c.Y.X;
            }
            if (animate && visible) c.Scale.Step(Math.Min(dt, .035), 320 * MotionParameters.Get("buttonStiffness"), MotionParameters.Get("buttonDamping"), MotionParameters.Get("buttonMin"), MotionParameters.Get("buttonMax")); else c.Scale.Snap(1);
            bool wiggle = editing && active && visible && animate && c.Button.Uid != "add" && !(c.Button == candidate && dragging);
            var pose = wiggle ? EditPose(c.Seed, now) : (Position: new Vector(), Angle: 0d);
            c.Wiggle.Angle = pose.Angle; c.Jitter.X = pose.Position.X; c.Jitter.Y = pose.Position.Y;
            if (c.Button.Content is FrameworkElement content) { content.RenderTransformOrigin = new Point(.5, .5); content.RenderTransform = c.ContentMotion; }
            c.Button.ApplyTemplate();
            c.FaceScale.ScaleX = c.FaceScale.ScaleY = c.Scale.X;
            if (c.Button.Template.FindName("MotionFace", c.Button) is FrameworkElement face && !ReferenceEquals(face.RenderTransform, c.FaceScale)) { face.RenderTransformOrigin = new Point(.5, .5); face.RenderTransform = c.FaceScale; }
            moving |= c.X.Moving || c.Y.Moving || c.Scale.Moving;
        }
        if (!moving && !dragging && !(editing && active && animate)) Stop();
    }
    internal void Resume() => Wake();
    internal void ToggleByKeyboard(string id) { ToggleDeleted?.Invoke(id); if (order.FirstOrDefault(b => b.Uid == id) is { } b) { cells[b].Scale.V += 2.2 * MotionParameters.Get("clickStrength"); Wake(); } }
}
