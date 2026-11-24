using System.Text.Json;

namespace CodexPrefix;

// 与 HTML 共用展示单位；资源内嵌，便携 EXE 无需伴随配置文件。
internal static class MotionParameters
{
    internal static readonly IReadOnlyDictionary<string, double> Values = Load();
    internal static double Get(string key) => Values[key];
    static Dictionary<string, double> Load()
    {
        using var stream = typeof(MotionParameters).Assembly.GetManifestResourceStream("CodexPrefix.Motion.Calibration.json")!;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("displayParameters").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble());
    }
}

internal sealed class MotionSpring(double value = 0)
{
    internal double X = value, V, Target = value;
    internal void Snap(double? value = null) { X = Target = value ?? Target; V = 0; }
    internal bool Moving => Math.Abs(V) > .002 || Math.Abs(X - Target) > .0001;
    // 阻尼振子解析解；单位为秒。避免高刚度、过阻尼时显式积分失稳。
    internal void Step(double dt, double stiffness, double damping, double min = -100000, double max = 100000)
    {
        double y = X - Target, next, velocity, w = Math.Sqrt(stiffness);
        if (stiffness == 0) { next = y + V * dt; velocity = V; }
        else if (damping < 1 - .000001)
        {
            double a = damping * w, b = w * Math.Sqrt(1 - damping * damping), e = Math.Exp(-a * dt), c = Math.Cos(b * dt), s = Math.Sin(b * dt);
            next = e * (y * c + (V + a * y) / b * s);
            velocity = e * (V * c - (a * V + stiffness * y) / b * s);
        }
        else if (damping > 1 + .000001)
        {
            double q = Math.Sqrt(damping * damping - 1), r1 = -w / (damping + q), r2 = -w * (damping + q);
            double a = (V - r2 * y) / (r1 - r2), b = y - a, e1 = Math.Exp(r1 * dt), e2 = Math.Exp(r2 * dt);
            next = a * e1 + b * e2; velocity = a * r1 * e1 + b * r2 * e2;
        }
        else
        {
            double e = Math.Exp(-w * dt), q = V + w * y;
            next = (y + q * dt) * e; velocity = (V - w * q * dt) * e;
        }
        X = Math.Clamp(Target + next, min, max); V = velocity;
        if ((X <= min && V < 0) || (X >= max && V > 0)) V = 0;
        if (!double.IsFinite(X) || !double.IsFinite(V)) Snap();
        if (Math.Abs(X - Target) < .0001 && Math.Abs(V) < .001) Snap();
    }
}

internal sealed class MiniMotion(int count)
{
    internal readonly MotionSpring X = new(), Y = new(), StretchX = new(), StretchY = new(), Skew = new(), Hover = new(1);
    internal readonly MotionSpring[] Scale = Enumerable.Range(0, count).Select(_ => new MotionSpring(1)).ToArray();
    internal readonly MotionSpring[] Shift = Enumerable.Range(0, count).Select(_ => new MotionSpring()).ToArray();
    readonly List<(double At, int Index, double Velocity)> waves = [];
    internal int Pressed = -1;
    internal double BudgetUntil;
    internal int WaveCount => waves.Count;
    internal IEnumerable<MotionSpring> Springs => new[] { X, Y, StretchX, StretchY, Skew, Hover }.Concat(Scale).Concat(Shift);
    internal bool Moving => waves.Count > 0 || Springs.Any(s => s.Moving);
    static double P(string name) => MotionParameters.Get(name);
    internal void Reset()
    {
        X.Snap(0); Y.Snap(0); StretchX.Snap(0); StretchY.Snap(0); Skew.Snap(0); Hover.Snap(1);
        foreach (var s in Scale) s.Snap(1);
        foreach (var s in Shift) s.Snap(0);
        waves.Clear(); Pressed = -1; BudgetUntil = 0;
    }
    internal void Hovering(bool inside, double now) { Hover.Target = inside ? P("hoverScale") : 1; Renew(now); }
    void Renew(double now) => BudgetUntil = now + P("motionBudget");
    internal void Press(int index, double now)
    {
        Pressed = index; Scale[index].Target = Math.Clamp(1 - P("pressDepth"), P("buttonMin"), 1); Renew(now);
    }
    internal void Release() { if (Pressed >= 0) Scale[Pressed].Target = 1; Pressed = -1; }
    internal void Click(int index, double now, bool keyboard = false)
    {
        Scale[index].Target = 1; Scale[index].V += (keyboard ? 2.2 : 7.2) * P("clickStrength");
        for (int j = 0; j < Scale.Length; j++) if (j != index)
        {
            int d = Math.Abs(j - index);
            waves.Add((now + d * P("lag"), j, Math.Sign(j - index) * P("neighbor") * 17 * Math.Exp(-d * P("waveDecay"))));
        }
        Renew(now);
    }
    internal void Swipe(double vx, double vy, double dt, bool inside, double now)
    {
        vx = Math.Clamp(vx, -P("pointerSpeed") * 1000, P("pointerSpeed") * 1000);
        vy = Math.Clamp(vy, -P("pointerSpeed") * 1000, P("pointerSpeed") * 1000);
        // 沿用 60Hz 标定；采样转为逐帧后，形变和剪切也须按时间计量，避免高刷重复加力。
        double sampleWeight = Math.Min(dt * 60, 1.4);
        double gain = P("swipe") * .018 * sampleWeight;
        double energy = Math.Min(1, Math.Sqrt(vx * vx + vy * vy) / (P("saturationSpeed") * 1000)) * P("swipe") * sampleWeight;
        double ratio = inside ? P("internalForceRatio") : 1;
        void Add(MotionSpring s, double impulse, double limit) => s.V += (Math.Clamp(s.V + impulse, -limit, limit) - s.V) * ratio;
        Add(X, vx * gain, 105 * P("impulseLimit")); Add(Y, vy * gain, 85 * P("impulseLimit"));
        Add(StretchX, Math.Abs(vx) > Math.Abs(vy) ? energy * .36 * P("stretchGain") : -energy * .18 * P("compressGain"), 64);
        Add(StretchY, Math.Abs(vy) > Math.Abs(vx) ? energy * .4 * P("stretchGain") : -energy * .2 * P("compressGain"), 64);
        Add(Skew, vx * .00012 * P("skewGain") * sampleWeight, 1.1 * P("skewGain")); Renew(now);
    }
    internal void Step(double elapsed, double now)
    {
        double dt = Math.Min(elapsed, .035); int steps = Math.Max(1, (int)Math.Ceiling(dt / .008)); dt /= steps;
        for (int n = 0; n < steps; n++)
        {
            X.Step(dt, 260 * P("positionStiffness"), P("damping"), -P("limitX"), P("limitX"));
            Y.Step(dt, 260 * P("positionStiffness"), P("damping"), -P("limitY"), P("limitY"));
            StretchX.Step(dt, 210 * P("deformStiffness"), P("damping"), -P("compressLimit"), P("stretchLimit"));
            StretchY.Step(dt, 210 * P("deformStiffness"), P("damping"), -P("compressLimit"), P("stretchLimit"));
            Skew.Step(dt, 240 * P("skewStiffness"), P("damping"), -P("skewLimit"), P("skewLimit"));
            Hover.Step(dt, 330 * P("hoverStiffness"), P("hoverDamping"), 1, Math.Min(2, 1 + 2 * (P("hoverScale") - 1)));
            for (int i = 0; i < Scale.Length; i++)
            {
                Scale[i].Step(dt, 320 * P("buttonStiffness"), P("buttonDamping"), P("buttonMin"), P("buttonMax"));
                Shift[i].Step(dt, 260 * P("neighborStiffness"), P("neighborDamping"), -P("neighborLimit"), P("neighborLimit"));
            }
        }
        if (BudgetUntil > 0 && now > BudgetUntil && Pressed == -1) { foreach (var s in Springs) s.Snap(); waves.Clear(); BudgetUntil = 0; }
        // 与浏览器逐帧顺序一致：到期波在本帧积分后注入，下一帧才产生位移。
        foreach (var wave in waves.Where(w => w.At <= now).Reverse().ToArray()) { Shift[wave.Index].V += wave.Velocity; waves.Remove(wave); }
    }
}
