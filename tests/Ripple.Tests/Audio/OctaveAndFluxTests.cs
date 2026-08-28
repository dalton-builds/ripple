using Ripple.Audio;
using Ripple.Render;
using Xunit;
using Xunit.Abstractions;

namespace Ripple.Tests.Audio;

/// <summary>
/// The two Phase 2 gate items closed in Phase 4: F_FLUX as an independent continuous
/// drive (§7.2), and octave disambiguation that survives a steady subdivision (§7.3).
///
/// Regression guards, not the measurement — GateDiagnostics prints the distributions
/// these thresholds were drawn from.
/// </summary>
public sealed class OctaveAndFluxTests(ITestOutputHelper log)
{
    private static Analyzer Make() => new(new AudioFormat(SignalGen.Rate, 2));
    private static double FrameRate => SignalGen.Rate / (double)AudioFormat.Hop;

    private static List<double> Field(float[] mono, int index, double skipSeconds = 2.0)
    {
        var v = new List<double>();
        SignalGen.Drive(Make(), SignalGen.ToStereo(mono), (fr, i) =>
        {
            if (i / FrameRate < skipSeconds) return;
            v.Add(fr.AsSpan()[index]);
        });
        return v;
    }

    /// <summary>
    /// The defect: F_FLUX measured p50 0.000 / p90 1.000 — two spikes and nothing between.
    /// A field with two states cannot drive a continuous parameter, and §7.2 specifies a
    /// continuous one. The same shape appears on the committed real-audio fixture, so it
    /// was never a synthetic-WAV artifact.
    /// </summary>
    [Fact]
    public void Flux_is_continuous_on_dense_material()
    {
        var flux = Field(SignalGen.MusicLike(24.0), FF.Flux);

        double p10 = SignalGen.Percentile(flux, 0.10);
        double p50 = SignalGen.Percentile(flux, 0.50);
        double p90 = SignalGen.Percentile(flux, 0.90);
        double atZero = flux.Count(v => v < 0.02) / (double)flux.Count;
        double atOne = flux.Count(v => v > 0.98) / (double)flux.Count;
        log.WriteLine($"p10 {p10:F3}  p50 {p50:F3}  p90 {p90:F3}  "
            + $"frac<0.02 {atZero:F3}  frac>0.98 {atOne:F3}");

        // The two spikes are what made it unusable, so they are what the test forbids.
        Assert.True(atZero < 0.20, $"{atZero:P1} of frames pinned at zero");
        Assert.True(atOne < 0.10, $"{atOne:P1} of frames pinned at one");

        // And it has to actually move: continuous but static is no more useful than binary.
        Assert.True(p50 is > 0.05 and < 0.80, $"median {p50:F3} is not in the usable band");
        Assert.True(p90 - p10 > 0.20,
            $"interdecile range {p90 - p10:F3} is too narrow to drive anything");
    }

    /// <summary>
    /// F_FLUX used to be a rescaling of the onset comparator's own decision variable, which
    /// is how §7.2 asked for an independent drive and got a second copy of F_ONSET.
    /// </summary>
    [Fact]
    public void Flux_is_independent_of_onset()
    {
        var flux = new List<double>();
        var onset = new List<double>();
        SignalGen.Drive(Make(), SignalGen.ToStereo(SignalGen.MusicLike(24.0)), (fr, i) =>
        {
            if (i / FrameRate < 2.0) return;
            flux.Add(fr.AsSpan()[FF.Flux]);
            onset.Add(fr.AsSpan()[FF.Onset]);
        });

        double ma = flux.Average(), mb = onset.Average();
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < flux.Count; i++)
        {
            double da = flux[i] - ma, db = onset[i] - mb;
            sab += da * db; saa += da * da; sbb += db * db;
        }
        double r = sab / Math.Sqrt(Math.Max(saa * sbb, 1e-18));

        log.WriteLine($"corr(flux, onset) = {r:F3}");
        // They are driven by the same audio events, so some correlation is correct and
        // expected. What is not acceptable is one being a monotone function of the other.
        Assert.True(Math.Abs(r) < 0.45, $"flux still tracks onset at r={r:F3}");
    }

    /// <summary>A steady tone is no spectral change, and must not read as maximum change.</summary>
    [Fact]
    public void Flux_reads_zero_on_a_steady_tone()
    {
        var flux = Field(SignalGen.Sine(12.0, 220), FF.Flux, skipSeconds: 3.0);
        double p90 = SignalGen.Percentile(flux, 0.90);
        log.WriteLine($"steady tone: p90 {p90:F4}, max {flux.Max():F4}");
        Assert.True(p90 < 0.10, $"a steady tone reported flux p90 {p90:F3}");
    }

    /// <summary>
    /// The case that was silently wrong. MusicLike puts kick and bass on the beat and hats
    /// on the HALF beat, so the onset envelope's strongest periodicity is the subdivision.
    /// At 88 BPM the tracker reported 170.18 with confidence 0.88 — high confidence, wrong
    /// octave — because the raw ACF peak sat at lag 33 and the disambiguation loop could
    /// only ever divide.
    ///
    /// 108 BPM is the control: its subdivision at 216 falls outside the [60,180] search
    /// range, so it was always tracked correctly and must stay that way.
    /// </summary>
    [Theory]
    [InlineData(80)]
    [InlineData(88)]
    [InlineData(108)]
    public void Tempo_prefers_the_beat_over_a_steady_subdivision(double bpm)
    {
        var a = Make();
        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.MusicLike(24.0, bpm)));

        log.WriteLine($"want {bpm} -> {a.Bpm:F2} BPM (conf {a.BpmConfidence:F2}, "
            + $"comb {a.CombSmooth:F2}, doubled {a.OctaveDoubled})");

        // Deliberately loose on the value: the doubled lag is quantized to hops, so at
        // 88 BPM the best available lag is 66 = 85.2 BPM. The assertion is about the
        // OCTAVE, which is what was wrong — not about sub-BPM accuracy.
        Assert.True(a.Bpm < bpm * 1.5,
            $"tracked {a.Bpm:F1} against {bpm}: still on the subdivision");
        Assert.True(a.Bpm > bpm * 0.62,
            $"tracked {a.Bpm:F1} against {bpm}: over-corrected past the beat");
    }

    /// <summary>
    /// The other half of the contract, and the reason the comb test is gated on ACF
    /// peakiness. Free time must still fall through to §7.3's onset fallback — a tracker
    /// that finds a tempo in Drums → Space is the failure that section exists to prevent.
    /// An earlier cut of the comb ratio was unbounded and reached 3.8e6 here.
    /// </summary>
    [Fact]
    public void Free_time_still_falls_through_to_the_fallback()
    {
        var a = Make();
        int below = 0, total = 0;
        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.ArrhythmicClicks(20.0)), (fr, i) =>
        {
            if (i / FrameRate < 4.0) return;
            total++;
            if (fr.AsSpan()[FF.BpmConf] < Analyzer.ConfidenceFloor) below++;
        });

        double frac = below / (double)total;
        log.WriteLine($"below the 0.35 floor on {frac:P1} of frames; "
            + $"final conf {a.BpmConfidence:F3}, comb {a.CombSmooth:F2}");
        Assert.True(frac > 0.90, $"arrhythmic material read as pulsed on {1 - frac:P1} of frames");
    }

    /// <summary>
    /// A metronomic source has no strong/weak alternation, so the comb test must leave it
    /// alone. This is the guard that the octave fix was not bought at the price of
    /// everything that already worked.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(150)]
    [InlineData(180)]
    public void Click_tracks_are_untouched_by_the_comb_test(double bpm)
    {
        var a = Make();
        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.ClickTrack(20.0, bpm)));
        log.WriteLine($"{bpm} -> {a.Bpm:F2} (comb {a.CombSmooth:F2}, doubled {a.OctaveDoubled})");
        Assert.False(a.OctaveDoubled, $"comb test doubled a {bpm} BPM click track");
        Assert.True(Math.Abs(a.Bpm - bpm) < 2.0, $"tracked {a.Bpm:F2} against {bpm}");
    }
}
