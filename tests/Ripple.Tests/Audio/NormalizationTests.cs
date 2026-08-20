using Ripple.Audio;
using Ripple.Features;
using Ripple.Render;
using Xunit;
using Xunit.Abstractions;

namespace Ripple.Tests.Audio;

/// <summary>
/// The first of the two things that matter more than the rest.
///
/// All 13 presets were authored against the SYNTHETIC driver's feature ranges. If the
/// real DSP's normalized outputs sit somewhere else — bands hovering at 0.05 where the
/// synthetic driver gives 0.4 — then every preset looks wrong on real audio, and it reads
/// as a visual problem rather than the scaling problem it is. Someone then "fixes" it by
/// retuning 13 presets against the new ranges, and the harness stops predicting what the
/// native host will do.
///
/// So: drive both, compare distributions per field, and fail on a gross mismatch.
///
/// This is deliberately a loose test. It is not asserting that a sine sweep and a
/// synthetic LFO produce the same histogram — they cannot. It asserts that both land in
/// the same usable part of 0..1, which is the property the presets actually depend on.
/// </summary>
public sealed class NormalizationTests(ITestOutputHelper log)
{
    private const double Seconds = 30;

    /// <summary>Steady-state fields, compared at the median.</summary>
    private static readonly (int Index, string Name)[] Sustained =
    [
        (FF.Sub, "sub"), (FF.Bass, "bass"), (FF.LowMid, "lowMid"), (FF.Mid, "mid"),
        (FF.HiMid, "hiMid"), (FF.Presence, "presence"), (FF.Brilliance, "brilliance"),
        (FF.Air, "air"),
        (FF.Rms, "rms"), (FF.Peak, "peak"), (FF.LoudSlow, "loudSlow"),
        (FF.Dynamics, "dynamics"), (FF.Flux, "flux"), (FF.Percussivity, "percussivity"),
        (FF.Centroid, "centroid"),
    ];

    /// <summary>Impulsive fields — compared at p90, because their median is near zero
    /// by design on both sides and would agree for the wrong reason.</summary>
    private static readonly (int Index, string Name)[] Impulsive =
    [
        (FF.Onset, "onset"), (FF.ChromaFlux, "chromaFlux"), (FF.Novelty, "novelty"),
    ];

    private static Dictionary<int, List<double>> RunSynthetic()
    {
        var driver = new SyntheticDriver();
        var frame = default(FeatureFrame);
        var f = frame.AsSpan();
        var acc = new Dictionary<int, List<double>>();
        const double dt = 1.0 / 60;

        for (int i = 0; i < (int)(Seconds * 60); i++)
        {
            driver.Fill(f, dt, i * dt);
            foreach (var (idx, _) in Sustained.Concat(Impulsive))
            {
                if (!acc.TryGetValue(idx, out var list)) acc[idx] = list = [];
                list.Add(f[idx]);
            }
        }
        return acc;
    }

    private static Dictionary<int, List<double>> RunReal(out Analyzer analyzer)
    {
        var a = new Analyzer(new AudioFormat(SignalGen.Rate, 2));
        var acc = new Dictionary<int, List<double>>();
        double frameRate = SignalGen.Rate / (double)AudioFormat.Hop;

        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.MusicLike(Seconds)), (fr, i) =>
        {
            // Skip the first two seconds: the adaptive normalizers are still opening up,
            // and on real audio that warm-up happens once at startup, not per section.
            if (i / frameRate < 2.0) return;
            var f = fr.AsSpan();
            foreach (var (idx, _) in Sustained.Concat(Impulsive))
            {
                if (!acc.TryGetValue(idx, out var list)) acc[idx] = list = [];
                list.Add(f[idx]);
            }
        });
        analyzer = a;
        return acc;
    }

    [Fact]
    public void Real_dsp_ranges_match_the_synthetic_driver()
    {
        var syn = RunSynthetic();
        var real = RunReal(out var analyzer);

        log.WriteLine($"{Seconds}s each; synthetic at 60 fps, real DSP at "
            + $"{SignalGen.Rate / (double)AudioFormat.Hop:F1} fps "
            + $"(bpm {analyzer.Bpm:F1}, conf {analyzer.BpmConfidence:F2})");
        log.WriteLine("");
        log.WriteLine($"  {"field",-13} {"stat",-6} {"synthetic",10} {"real",10} {"delta",8}");
        log.WriteLine("  " + new string('-', 52));

        var failures = new List<string>();
        var deltas = new List<double>();

        void Compare((int Index, string Name)[] fields, double p, string stat, double tol,
                     bool mayReachOne = false)
        {
            foreach (var (idx, name) in fields)
            {
                double s = SignalGen.Percentile(syn[idx], p);
                double r = SignalGen.Percentile(real[idx], p);
                double d = r - s;
                deltas.Add(Math.Abs(d));
                log.WriteLine($"  {name,-13} {stat,-6} {s,10:F3} {r,10:F3} {d,8:+0.000;-0.000}");

                if (Math.Abs(d) > tol)
                    failures.Add($"{name} {stat}: synthetic {s:F3} vs real {r:F3} "
                        + $"(delta {d:+0.000;-0.000}, tolerance {tol:F2})");

                // Both sides must actually use the range. A field pinned at 0 or 1 is a
                // scaling bug even when the two happen to agree — except that an impulsive
                // field reaching exactly 1 at p90 is the design: the onset impulse is SET
                // to 1 on detection.
                if (r < 0.02 || (r > 0.98 && !mayReachOne))
                    failures.Add($"{name} {stat}: real DSP is pinned at {r:F3}");
            }
        }

        // Two thresholds, because they catch different things.
        //
        // The AGGREGATE is the real assertion: a systematic scaling error moves every field
        // the same way, and that is the failure that would make all 13 presets look wrong
        // at once. It found one — §7.1's normalizer is top-heavy by construction on dense
        // material, and every field was reading ~0.4 high. See Analyzer.LevelGamma.
        //
        // The PER-FIELD threshold is deliberately loose, because a per-field difference is
        // not evidence of a DSP bug. It compares a synthetic audio signal of this test's own
        // invention against the synthetic driver's default slider positions, and there is no
        // reason those should agree on, say, how much cymbal is in the mix. Tightening it
        // would only pressure the DSP into matching this file's idea of music.
        Compare(Sustained, 0.5, "median", 0.42);
        Compare(Impulsive, 0.9, "p90", 0.45, mayReachOne: true);

        double meanDelta = deltas.Average();
        log.WriteLine("");
        log.WriteLine($"  mean |delta| across {deltas.Count} fields: {meanDelta:F3}");
        if (meanDelta > 0.25)
            failures.Add($"mean |delta| {meanDelta:F3} exceeds 0.25 — this is a SYSTEMATIC "
                + "scaling difference, not per-field variation");

        log.WriteLine("");
        if (failures.Count > 0)
        {
            foreach (var s in failures) log.WriteLine("  FAIL " + s);
            Assert.Fail($"{failures.Count} field(s) out of range against the synthetic driver:\n  "
                + string.Join("\n  ", failures));
        }
    }

    /// <summary>
    /// A twenty-minute Dark Star moves from near-silence to full band peak, and plan §7.1
    /// calls the adaptive normalization "the single most important detail in this document
    /// for making the visualizer feel alive". So: the same material at two very different
    /// levels must normalize to nearly the same features. If it does not, quiet passages
    /// give a dead screen.
    /// </summary>
    [Fact]
    public void Adaptive_normalization_is_level_independent()
    {
        double frameRate = SignalGen.Rate / (double)AudioFormat.Hop;

        double[] Medians(double gain)
        {
            var a = new Analyzer(new AudioFormat(SignalGen.Rate, 2));
            var sig = SignalGen.MusicLike(20.0);
            for (int i = 0; i < sig.Length; i++) sig[i] = (float)(sig[i] * gain);

            var lists = new List<double>[8];
            for (int i = 0; i < 8; i++) lists[i] = [];
            SignalGen.Drive(a, SignalGen.ToStereo(sig), (fr, i) =>
            {
                if (i / frameRate < 5.0) return;      // let the window settle
                var f = fr.AsSpan();
                for (int b = 0; b < 8; b++) lists[b].Add(f[FF.Sub + b]);
            });
            var med = new double[8];
            for (int b = 0; b < 8; b++) med[b] = SignalGen.Percentile(lists[b], 0.5);
            return med;
        }

        var loud = Medians(1.0);
        var quiet = Medians(0.02);          // -34 dB, a Dark Star trough

        log.WriteLine($"  {"band",-12} {"loud",8} {"quiet(-34dB)",14} {"delta",8}");
        log.WriteLine("  " + new string('-', 46));
        double worst = 0;
        for (int b = 0; b < 8; b++)
        {
            double d = quiet[b] - loud[b];
            log.WriteLine($"  {FF.Names[FF.Sub + b],-12} {loud[b],8:F3} {quiet[b],14:F3} {d,8:+0.000;-0.000}");
            worst = Math.Max(worst, Math.Abs(d));
        }

        Assert.True(worst < 0.15,
            $"a 34 dB level change moved a band median by {worst:F3} — normalization is not adapting");
    }
}
