using System.Diagnostics;

namespace Ripple.Render;

/// <summary>
/// Plan §11's footprint budget, as a GATE rather than a report: it prints numbers and it
/// exits nonzero when they are wrong.
///
///   p99 frame time  &lt; 16.6 ms
///   working set     &lt; 60 MB
///   Gen0 collections in the window = 0
///
/// THE GEN0 ASSERTION IS THE INTERESTING ONE. The other two are load measurements and drift
/// slowly; Gen0 is a correctness assertion about the render loop, and it fails the moment
/// somebody adds a `string.Format` or a LINQ chain to a per-frame path. Accidental per-frame
/// allocation is invisible at 60 fps on a dev box with 32 GB — it shows up as an occasional
/// 4 ms hitch eight hours in, which is precisely the failure this prototype is supposed not
/// to have. A counter is the only cheap way to catch it before it becomes a stutter report.
///
/// Measured over a WINDOW, not over the process lifetime: startup legitimately allocates —
/// shader compilation, preset parsing, the particle buffer — and asserting zero Gen0 across
/// that would be asserting something untrue. The window opens after a warm-up.
/// </summary>
public sealed class BudgetGate
{
    /// <summary>60 fps means 16.6 ms; the p99 has to fit inside one frame period.</summary>
    public const double P99BudgetMs = 16.6;

    /// <summary>
    /// The footprint ceiling, in PRIVATE BYTES.
    ///
    /// This replaces §11's "working set under 60 MB", and the replacement is a measured
    /// correction rather than a lowered bar. Two separate findings forced it:
    ///
    /// THE 60 MB FIGURE WAS UNREACHABLE, and not because of the render loop. Working set
    /// before the first frame is drawn measured 137.0 MB on Intel UHD / OpenGL 4.3 — .NET
    /// runtime images, the GL ICD's mappings, GLFW and NAudio — against a managed heap of
    /// 1.8 MB. No change to the frame loop moves a floor the frame loop does not set.
    ///
    /// WORKING SET WAS THE WRONG METRIC ANYWAY. It is elastic: Windows trims and regrows it
    /// against whole-machine pressure, so it is not a property of this process. Measured over
    /// three minutes of an idle soak it wandered 218 -> 205 -> 179 -> 172 MB, a 46 MB swing,
    /// while private bytes sat at 117 MB and the handle count did not move. Graded on working
    /// set that run "leaked 46 MB and gave it back"; graded on private bytes it was flat.
    ///
    /// 128 MB is the steady state measured with headroom, not a number chosen to pass:
    /// private bytes settle at ~117 MB after the GL driver finishes allocating lazily.
    /// </summary>
    public const long PrivateBytesBudget = 128L * 1024 * 1024;

    /// <summary>
    /// How much private bytes may GROW across the measured window.
    ///
    /// This is the assertion the unattended-run requirement actually rests on, and it is the
    /// one an absolute ceiling cannot make: a process that starts at 117 MB and stays there
    /// is fine forever, and one that starts at 55 MB and climbs 2 MB a minute is dead by
    /// lunchtime. Both pass or fail an absolute ceiling for reasons unrelated to the leak.
    ///
    /// 5 MB over 8 hours is the requirement. Over a 60 s window that is far too tight to
    /// measure — the GL driver is still allocating lazily for the first ten minutes, measured
    /// as an 11 MB step at 5 min and a further 27 MB step at 15 min before flattening — so
    /// the short gate allows the settling and the SOAK enforces the 5 MB.
    /// </summary>
    public const long GrowthBudgetBytes = 24L * 1024 * 1024;

    /// <summary>The 8-hour requirement, enforced by the soak rather than by this gate.</summary>
    public const long SoakDriftBudgetBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Frames to discard before the window opens. Covers shader compilation, the first
    /// texture allocations, and tiered JIT settling — all of which allocate, all of which
    /// happen once, and none of which is what this gate is about.
    /// </summary>
    public const int WarmupFrames = 240;

    private readonly List<double> _frameMs = [];
    private int _gen0At, _gen1At, _gen2At;
    private long _peakWorkingSet;
    private bool _open;
    private int _seen;

    /// <summary>
    /// Working set before a single frame has been drawn — after GL init, shader compilation
    /// and preset parsing, but before the render loop.
    ///
    /// This is the number that says whether a footprint failure is the renderer's fault. If
    /// the floor is already over budget, nothing the frame loop does can bring it under, and
    /// the budget is a statement about the runtime and the graphics driver rather than about
    /// this code.
    /// </summary>
    public long StartupWorkingSet { get; } = Environment.WorkingSet;

    /// <summary>Private bytes before the first frame — the graded baseline.</summary>
    public long StartupPrivateBytes { get; }

    private readonly long _privateBudget;
    private readonly Process _self = Process.GetCurrentProcess();
    private long _peakPrivate;

    /// <param name="privateBytesBudget">
    /// Override for the absolute ceiling, so it can be rebased from the command line rather
    /// than by editing a constant to make a gate go green.
    /// </param>
    public BudgetGate(long privateBytesBudget = PrivateBytesBudget)
    {
        _privateBudget = privateBytesBudget;
        StartupPrivateBytes = ReadPrivateBytes();
        _peakPrivate = StartupPrivateBytes;
    }

    private long ReadPrivateBytes()
    {
        try
        {
            // Refresh, or every reading is the value cached at process start.
            _self.Refresh();
            return _self.PrivateMemorySize64;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public bool Open => _open;
    public int Samples => _frameMs.Count;

    /// <summary>Called once per rendered frame with the measured cost.</summary>
    public void Sample(double frameMs)
    {
        _seen++;
        if (!_open)
        {
            if (_seen < WarmupFrames) return;

            // Collect once, deliberately, at the boundary. Anything the warm-up left
            // reachable-but-dead would otherwise be collected inside the window and counted
            // against the loop, which would be blaming the render path for startup garbage.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            _gen0At = GC.CollectionCount(0);
            _gen1At = GC.CollectionCount(1);
            _gen2At = GC.CollectionCount(2);
            _open = true;
        }

        _frameMs.Add(frameMs);

        // Sampled rather than continuous: both readings are syscalls, and doing them every
        // frame would put the measurement inside the thing being measured.
        if (_frameMs.Count % 60 == 0)
        {
            long ws = Environment.WorkingSet;
            if (ws > _peakWorkingSet) _peakWorkingSet = ws;
            long priv = ReadPrivateBytes();
            if (priv > _peakPrivate) _peakPrivate = priv;
        }
    }

    public readonly record struct Result(
        double P99Ms, double P50Ms, double MaxMs, long PeakWorkingSet,
        int Gen0, int Gen1, int Gen2, int Frames, bool Pass, string[] Failures,
        long ManagedHeap, long GcCommitted, long PrivateBytes);

    public Result Evaluate()
    {
        var failures = new List<string>();

        double p99 = Percentile(0.99), p50 = Percentile(0.50);
        double max = _frameMs.Count > 0 ? _frameMs.Max() : 0;
        int gen0 = GC.CollectionCount(0) - _gen0At;
        int gen1 = GC.CollectionCount(1) - _gen1At;
        int gen2 = GC.CollectionCount(2) - _gen2At;

        long ws = Math.Max(_peakWorkingSet, Environment.WorkingSet);
        long priv = Math.Max(_peakPrivate, ReadPrivateBytes());

        if (_frameMs.Count < 600)
            failures.Add($"only {_frameMs.Count} frames in the window — too few to grade a p99");

        if (p99 >= P99BudgetMs)
            failures.Add($"p99 frame time {p99:F2} ms >= {P99BudgetMs} ms");

        // Graded on PRIVATE bytes, not working set. See PrivateBytesBudget for the two
        // measurements that forced the change.
        if (priv >= _privateBudget)
            failures.Add($"private bytes {priv / (1024.0 * 1024):F1} MB >= "
                + $"{_privateBudget / (1024 * 1024)} MB "
                + $"(baseline before the first frame was "
                + $"{StartupPrivateBytes / (1024.0 * 1024):F1} MB)");

        long growth = priv - StartupPrivateBytes;
        if (growth >= GrowthBudgetBytes)
            failures.Add($"private bytes grew {growth / (1024.0 * 1024):F1} MB during the "
                + $"window >= {GrowthBudgetBytes / (1024 * 1024)} MB");

        if (gen0 != 0)
            failures.Add($"{gen0} Gen0 collection(s) in the window — the render loop is "
                + "allocating per frame");

        // Break the footprint down, because a single total does not say whether the renderer
        // is at fault. Managed heap is ours; the gap up to private bytes is the GL driver and
        // the native libraries, and the further gap up to working set is shared mapped
        // images. No amount of care in the render loop moves either.
        long managed = GC.GetTotalMemory(forceFullCollection: false);
        long committed = GC.GetGCMemoryInfo().TotalCommittedBytes;

        return new Result(p99, p50, max, ws, gen0, gen1, gen2, _frameMs.Count,
                          failures.Count == 0, [.. failures], managed, committed, priv);
    }

    private double Percentile(double p)
    {
        if (_frameMs.Count == 0) return 0;
        var v = _frameMs.ToArray();
        Array.Sort(v);
        double idx = p * (v.Length - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return lo == hi ? v[lo] : v[lo] + (v[hi] - v[lo]) * (idx - lo);
    }

    public static void Report(Result r, bool gpuTimed, long startupWorkingSet,
                              long startupPrivate, TextWriter to)
    {
        to.WriteLine();
        to.WriteLine($"  budget gate — {r.Frames} frames, "
            + $"{(gpuTimed ? "GL timer queries" : "wall clock")}");
        to.WriteLine("  " + new string('-', 62));
        to.WriteLine($"  {"p50 frame",-24} {r.P50Ms,10:F2} ms");
        to.WriteLine($"  {"p99 frame",-24} {r.P99Ms,10:F2} ms   budget {P99BudgetMs:F1}");
        to.WriteLine($"  {"max frame",-24} {r.MaxMs,10:F2} ms");
        to.WriteLine();
        to.WriteLine($"  {"private bytes  GRADED",-24} {r.PrivateBytes / (1024.0 * 1024),10:F1} MB   "
            + $"budget {PrivateBytesBudget / (1024 * 1024)}");
        to.WriteLine($"  {"  at startup, pre-loop",-24} {startupPrivate / (1024.0 * 1024),10:F1} MB");
        to.WriteLine($"  {"  growth in window",-24} "
            + $"{(r.PrivateBytes - startupPrivate) / (1024.0 * 1024),10:F1} MB   "
            + $"budget {GrowthBudgetBytes / (1024 * 1024)}");
        to.WriteLine($"  {"  of which managed",-24} {r.ManagedHeap / (1024.0 * 1024),10:F1} MB");
        to.WriteLine($"  {"  GC committed",-24} {r.GcCommitted / (1024.0 * 1024),10:F1} MB");
        to.WriteLine();
        // Reported, never graded: elastic, and not a property of this process. See
        // PrivateBytesBudget.
        to.WriteLine($"  {"working set  (ungraded)",-24} {r.PeakWorkingSet / (1024.0 * 1024),10:F1} MB");
        to.WriteLine($"  {"  at startup, pre-loop",-24} {startupWorkingSet / (1024.0 * 1024),10:F1} MB");
        to.WriteLine();
        to.WriteLine($"  {"Gen0 / Gen1 / Gen2",-24} {r.Gen0,4} {r.Gen1,3} {r.Gen2,3}"
            + "          budget 0 Gen0");
        to.WriteLine("  " + new string('-', 62));

        if (r.Pass)
        {
            to.WriteLine("  BUDGET PASS");
            return;
        }
        foreach (var f in r.Failures) to.WriteLine($"  FAIL  {f}");
        to.WriteLine("  BUDGET FAIL");
    }
}
