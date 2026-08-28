namespace Ripple.Render;

/// <summary>
/// Adaptive quality (plan §11). Phase 1 shipped the dial table and the clamp; Phase 4 adds
/// the closed control loop, GPU timing and the hysteresis that makes it usable.
///
/// The dials are pulled in the plan's order, because that is the order of
/// benefit-per-unit-of-ugliness: render scale first (largest and cheapest win), then
/// particle count, then fractal iterations, then bloom taps, and only then whole fractal
/// families. Tier 0 is the IDENTITY — a parity run pins tier 0 and is unaffected by
/// anything in this file, which is what lets the gate keep measuring shader math while the
/// governor exists.
/// </summary>
public sealed class Governor
{
    /// <summary>
    /// One rung of §11's ladder. Scale is absolute; the rest are deltas or multipliers
    /// against whatever the preset asked for, because a preset that wants 8k particles
    /// should not be pushed UP to 24k by a governor tier.
    /// </summary>
    public readonly record struct Tier(
        double Scale, double Particles, int Octaves, int BloomTaps, double IterScale,
        bool CheapFamiliesOnly, string Label);

    /// <summary>
    /// §11's ladder, as data:
    ///   renderScale 1.0 → 0.75 → 0.6 → 0.5
    ///   particles   64k → 24k → 8k → 4k        (0.375 / 0.125 / 0.0625 of 64k)
    ///   iterations  128 → 96 → 64 → 32         (0.75 / 0.5 / 0.25)
    ///   bloom taps  3 → 2 → 1 → off
    ///   then disable the expensive fractal families
    ///
    /// Six rungs rather than four: the plan's four are the headline dials, and the last two
    /// are the "this machine cannot do it at all" rungs that keep the thing running rather
    /// than dropping frames. Tier 5 is the panic floor and is deliberately ugly.
    /// </summary>
    public static readonly Tier[] Tiers =
    [
        new(1.00, 1.0000,  0,  0, 1.00, false, "full"),
        new(0.75, 0.3750, -1, -1, 0.75, false, "scale 0.75"),
        new(0.60, 0.1250, -1, -2, 0.50, false, "scale 0.60"),
        new(0.50, 0.0625, -2, -3, 0.25, true,  "scale 0.50"),
        new(0.40, 0.0000, -3, -3, 0.25, true,  "no particles"),
        new(0.30, 0.0000, -4, -3, 0.20, true,  "floor"),
    ];

    /// <summary>
    /// Fractal families cheap enough to keep at the bottom of the ladder. Everything else
    /// falls back to warp_fbm once <see cref="Tier.CheapFamiliesOnly"/> is set — §11's
    /// "disable expensive fractal families", which is the last dial before the floor.
    /// </summary>
    private static readonly HashSet<string> CheapFamilies =
        new(StringComparer.Ordinal) { "warp_fbm", "julia_orbit" };

    // --- control loop constants, all from §11 ------------------------------

    /// <summary>Consecutive frames over budget before stepping down.</summary>
    private const int StepDownFrames = 30;

    /// <summary>Consecutive frames under the step-up band before stepping up.</summary>
    private const int StepUpFrames = 300;

    /// <summary>Step up only while comfortably inside budget, or it immediately steps back down.</summary>
    private const double StepUpFraction = 0.55;

    /// <summary>Minimum time at a tier before it may change again. §11: prevents oscillation.</summary>
    private const double DwellSeconds = 5.0;

    /// <summary>§11's budget: 70% of the frame period, leaving headroom for the compositor.</summary>
    private const double BudgetFraction = 0.7;

    public bool Auto { get; set; }
    public int Level { get; private set; }
    public bool Panic { get; private set; }

    public Tier Current => Tiers[Level];
    public double RenderScale => Tiers[Level].Scale;

    /// <summary>Budget in ms for the measured frame cost. Derived from the target frame rate.</summary>
    public double BudgetMs { get; private set; } = 1000.0 / 60 * BudgetFraction;

    /// <summary>Whether the last decision used GPU timer queries or wall clock.</summary>
    public bool UsingGpuTime { get; private set; }

    /// <summary>Smoothed measured frame cost, for the HUD.</summary>
    public double MeasuredMs { get; private set; }

    public int StepDowns { get; private set; }
    public int StepUps { get; private set; }

    /// <summary>Raised when the tier changes, so the host can resize its targets.</summary>
    public event Action<int, string>? LevelChanged;

    private int _over, _under;
    private double _dwell;

    public void SetTargetFps(double fps)
        => BudgetMs = 1000.0 / Math.Max(1.0, fps) * BudgetFraction;

    /// <summary>Pin to a tier. A parity run pins to 0 and never moves.</summary>
    public void SetLevel(int level)
    {
        int want = Math.Clamp(level, 0, Tiers.Length - 1);
        if (want == Level) { Panic = false; return; }
        Level = want;
        Panic = false;
        _over = _under = 0;
        _dwell = 0;
        LevelChanged?.Invoke(Level, Tiers[Level].Label);
    }

    /// <summary>
    /// One control step, once per rendered frame.
    ///
    /// <paramref name="frameMs"/> is GPU time when timer queries are working and wall clock
    /// otherwise; <paramref name="gpuTimed"/> says which, because the two are not
    /// interchangeable and the HUD should not imply a precision it does not have.
    ///
    /// The two counters are RESET by any frame on the other side of their band, so it takes
    /// 30 CONSECUTIVE bad frames to step down — a single hitch from a shader recompile or a
    /// window resize cannot drop quality — and 300 consecutive good ones to step back up.
    /// The asymmetry is deliberate: dropping quality has to be quick because the symptom is
    /// visible stutter, and restoring it has to be slow because the symptom of getting it
    /// wrong is oscillation.
    /// </summary>
    public void Tick(double dt, double frameMs, bool gpuTimed)
    {
        UsingGpuTime = gpuTimed;
        // 12-frame time constant: long enough to ignore a single hitch, short enough that a
        // genuine load change is visible within a fifth of a second.
        MeasuredMs = MeasuredMs <= 0 ? frameMs : MeasuredMs + (frameMs - MeasuredMs) * 0.08;

        if (!Auto) return;
        _dwell += dt;

        if (frameMs > BudgetMs) { _over++; _under = 0; }
        else if (frameMs < BudgetMs * StepUpFraction) { _under++; _over = 0; }
        else { _over = 0; _under = 0; }   // in the dead band: hold, and forget both runs

        if (_dwell < DwellSeconds) return;

        if (_over >= StepDownFrames && Level < Tiers.Length - 1)
        {
            StepDowns++;
            SetLevel(Level + 1);
        }
        else if (_under >= StepUpFrames && Level > 0)
        {
            StepUps++;
            SetLevel(Level - 1);
        }
    }

    /// <summary>
    /// CONTRACTS §4: the governor clamp is last and absolute. A preset cannot ask for 64k
    /// particles and win against a machine that cannot afford them.
    /// </summary>
    public void Apply(DrawState d)
    {
        var t = Tiers[Level];
        d.Octaves = Math.Max(2, d.Octaves + t.Octaves);
        d.PCount *= t.Particles;
        d.BloomTaps = Math.Max(0, d.BloomTaps + t.BloomTaps);

        // Guarded rather than unconditional, so tier 0 is EXACTLY the identity. An
        // unconditional Math.Max(16, ...) would raise a preset that authored fewer
        // iterations than that, which is a silent change to an authored look — and it would
        // land in the parity gate as a shader-math failure.
        if (t.IterScale < 1.0)
            d.Iterations = Math.Max(16, d.Iterations * t.IterScale);

        if (t.CheapFamiliesOnly && !CheapFamilies.Contains(d.Fractal))
            d.Fractal = "warp_fbm";

        if (!Panic) return;
        d.PCount = 0;
        d.BloomTaps = 0;
        d.BloomGain *= 0.5;
        d.Octaves = 2;
        d.Fractal = "warp_fbm";
    }
}
