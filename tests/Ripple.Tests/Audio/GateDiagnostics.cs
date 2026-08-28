using Ripple.Audio;
using Ripple.Render;
using Xunit;
using Xunit.Abstractions;

namespace Ripple.Tests.Audio;

/// <summary>
/// Measurement, not assertion. These print the distributions the Phase 2 gate is argued
/// from, so the argument can be re-run rather than remembered.
///
/// Skipped by default is wrong here — they are cheap and their output is the evidence.
/// They assert nothing, so they cannot fail for a reason that is not a crash.
/// </summary>
public sealed class GateDiagnostics(ITestOutputHelper log)
{
    private static Analyzer Make() => new(new AudioFormat(SignalGen.Rate, 2));
    private static double FrameRate => SignalGen.Rate / (double)AudioFormat.Hop;

    [Fact]
    public void Tempo_octave_across_the_signal_space()
    {
        log.WriteLine($"{"signal",-34} {"want",6} {"bpm",8} {"conf",6} {"medBpm",8} {"acfLag",7} {"oct",4} {"cmb10",6} {"cmb50",6} {"cmb90",6}");
        log.WriteLine(new string('-', 76));

        void Row(string name, double want, float[] mono)
        {
            var a = Make();
            var comb = new List<double>();
            var bpms = new List<double>();
            SignalGen.Drive(a, SignalGen.ToStereo(mono), (_, i) =>
            {
                if (i / FrameRate < 4.0) return;   // median + confidence warm-up
                comb.Add(a.CombSmooth);
                bpms.Add(a.Bpm);
            });
            log.WriteLine($"{name,-34} {want,6:F1} {a.Bpm,8:F2} {a.BpmConfidence,6:F2} "
                + $"{SignalGen.Percentile(bpms, 0.50),8:F1} {a.AcfLag,7} {(a.OctaveDoubled ? "2x" : "-"),4} "
                + $"{SignalGen.Percentile(comb, 0.10),6:F2} {SignalGen.Percentile(comb, 0.50),6:F2} "
                + $"{SignalGen.Percentile(comb, 0.90),6:F2}");
        }

        foreach (double bpm in (double[])[60, 90, 120, 150, 180])
            Row($"click {bpm:F0}", bpm, SignalGen.ClickTrack(20.0, bpm));

        // MusicLike puts hats on the HALF BEAT, so the onset envelope's strongest
        // periodicity is at twice the beat rate. Below 90 BPM that subdivision is inside
        // the [60,180] search range, which is the case the octave logic has to get right.
        foreach (double bpm in (double[])[64, 72, 80, 88, 108])
            Row($"musicLike {bpm:F0} (sub {bpm * 2:F0})", bpm, SignalGen.MusicLike(24.0, bpm));

        Row("arrhythmic (drums->space)", double.NaN, SignalGen.ArrhythmicClicks(20.0));
        Row("sustained pad", double.NaN, SignalGen.Sine(20.0, 220));
    }

    [Fact]
    public void Flux_distribution_shape()
    {
        log.WriteLine($"{"signal",-26} {"p10",7} {"p50",7} {"p90",7} {"mean",7} "
            + $"{"frac<.02",9} {"frac>.98",9} {"corr(onset)",11}");
        log.WriteLine(new string('-', 92));

        void Row(string name, float[] mono)
        {
            var a = Make();
            var flux = new List<double>();
            var onset = new List<double>();
            SignalGen.Drive(a, SignalGen.ToStereo(mono), (fr, i) =>
            {
                if (i / FrameRate < 2.0) return;    // normalizer warm-up
                flux.Add(fr.AsSpan()[FF.Flux]);
                onset.Add(fr.AsSpan()[FF.Onset]);
            });

            double lo = flux.Count(v => v < 0.02) / (double)flux.Count;
            log.WriteLine($"{"  onset:",-26} {SignalGen.Percentile(onset, 0.10),7:F3} "
                + $"{SignalGen.Percentile(onset, 0.50),7:F3} {SignalGen.Percentile(onset, 0.90),7:F3} "
                + $"{onset.Average(),7:F3}");
            double hi = flux.Count(v => v > 0.98) / (double)flux.Count;
            log.WriteLine($"{name,-26} {SignalGen.Percentile(flux, 0.10),7:F3} "
                + $"{SignalGen.Percentile(flux, 0.50),7:F3} {SignalGen.Percentile(flux, 0.90),7:F3} "
                + $"{flux.Average(),7:F3} {lo,9:F3} {hi,9:F3} {Corr(flux, onset),11:F3}");
        }

        Row("musicLike 108", SignalGen.MusicLike(24.0));
        Row("musicLike 80", SignalGen.MusicLike(24.0, 80));
        Row("click 120", SignalGen.ClickTrack(20.0, 120));
        Row("arrhythmic", SignalGen.ArrhythmicClicks(20.0));
        Row("sustained pad", SignalGen.Sine(20.0, 220));
        Row("chord changes", SignalGen.Concat(
            SignalGen.Chord(5, [261.63, 329.63, 392.00]),
            SignalGen.Chord(5, [392.00, 493.88, 587.33]),
            SignalGen.Chord(5, [349.23, 440.00, 523.25])));
    }

    /// <summary>Pearson correlation. F_FLUX duplicating F_ONSET shows up here as ~1.</summary>
    private static double Corr(List<double> a, List<double> b)
    {
        int n = Math.Min(a.Count, b.Count);
        if (n < 2) return double.NaN;
        double ma = a.Take(n).Average(), mb = b.Take(n).Average();
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sab += da * db; saa += da * da; sbb += db * db;
        }
        return sab / Math.Sqrt(Math.Max(saa * sbb, 1e-18));
    }
}
