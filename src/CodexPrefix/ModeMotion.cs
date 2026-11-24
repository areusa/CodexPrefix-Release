using System.Windows;

namespace CodexPrefix;

// 模式过渡的几何与内容节奏独立，可在无窗口的确定性测试中逐帧复核。
internal sealed class ModeMotion
{
    internal readonly MotionSpring X, Y, Width, Height;
    internal bool ToMini { get; private set; }
    internal readonly bool Reduced;
    internal bool Done { get; private set; }
    internal double SourceOpacity { get; private set; } = 1;
    internal double TargetOpacity { get; private set; }
    double elapsed;
    double startSource = 1, startTarget;
    bool skipDelay;
    double fullHeight, miniHeight;
    internal ModeMotion(Rect from, Rect to, bool toMini, bool reduced = false)
    {
        X = new(from.X) { Target = to.X }; Y = new(from.Y) { Target = to.Y };
        Width = new(from.Width) { Target = to.Width }; Height = new(from.Height) { Target = to.Height };
        ToMini = toMini; Reduced = reduced;
        fullHeight = toMini ? from.Height : to.Height; miniHeight = toMini ? to.Height : from.Height;
    }
    static double P(string key) => MotionParameters.Get(key);
    internal IEnumerable<MotionSpring> Springs => new[] { X, Y, Width, Height };
    internal Rect Bounds => new(X.X, Y.X, Width.X, Height.X);
    internal (double X, double Y) Deformation => Reduced ? (1, 1) :
        (1 + Math.Min(Math.Abs(X.V) * P("morphGain") / 18000, P("morphStretch")) - Math.Min(Math.Abs(Y.V) * P("morphGain") / 30000, P("morphCompress")),
         1 + Math.Min(Math.Abs(Y.V) * P("morphGain") / 18000, P("morphStretch")) - Math.Min(Math.Abs(X.V) * P("morphGain") / 30000, P("morphCompress")));
    internal double Radius => Math.Min(Math.Min(Width.X, Height.X) / 2, P("fullRadius") + (P("miniRadius") - P("fullRadius")) * Math.Clamp((fullHeight - Height.X) / Math.Max(1, fullHeight - miniHeight), 0, 1));
    internal void Retarget(Rect to, bool mini)
    {
        double full = ToMini ? SourceOpacity : TargetOpacity, small = ToMini ? TargetOpacity : SourceOpacity;
        ToMini = mini; startSource = mini ? full : small; startTarget = mini ? small : full;
        if (mini) miniHeight = to.Height; else fullHeight = to.Height;
        SourceOpacity = startSource; TargetOpacity = startTarget;
        X.Target = to.X; Y.Target = to.Y; Width.Target = to.Width; Height.Target = to.Height;
        elapsed = 0; Done = false; skipDelay = true;
    }
    internal void Finish() { foreach (var s in Springs) s.Snap(); SourceOpacity = 0; TargetOpacity = 1; Done = true; }
    internal void Step(double dt)
    {
        if (Done) return;
        if (dt > .25 || !double.IsFinite(dt) || dt < 0) { Finish(); return; }
        elapsed += dt;
        double t = elapsed / P("duration");
        double Curve(double v) => 1 - Math.Pow(1 - Math.Clamp(v, 0, 1), P("easePower"));
        SourceOpacity = startSource * (1 - Curve(t / P("exitEnd")));
        TargetOpacity = startTarget + (1 - startTarget) * Curve((t - P("enterStart")) / P("enterLength"));
        if (Reduced)
        {
            double progress = 1 - Math.Pow(1 - Math.Clamp(elapsed / .16, 0, 1), 3);
            SourceOpacity = startSource * (1 - progress); TargetOpacity = startTarget + (1 - startTarget) * progress;
            if (elapsed >= .16) Finish();
            return;
        }
        if (skipDelay || elapsed >= Math.Min(P("delayCap"), P("duration") * P("delayRatio")))
        {
            double step = Math.Min(dt, .035); int n = Math.Max(1, (int)Math.Ceiling(step / .008)); step /= n;
            for (int i = 0; i < n; i++)
            {
                X.Step(step, 190 * P("morphStiffness"), P("morphDamping")); Y.Step(step, 190 * P("morphStiffness"), P("morphDamping"));
                Width.Step(step, 190 * P("morphStiffness"), P("morphDamping"), 12, 8192); Height.Step(step, 190 * P("morphStiffness"), P("morphDamping"), 12, 8192);
            }
        }
        if ((Springs.All(s => Math.Abs(s.X - s.Target) < .1 && Math.Abs(s.V) < .7) && t >= P("settleAfter")) || t > P("morphTimeout")) Finish();
    }
}
