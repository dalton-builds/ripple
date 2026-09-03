namespace Ripple.Audio;

/// <summary>
/// §7.1's level normalization, as a PERCENTILE within the field's own recent history.
///
/// This replaces the window-plus-exponent curve, and it replaces it because that approach was
/// measured to be unfixable rather than merely untuned. The old chain was
/// `(env - lo) / max(hi - lo, 6 dB)` followed by `v^gamma`, and gamma had to be fitted per
/// band against real material. It could not be:
///
///   The air band's median on a 1972 Veneta board tape is EXACTLY 0.0000, against 0.120 on a
///   1985 release of the same song. A desk feed from 1972 has no content above 10 kHz at all.
///   Fitting an exponent that maps zero onto the synthetic driver's 0.212 gives 0.11 on one
///   capture and 3.41 on the other — a 30x disagreement — and no number of further captures
///   improves it, because you cannot calibrate a band that has nothing in it.
///
/// Percentile mapping has no such failure mode. It asks "where does this value sit in what
/// this band has been doing lately", which is defined for any distribution and needs no target
/// median to aim at. A band with no content has no spread, and no spread is a state this class
/// recognises and reports as zero rather than stretching noise across the range.
///
/// WHAT HAPPENS IN SILENCE, which is the case §7.1's 6 dB floor existed for. That floor was
/// there because dividing by a tiny span amplifies the noise floor into visible motion — a
/// band carrying nothing but FFT leakage would swing over its full output range. Percentile
/// mapping has exactly the same hazard in a different dress: the percentile of a value within
/// a distribution 0.4 dB wide is still 0..1, so noise would still become motion. So the floor
/// is carried forward verbatim in spirit: the p5..p95 spread of the history is measured, and
/// the output is scaled by `spread / 6 dB`, clamped to 1. A band with 6 dB or more of genuine
/// range is unaffected; a band that is flat reads zero, stably, forever. That is the air-band
/// case and it is now handled by construction instead of by a fitted constant.
///
/// COST AND MEMORY ARE BOUNDED, per §11's budget — the DSP had 0.3 percentage points of
/// headroom against 2% of a core. The history is a decaying histogram of 1 dB bins: one
/// multiply-add per bin per hop and a prefix sum to locate the current value, which is 140
/// operations rather than anything that grows. No buffer, no sort, no reallocation.
/// </summary>
public sealed class PercentileNorm
{
    /// <summary>
    /// 1 dB bins spanning -140 to +80 dB.
    ///
    /// THE UPPER BOUND HAS TO BE POSITIVE, and getting that wrong is a silent failure worth
    /// warning about. The band energies are `10*log10(mean power)` over an UNNORMALIZED FFT, so
    /// they are not bounded at 0 dB: measured on music-like material the band envelopes run
    /// **-9.2 to +41.9 dB**, with `bass` alone spanning 28.0 to 41.9. A first cut of this class
    /// used -140..0 and every loud band clamped into the top bin, which reads as zero spread,
    /// which the guard below correctly reports as a static band — so the whole spectrum went
    /// silent on loud material and came back at -34 dB. It looked like a level-dependence bug
    /// and was a range bug.
    ///
    /// The floor comes from the `1e-12` epsilon in the energy sum: true silence is -120 dB, so
    /// -140 leaves room and the extra bins cost nothing.
    /// </summary>
    private const int Bins = 220;
    private const double MinDb = -140.0;

    /// <summary>
    /// §7.1's 6 dB floor, carried forward. Below this much p5..p95 spread the field is treated
    /// as static and faded out rather than having its noise expanded. See the class note.
    /// </summary>
    public const double SpreadFloorDb = 6.0;

    /// <summary>
    /// Enough history to have a distribution at all. Two seconds at the hop rate — below this
    /// the percentile of the first few frames is meaningless and the field reads zero, which
    /// is the same cold-start behaviour the old envelope had from starting at -120.
    /// </summary>
    private const double MinWeight = 180.0;

    private readonly float[] _hist = new float[Bins];
    private readonly double _tau;
    private double _weight;

    /// <summary>Last p5..p95 spread in dB, for the diagnostics.</summary>
    public double SpreadDb { get; private set; }

    /// <param name="windowSeconds">
    /// How long the history effectively remembers — the one real tuning parameter now.
    ///
    /// Too short and a quiet passage renormalizes into looking loud, because the passage fills
    /// the whole history and its own median becomes the middle of the range. Too long and a
    /// dynamic arc flattens, because a crescendo is judged against material minutes away.
    ///
    /// 45 s is chosen against the shape of the material rather than by taste: verse-to-chorus
    /// dynamics run 20-30 s, so the window has to be longer than that for a quiet verse to read
    /// as quiet, and a Dark Star arc runs several minutes, so it has to be much shorter than
    /// that for the arc to still read as movement. 45 s also means a track change is absorbed
    /// in about a minute, which is the slowest adaptation worth accepting.
    /// </param>
    public PercentileNorm(double windowSeconds = 45.0) => _tau = windowSeconds;

    public void Reset()
    {
        Array.Clear(_hist);
        _weight = 0;
        SpreadDb = 0;
    }

    /// <summary>
    /// Add a dB observation and return where it sits in the history, 0..1.
    ///
    /// The value is inserted before it is located, so the current frame is part of its own
    /// distribution. That matters at the extremes: a new all-time loudest frame reads just
    /// under 1 rather than exactly 1, so the field approaches its ceiling instead of pinning
    /// to it and staying there.
    /// </summary>
    public double Step(double db, double dt)
    {
        // Exponential forgetting IS the rolling window. A true sliding window needs the
        // samples kept; decaying the counts needs only the counts.
        double keep = Math.Exp(-dt / _tau);
        for (int i = 0; i < Bins; i++) _hist[i] *= (float)keep;
        _weight = _weight * keep + 1.0;

        int bin = BinOf(db);
        _hist[bin] += 1f;

        if (_weight < MinWeight) { SpreadDb = 0; return 0; }

        // p5..p95 rather than min..max: one anomalous frame — a click, a dropout — must not
        // define the range the whole band is measured against.
        double lo = Quantile(0.05), hi = Quantile(0.95);
        SpreadDb = hi - lo;

        // §7.1's floor. At 6 dB and above the field is untouched; below, it fades to nothing
        // rather than expanding its own noise floor.
        double gain = Math.Clamp(SpreadDb / SpreadFloorDb, 0, 1);
        if (gain <= 0) return 0;

        // Mass strictly below the value's bin, plus half of its own bin. The half is what makes
        // a perfectly constant signal read 0.5 rather than 1.0 — though the spread gain will
        // have taken it to zero anyway, this keeps the arithmetic honest in between.
        double below = 0;
        for (int i = 0; i < bin; i++) below += _hist[i];
        below += _hist[bin] * 0.5;

        return Math.Clamp(below / _weight, 0, 1) * gain;
    }

    private static int BinOf(double db)
        => (int)Math.Clamp(Math.Floor(db - MinDb), 0, Bins - 1);

    /// <summary>The dB value below which <paramref name="p"/> of the weight lies.</summary>
    private double Quantile(double p)
    {
        double target = p * _weight;
        double acc = 0;
        for (int i = 0; i < Bins; i++)
        {
            acc += _hist[i];
            if (acc >= target) return MinDb + i;
        }
        return MinDb + Bins - 1;
    }
}
