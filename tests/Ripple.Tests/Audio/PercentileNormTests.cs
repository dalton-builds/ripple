using Ripple.Audio;
using Ripple.Render;
using Xunit;
using Xunit.Abstractions;

namespace Ripple.Tests.Audio;

/// <summary>
/// §7.1's level normalization after the window-plus-exponent chain was retired.
///
/// The tests here are chosen to be the properties the exponent could NOT hold, because that is
/// what the replacement has to be judged on. In order of how much they matter:
///
///   1. a band with no content reads zero, stably — the air-band case, which is what made the
///      exponent unfittable (median 0.0000 on a 1972 board tape against 0.120 in 1985);
///   2. the SAME music with a different spectral balance produces the same mid-band values —
///      the recording-independence the fit could not achieve;
///   3. silence does not become motion, which is what §7.1's 6 dB floor existed for;
///   4. level independence, which the old chain did hold and must not lose.
/// </summary>
public sealed class PercentileNormTests(ITestOutputHelper log)
{
    private static Analyzer Make() => new(new AudioFormat(SignalGen.Rate, 2));
    private static double FrameRate => SignalGen.Rate / (double)AudioFormat.Hop;

    private static readonly (int Index, string Name)[] Bands =
    [
        (FF.Sub, "sub"), (FF.Bass, "bass"), (FF.LowMid, "lowMid"), (FF.Mid, "mid"),
        (FF.HiMid, "hiMid"), (FF.Presence, "presence"), (FF.Brilliance, "brilliance"),
        (FF.Air, "air"),
    ];

    /// <summary>Per-field medians over a signal, skipping the normalizer's warm-up.</summary>
    private static Dictionary<string, double> Medians(float[] mono, double skipSeconds = 6.0)
    {
        var acc = new Dictionary<string, List<double>>();
        SignalGen.Drive(Make(), SignalGen.ToStereo(mono), (fr, i) =>
        {
            if (i / FrameRate < skipSeconds) return;
            var f = fr.AsSpan();
            foreach (var (idx, name) in Bands)
            {
                if (!acc.TryGetValue(name, out var l)) acc[name] = l = [];
                l.Add(f[idx]);
            }
            foreach (var (idx, name) in (( int, string )[])[(FF.Rms, "rms"), (FF.Peak, "peak")])
            {
                if (!acc.TryGetValue(name, out var l)) acc[name] = l = [];
                l.Add(f[idx]);
            }
        });
        return acc.ToDictionary(kv => kv.Key, kv => SignalGen.Percentile(kv.Value, 0.50));
    }

    // ------------------------------------------------- 1. the air-band case

    /// <summary>
    /// THE TEST THIS REPLACEMENT EXISTS FOR.
    ///
    /// Rolling the top octave off the same music must send the air band to ~0 rather than
    /// demanding a different exponent. Under the fitted curve the two versions wanted air
    /// exponents of 0.11 and 3.41 — a 30x disagreement — because an exponent has to hit a
    /// target median and there is nothing there to stretch onto it.
    /// </summary>
    [Fact]
    public void A_band_with_no_content_reads_zero()
    {
        var bright = Medians(SignalGen.MusicLike(30.0));
        var dark = Medians(SignalGen.MusicLikeDarkTape(30.0));

        log.WriteLine($"  {"band",-12} {"bright",8} {"dark tape",10}");
        foreach (var (_, name) in Bands)
            log.WriteLine($"  {name,-12} {bright[name],8:F3} {dark[name],10:F3}");

        // The air band is gone from the dark version, and "gone" must mean zero.
        Assert.True(dark["air"] < 0.05,
            $"air reads {dark["air"]:F3} on material that has no air content");
        Assert.True(dark["brilliance"] < 0.25,
            $"brilliance reads {dark["brilliance"]:F3} with the top octave removed");

        // And it must not have been zero all along — the bright version has to show content,
        // or this passes for the wrong reason.
        Assert.True(bright["air"] > 0.10,
            $"air reads {bright["air"]:F3} on material that does have air content");
    }

    /// <summary>
    /// The same case in isolation and taken to the limit: a band fed nothing but the FFT's own
    /// leakage floor must be flat at zero, not swinging over its range.
    ///
    /// This is what §7.1's 6 dB floor was for, and `PercentileNorm` carries it forward as a
    /// spread guard rather than as a divisor — the percentile of a value within a distribution
    /// 0.4 dB wide is still 0..1, so the hazard survives the change of mechanism and had to be
    /// handled deliberately.
    /// </summary>
    [Fact]
    public void A_pure_tone_does_not_light_the_bands_it_is_not_in()
    {
        var m = Medians(SignalGen.Sine(20.0, 1550), skipSeconds: 4.0);
        log.WriteLine(string.Join("  ", Bands.Select(b => $"{b.Name} {m[b.Name]:F3}")));

        // 1550 Hz is hiMid. Everything else is leakage, measured 105-130 dB down.
        foreach (var (_, name) in Bands)
        {
            if (name == "hiMid") continue;
            Assert.True(m[name] < 0.10, $"{name} reads {m[name]:F3} on a pure 1550 Hz tone");
        }
    }

    [Fact]
    public void Silence_stays_at_zero()
    {
        var m = Medians(SignalGen.Silence(20.0), skipSeconds: 4.0);
        log.WriteLine(string.Join("  ", Bands.Select(b => $"{b.Name} {m[b.Name]:F3}")));
        foreach (var (_, name) in Bands)
            Assert.True(m[name] < 0.02, $"{name} reads {m[name]:F3} in silence");
        Assert.True(m["rms"] < 0.02, $"rms reads {m["rms"]:F3} in silence");
    }

    // -------------------------------------- 2. recording independence

    /// <summary>
    /// The property the fitted exponent could not deliver: two recordings of the same music
    /// with different spectral balance must give the SAME mid-band values.
    ///
    /// The real captures showed the fit agreeing to 1-4% on `lowMid`, `mid` and `hiMid` and
    /// failing at 34-187% on `sub`, `brilliance` and `air`. The bands that agreed are the ones
    /// whose content is a property of the music; the bands that failed are the ones whose
    /// content is a property of the tape. A normalization that reports a percentile within each
    /// band's own history should be indifferent to the difference — the mids see the same music,
    /// so they should report the same thing.
    /// </summary>
    [Fact]
    public void Mid_bands_agree_across_a_change_of_spectral_balance()
    {
        var bright = Medians(SignalGen.MusicLike(30.0));
        var dark = Medians(SignalGen.MusicLikeDarkTape(30.0));

        log.WriteLine($"  {"band",-12} {"bright",8} {"dark",8} {"delta",8}");
        double worst = 0; string worstBand = "";
        foreach (var name in (string[])["bass", "lowMid", "mid", "hiMid"])
        {
            double d = Math.Abs(bright[name] - dark[name]);
            if (d > worst) { worst = d; worstBand = name; }
            log.WriteLine($"  {name,-12} {bright[name],8:F3} {dark[name],8:F3} {d,8:F3}");
        }
        log.WriteLine($"  worst mid-band delta: {worstBand} {worst:F3}");

        // The lowpass does touch hiMid a little — its top edge is 2.4 kHz and the corner is
        // 6 kHz — so this is a tolerance rather than an equality. 0.15 is well inside the
        // 0.34-1.87 the exponent fits disagreed by, expressed in the same units.
        Assert.True(worst < 0.15,
            $"{worstBand} moved {worst:F3} on the same music with a different top end");
    }

    // ------------------------------------------- 3 & 4. what must not regress

    /// <summary>
    /// The old chain held this and the new one must too: the same material 34 dB quieter
    /// normalizes to the same place. This is the whole point of adaptive normalization and the
    /// reason a preset can be authored once.
    /// </summary>
    [Fact]
    public void Normalization_is_still_level_independent()
    {
        var loud = SignalGen.MusicLike(24.0);
        var quiet = new float[loud.Length];
        double g = Math.Pow(10, -34.0 / 20.0);
        for (int i = 0; i < loud.Length; i++) quiet[i] = (float)(loud[i] * g);

        var a = Medians(loud);
        var b = Medians(quiet);

        log.WriteLine($"  {"band",-12} {"loud",8} {"-34 dB",8} {"delta",8}");
        double worst = 0; string worstBand = "";
        foreach (var (_, name) in Bands)
        {
            double d = Math.Abs(a[name] - b[name]);
            if (d > worst) { worst = d; worstBand = name; }
            log.WriteLine($"  {name,-12} {a[name],8:F3} {b[name],8:F3} {d,8:F3}");
        }
        log.WriteLine($"  worst: {worstBand} {worst:F3}");
        Assert.True(worst < 0.12, $"{worstBand} moved {worst:F3} for a pure level change");
    }

    /// <summary>
    /// A field has to use its range, or a preset driven by it does nothing. The exponent
    /// existed to fix exactly this and the percentile has to deliver it without one.
    /// </summary>
    [Fact]
    public void Fields_use_their_range()
    {
        var acc = new Dictionary<string, List<double>>();
        SignalGen.Drive(Make(), SignalGen.ToStereo(SignalGen.MusicLike(30.0)), (fr, i) =>
        {
            if (i / FrameRate < 6.0) return;
            var f = fr.AsSpan();
            foreach (var (idx, name) in Bands)
            {
                if (!acc.TryGetValue(name, out var l)) acc[name] = l = [];
                l.Add(f[idx]);
            }
        });

        log.WriteLine($"  {"band",-12} {"p10",7} {"p50",7} {"p90",7} {"range",7}");
        foreach (var (_, name) in Bands)
        {
            var v = acc[name];
            double p10 = SignalGen.Percentile(v, 0.10);
            double p50 = SignalGen.Percentile(v, 0.50);
            double p90 = SignalGen.Percentile(v, 0.90);
            log.WriteLine($"  {name,-12} {p10,7:F3} {p50,7:F3} {p90,7:F3} {p90 - p10,7:F3}");
        }

        // The bands carrying the music must move. Air is deliberately excluded: MusicLike's
        // top-octave content is a crash every four bars, so a narrow range there is correct.
        foreach (var name in (string[])["bass", "lowMid", "mid", "hiMid", "presence"])
        {
            var v = acc[name];
            double range = SignalGen.Percentile(v, 0.90) - SignalGen.Percentile(v, 0.10);
            Assert.True(range > 0.15, $"{name} only spans {range:F3} from p10 to p90");
        }
    }

    // --------------------------------------------------- the unit itself

    [Fact]
    public void Spread_guard_carries_the_six_dB_floor()
    {
        // A field that never moves: percentile is undefined and the answer must be zero.
        var flat = new PercentileNorm(windowSeconds: 4.0);
        double last = 0;
        for (int i = 0; i < 2000; i++) last = flat.Step(-95.0, 1.0 / 93.75);
        log.WriteLine($"constant -95 dB -> {last:F4}, spread {flat.SpreadDb:F2} dB");
        Assert.Equal(0.0, last, 4);

        // A field with plenty of range: the percentile is used in full.
        var moving = new PercentileNorm(windowSeconds: 4.0);
        double lo = 0, hi = 0;
        for (int i = 0; i < 4000; i++)
        {
            double db = -60 + 40 * (0.5 + 0.5 * Math.Sin(i * 0.02));
            double v = moving.Step(db, 1.0 / 93.75);
            if (i > 2000) { lo = Math.Min(lo == 0 ? v : lo, v); hi = Math.Max(hi, v); }
        }
        log.WriteLine($"40 dB sweep -> {lo:F3}..{hi:F3}, spread {moving.SpreadDb:F1} dB");
        Assert.True(moving.SpreadDb > PercentileNorm.SpreadFloorDb);
        Assert.True(hi - lo > 0.6, $"a 40 dB sweep only spanned {hi - lo:F3}");
    }

    [Fact]
    public void Cold_start_reads_zero_rather_than_a_half()
    {
        // Before there is a distribution there is no percentile, and 0.5 would be a lie that
        // shows up as every field sitting at mid-range for the first two seconds.
        var p = new PercentileNorm();
        double first = p.Step(-20.0, 1.0 / 93.75);
        log.WriteLine($"first sample -> {first:F4}");
        Assert.Equal(0.0, first, 6);
    }

    /// <summary>Diagnostic: what dB range do the band envelopes actually occupy?</summary>
    [Fact]
    public void Band_db_range_on_real_material()
    {
        var a = Make();
        double lo = double.MaxValue, hi = double.MinValue;
        var perBand = new (double Lo, double Hi)[8];
        for (int b = 0; b < 8; b++) perBand[b] = (double.MaxValue, double.MinValue);

        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.MusicLike(20.0)), (_, i) =>
        {
            if (i / FrameRate < 2.0) return;
            for (int b = 0; b < 8; b++)
            {
                double db = a.BandEnvDb(b);
                lo = Math.Min(lo, db); hi = Math.Max(hi, db);
                perBand[b] = (Math.Min(perBand[b].Lo, db), Math.Max(perBand[b].Hi, db));
            }
        });
        log.WriteLine($"overall band env dB: {lo:F1} .. {hi:F1}");
        for (int b = 0; b < 8; b++)
            log.WriteLine($"  band {b}: {perBand[b].Lo,8:F1} .. {perBand[b].Hi,8:F1}");
    }
}
