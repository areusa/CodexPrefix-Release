using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace CodexPrefix;

// 仅显式开启时采样。环形缓冲提前分配，输入/渲染线程不序列化、不写盘。
internal sealed class MotionCapture
{
    const int Capacity = 20000, Stride = 192;
    readonly MotionRow[] rows = new MotionRow[Capacity];
    readonly double[] springs = new double[Capacity * Stride];
    readonly DateTimeOffset started = DateTimeOffset.Now;
    readonly long startTicks = Stopwatch.GetTimestamp();
    readonly long allocatedStart = GC.GetTotalAllocatedBytes(false);
    readonly int[] gcStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    int count;
    long costTicks, maximumCost;
    internal bool Active { get; private set; } = true;
    internal int Count => count;
    internal void Record(double now, string kind, string reason, double dt, int epoch, Point? p, Point? before,
        Rect stable, Matrix matrix, bool guarded, bool needsExit, bool fresh, bool transitioning, bool dragging,
        double suppressUntil, bool rendering, bool pending, MiniMotion physics, string source = "")
    {
        if (!Active) return;
        long begin = Stopwatch.GetTimestamp();
        int slot = count++ % Capacity, offset = slot * Stride, n = 0;
        void Save(MotionSpring s) { if (n + 3 > Stride) return; springs[offset + n++] = s.X; springs[offset + n++] = s.V; springs[offset + n++] = s.Target; }
        Save(physics.X); Save(physics.Y); Save(physics.StretchX); Save(physics.StretchY); Save(physics.Skew); Save(physics.Hover);
        for (int i = 0; i < physics.Scale.Length; i++) Save(physics.Scale[i]);
        for (int i = 0; i < physics.Shift.Length; i++) Save(physics.Shift[i]);
        // 坐标相对静止条左上角，避免记录用户屏幕绝对位置。
        rows[slot] = new(now, kind, reason, dt * 1000, epoch, p.HasValue, p?.X - stable.X ?? 0, p?.Y - stable.Y ?? 0,
            before.HasValue, before?.X - stable.X ?? 0, before?.Y - stable.Y ?? 0, stable.Width, stable.Height,
            matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.OffsetX, matrix.OffsetY,
            guarded, needsExit, fresh, transitioning, dragging, Math.Max(0, suppressUntil - now) * 1000,
            rendering, pending, physics.Pressed, physics.WaveCount, physics.BudgetUntil, n, physics.Scale.Length, source);
        long cost = Stopwatch.GetTimestamp() - begin; costTicks += cost; maximumCost = Math.Max(maximumCost, cost);
    }
    internal MotionCaptureMetrics Measure()
    {
        int samples = 0, frames = 0, late = 0, accepted = 0, waiting = 0;
        double maximumInterval = 0;
        var sources = new Dictionary<string, int>();
        for (int i = Math.Max(0, count - Capacity); i < count; i++)
        {
            var row = rows[i % Capacity];
            if (row.Kind == "帧前") frames++;
            if (row.Kind != "采样") continue;
            samples++; sources[row.Source] = sources.GetValueOrDefault(row.Source) + 1;
            if (row.Reason == "已施力") accepted++;
            if (row.Reason == "间隔超过80ms") late++;
            if (row.Reason == "等待移出") waiting++;
            if (row.HasPrevious) maximumInterval = Math.Max(maximumInterval, row.IntervalMs);
        }
        return new(count, samples, frames, late, accepted, waiting, maximumInterval, sources);
    }
    internal async Task<string> Finish(string folder, object environment)
    {
        Active = false;
        double duration = Stopwatch.GetElapsedTime(startTicks).TotalSeconds;
        long allocated = GC.GetTotalAllocatedBytes(false) - allocatedStart;
        int[] collections = [GC.CollectionCount(0) - gcStart[0], GC.CollectionCount(1) - gcStart[1], GC.CollectionCount(2) - gcStart[2]];
        int total = count;
        return await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(Path.Combine(folder, "metadata.json"), JsonSerializer.Serialize(new
            {
                started, durationSeconds = duration, totalRecords = total, retainedRecords = Math.Min(total, Capacity), overwritten = Math.Max(0, total - Capacity),
                recordCostMeanMicroseconds = total == 0 ? 0 : costTicks * 1e6 / Stopwatch.Frequency / total,
                recordCostMaxMicroseconds = maximumCost * 1e6 / Stopwatch.Frequency,
                processAllocatedBytes = allocated, collections, environment,
                parameters = MotionParameters.Values,
                springColumns = "依次 X/Y/StretchX/StretchY/Skew/Hover，再各按钮 Scale、各按钮 Shift；每项为位置、速度、目标",
                coordinates = "光标相对未变形条左上角，单位 DIP；Matrix 为整体形变，顶部中心为变换原点",
                note = "帧后表示 WPF 回调完成，不是 DWM 已呈现。没有轨迹注入，没有读取或改写真实 Codex 会话。"
            }, options), new UTF8Encoding(true));
            var eventOptions = new JsonSerializerOptions(options) { WriteIndented = false };
            using var writer = new StreamWriter(Path.Combine(folder, "events.jsonl"), false, new UTF8Encoding(true));
            for (int i = Math.Max(0, total - Capacity); i < total; i++)
            {
                int slot = i % Capacity;
                writer.WriteLine(JsonSerializer.Serialize(new { sequence = i, state = rows[slot], springs = springs.AsSpan(slot * Stride, rows[slot].SpringValues).ToArray() }, eventOptions));
            }
            return folder;
        });
    }
    readonly record struct MotionRow(double Seconds, string Kind, string Reason, double IntervalMs, int Epoch,
        bool HasPointer, double PointerX, double PointerY, bool HasPrevious, double PreviousX, double PreviousY,
        double Width, double Height, double M11, double M12, double M21, double M22, double OffsetX, double OffsetY,
        bool Guarded, bool NeedsExit, bool Fresh, bool Transitioning, bool Dragging, double ProtectionMs,
        bool Rendering, bool PendingAction, int Pressed, int Waves, double BudgetUntil, int SpringValues, int ButtonCount, string Source);
}

internal sealed record MotionCaptureMetrics(int Records, int Samples, int Frames, int Over80Ms, int Accepted, int WaitingExit,
    double MaximumSampleIntervalMs, IReadOnlyDictionary<string, int> Sources);
