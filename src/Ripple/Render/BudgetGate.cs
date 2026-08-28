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
    /// §11's footprint budget, as written in the plan.
    ///
    /// MEASURED AND NOT ACHIEVABLE ON THIS STACK, and the breakdown says why rather than
    /// leaving it to be argued about. On Intel UHD / OpenGL 4.3, working set before the
    /// first frame is drawn is 137.0 MB — .NET runtime images, the GL ICD's mappings, GLFW
    /// and NAudio — against a managed heap of 1.8 MB and 15.7 MB of growth across a whole
    /// 60 s run. There is no change to the render loop that brings the total under 60 MB,
    /// because the loop is not what is spending it.
    ///
    /// Left at the plan's number deliberately. Lowering a gate to meet the code is how a
    /// budget stops meaning anything, and rebasing it is the plan owner's call, not this
    /// file's — pass --budget-working-set to do that from the command line. What the gate
    /// grows instead is <see cref="GrowthBudgetBytes"/>, which is the assertion the 8-hour
    /// requirement actually rests on.
    /// </summary>
    public const long WorkingSetBudgetBytes = 60L * 1024 * 1024;

    /// <summary>
    /// How much the working set may GROW across the measured window.
    ///
    /// This is the check that protects an unattended 8-hour run, and it is the one an
    /// absolute ceiling cannot make: a process that starts at 137 MB and stays there is fine
    /// forever, and one that starts at 55 MB and climbs 2 MB a minute is dead by lunchtime.
    /// Both pass or fail an absolute 60 MB test for reasons unrelated to the leak.
    /// </summary>
    public const long GrowthBudgetBytes = 24L * 1024 * 1024;

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

    private readonly long _workingSetBudget;

    /// <param name="workingSetBudgetBytes">
    /// Override for the absolute ceiling, so the plan owner can rebase a budget the platform
    /// cannot meet without anybody editing a constant to make a gate go green.
    /// </param>
    public BudgetGate(long workingSetBudgetBytes = WorkingSetBudgetBytes)
        => _workingSetBudget = workingSetBudgetBytes;

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

        // Sampled rather than continuous: Process.WorkingSet64 is a syscall, and doing it
        // every frame would put the measurement inside the thing being measured.
        if (_frameMs.Count % 60 == 0)
        {
            long ws = Environment.WorkingSet;
            if (ws > _peakWorkingSet) _peakWorkingSet = ws;
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

        if (_frameMs.Count < 600)
            failures.Add($"only {_frameMs.Count} frames in the window — too few to grade a p99");

        if (p99 >= P99BudgetMs)
            failures.Add($"p99 frame time {p99:F2} ms >= {P99BudgetMs} ms");

        if (ws >= _workingSetBudget)
            failures.Add($"working set {ws / (1024.0 * 1024):F1} MB >= "
                + $"{_workingSetBudget / (1024 * 1024)} MB "
                + $"(floor before the first frame was {StartupWorkingSet / (1024.0 * 1024):F1} MB)");

        long growth = ws - StartupWorkingSet;
        if (growth >= GrowthBudgetBytes)
            failures.Add($"working set grew {growth / (1024.0 * 1024):F1} MB during the window "
                + $">= {GrowthBudgetBytes / (1024 * 1024)} MB");

        if (gen0 != 0)
            failures.Add($"{gen0} Gen0 collection(s) in the window — the render loop is "
                + "allocating per frame");

        // Break the footprint down, because "148 MB" on its own does not say whether the
        // renderer is at fault. Managed heap is ours; the gap between that and the working
        // set is the runtime, the GL driver's mappings and the loaded images, and no amount
        // of care in the render loop moves it.
        long managed = GC.GetTotalMemory(forceFullCollection: false);
        long committed = GC.GetGCMemoryInfo().TotalCommittedBytes;
        long priv = 0;
        try { priv = Process.GetCurrentProcess().PrivateMemorySize64; }
        catch (Exception) { /* not worth failing a report over */ }

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

    public static void Report(Result r, bool gpuTimed, long startupWorkingSet, TextWriter to)
    {
        to.WriteLine();
        to.WriteLine($"  budget gate — {r.Frames} frames, "
            + $"{(gpuTimed ? "GL timer queries" : "wall clock")}");
        to.WriteLine("  " + new string('-', 58));
        to.WriteLine($"  {"p50 frame",-22} {r.P50Ms,10:F2} ms");
        to.WriteLine($"  {"p99 frame",-22} {r.P99Ms,10:F2} ms   budget {P99BudgetMs:F1}");
        to.WriteLine($"  {"max frame",-22} {r.MaxMs,10:F2} ms");
        to.WriteLine($"  {"peak working set",-22} {r.PeakWorkingSet / (1024.0 * 1024),10:F1} MB   "
            + $"budget {WorkingSetBudgetBytes / (1024 * 1024)}");
        to.WriteLine($"  {"  of which managed",-22} {r.ManagedHeap / (1024.0 * 1024),10:F1} MB");
        to.WriteLine($"  {"  GC committed",-22} {r.GcCommitted / (1024.0 * 1024),10:F1} MB");
        to.WriteLine($"  {"  private bytes",-22} {r.PrivateBytes / (1024.0 * 1024),10:F1} MB");
        to.WriteLine($"  {"  at startup, pre-loop",-22} {startupWorkingSet / (1024.0 * 1024),10:F1} MB");
        to.WriteLine($"  {"  growth in window",-22} "
            + $"{(r.PeakWorkingSet - startupWorkingSet) / (1024.0 * 1024),10:F1} MB   "
            + $"budget {GrowthBudgetBytes / (1024 * 1024)}");
        to.WriteLine($"  {"Gen0 / Gen1 / Gen2",-22} {r.Gen0,4} {r.Gen1,3} {r.Gen2,3}"
            + "        budget 0 Gen0");
        to.WriteLine("  " + new string('-', 58));

        if (r.Pass)
        {
            to.WriteLine("  BUDGET PASS");
            return;
        }
        foreach (var f in r.Failures) to.WriteLine($"  FAIL  {f}");
        to.WriteLine("  BUDGET FAIL");
    }
}
