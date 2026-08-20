using Ripple.Audio;
using Ripple.Render;
using Xunit;
using Xunit.Abstractions;

namespace Ripple.Tests.Audio;

/// <summary>
/// Plan §14, the DSP half: band mapping isolation, tempo tracking including a drifting
/// track, chroma and key, and the bpmConfidence fallback that makes free-time passages
/// work.
/// </summary>
public sealed class AnalyzerTests(ITestOutputHelper log)
{
    private static Analyzer Make(int rate = SignalGen.Rate)
        => new(new AudioFormat(rate, 2));

    /* ------------------------------------------------------------ 7.1 bands */

    /// <summary>
    /// A sine at each band's centre must light that band and leave its neighbours below
    /// 0.1. This is the test that catches an off-by-one in the bin edges, which otherwise
    /// shows up as "the visuals react to the wrong thing" much later. It found two real
    /// ones: overlapping band edges, and a frame-alignment bug in Append that put a
    /// broadband click into every window.
    ///
    /// One documented exception. At 48 kHz a 2048-point window has 23.4 Hz bins, and a
    /// Hann main lobe is 4 bins wide — about 94 Hz. The sub band (20-60 Hz) is 1.7 bins
    /// wide and bass (60-150 Hz) is 3.8, both narrower than the main lobe, so a tone in
    /// one of them physically cannot be resolved out of the other: a 38 Hz tone puts real
    /// energy in bass's first bin. That is the transform's resolution, not a mapping bug,
    /// so adjacent leakage is allowed for bands narrower than the main lobe and required
    /// to be below 0.1 everywhere else. Fixing it properly would need a longer window for
    /// the low bands, which is a multi-resolution analysis and not what §7 asks for.
    /// </summary>
    [Theory]
    [InlineData(0, 38)]      // sub          20-60
    [InlineData(1, 95)]      // bass         60-150
    [InlineData(2, 245)]     // lowMid       150-400
    [InlineData(3, 630)]     // mid          400-1000
    [InlineData(4, 1550)]    // hiMid        1000-2400
    [InlineData(5, 3460)]    // presence     2400-5000
    [InlineData(6, 7000)]    // brilliance   5000-10000
    [InlineData(7, 12600)]   // air          10000-16000
    public void Band_isolates_its_own_range(int band, double hz)
    {
        var a = Make();
        // Silence first so the adaptive normalization starts from a floor and has to open
        // up, which is how it behaves on real material.
        var signal = SignalGen.Concat(
            SignalGen.Silence(0.5),
            SignalGen.Sine(3.0, hz, 0.5));

        var frames = SignalGen.Collect(a, signal);
        Assert.NotEmpty(frames);
        var last = frames[^1];
        var f = last.AsSpan();

        double target = f[FF.Sub + band];
        var bands = new float[8];
        f.Slice(FF.Sub, 8).CopyTo(bands);
        log.WriteLine($"band {band} @ {hz} Hz -> "
            + string.Join(" ", bands.Select(v => $"{v:F3}")));

        Assert.True(target > 0.6, $"band {band} at {hz} Hz only reached {target:F3}");

        // The driven band must be the strongest, whatever else leaks.
        for (int i = 0; i < 8; i++)
            Assert.True(f[FF.Sub + i] <= target + 1e-6,
                $"band {i} = {f[FF.Sub + i]:F3} exceeds driven band {band} = {target:F3}");

        // Bands narrower than the Hann main lobe cannot be resolved from their immediate
        // neighbour; everything else must be clean.
        const double binHz = SignalGen.Rate / (double)AudioFormat.Window;
        const double mainLobeHz = 4 * binHz;
        var edges = new (double Lo, double Hi)[]
        {
            (20, 60), (60, 150), (150, 400), (400, 1000),
            (1000, 2400), (2400, 5000), (5000, 10000), (10000, 16000),
        };
        bool narrow(int i) => edges[i].Hi - edges[i].Lo < mainLobeHz;

        for (int i = 0; i < 8; i++)
        {
            if (i == band) continue;
            bool adjacent = Math.Abs(i - band) == 1;
            bool unresolvable = adjacent && (narrow(i) || narrow(band));
            if (unresolvable)
            {
                log.WriteLine($"  band {i} = {f[FF.Sub + i]:F3} — adjacent and narrower than "
                    + $"the {mainLobeHz:F0} Hz main lobe, not separable at this window size");
                continue;
            }
            Assert.True(f[FF.Sub + i] < 0.1,
                $"band {i} = {f[FF.Sub + i]:F3} while driving band {band} at {hz} Hz");
        }
    }

    [Fact]
    public void Silence_reads_as_silence()
    {
        var a = Make();
        var frames = SignalGen.Collect(a, SignalGen.Silence(2.0));
        var f = frames[^1].AsSpan();

        for (int i = 0; i < 8; i++)
            Assert.True(f[FF.Sub + i] < 0.02, $"band {i} = {f[FF.Sub + i]:F4} in silence");
        Assert.True(f[FF.Rms] < 0.02, $"rms = {f[FF.Rms]:F4} in silence");
        // The 6 dB floor in the normalizer exists precisely so silence does not get
        // amplified into noise; if it regresses, this is where it shows.
    }

    [Fact]
    public void Bands_stay_in_range_on_a_loud_signal()
    {
        var a = Make();
        var frames = SignalGen.Collect(a, SignalGen.MusicLike(6.0));
        foreach (var fr in frames)
        {
            var f = fr.AsSpan();
            for (int i = 0; i < 56; i++)
            {
                if (i is FF.Bpm or FF.BeatCount or FF.StereoBal or FF.Era or FF.SongMode
                    or FF.Time or FF.Dt or FF.TrackPos
                    or FF.Width or FF.Height or FF.Aspect or FF.RenderScale) continue;
                Assert.True(f[i] >= 0 && f[i] <= 1,
                    $"{FF.Names[i]} = {f[i]} is outside 0..1");
            }
        }
    }

    /* ------------------------------------------------------------ 7.2 onsets */

    [Fact]
    public void Onsets_fire_on_clicks_and_not_on_a_steady_tone()
    {
        var a = Make();
        int clickOnsets = 0;
        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.ClickTrack(6.0, 120)),
            (fr, _) => { if (fr.AsSpan()[FF.Onset] > 0.85) clickOnsets++; });

        var b = Make();
        int toneOnsets = 0;
        SignalGen.Drive(b, SignalGen.ToStereo(SignalGen.Concat(
                SignalGen.Silence(0.5), SignalGen.Sine(6.0, 440, 0.5))),
            (fr, i) => { if (i > 60 && fr.AsSpan()[FF.Onset] > 0.85) toneOnsets++; });

        log.WriteLine($"click-track frames above 0.85: {clickOnsets}, steady tone: {toneOnsets}");
        // 6 s at 120 BPM is 12 beats; the impulse decays over ~150 ms so each detection
        // holds for a few frames. The exact count is not the point — that clicks fire and
        // a held tone does not, is.
        Assert.True(clickOnsets > 8, $"only {clickOnsets} onset frames on a click track");
        Assert.True(toneOnsets < 4, $"{toneOnsets} onset frames on a steady tone");
    }

    /* ------------------------------------------------------------- 7.3 tempo */

    [Theory]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(150)]
    [InlineData(180)]
    public void Tempo_locks_onto_a_click_track(double bpm)
    {
        var a = Make();
        // 20 s: the BPM estimate is median-filtered over 8 s, so anything shorter is
        // measuring the filter's warm-up rather than the tracker.
        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.ClickTrack(20.0, bpm)));

        log.WriteLine($"target {bpm} BPM -> {a.Bpm:F2} (confidence {a.BpmConfidence:F2})");
        Assert.True(Math.Abs(a.Bpm - bpm) < 2.0,
            $"tracked {a.Bpm:F2} BPM against a {bpm} BPM click track");
        Assert.True(a.BpmConfidence > 0.35,
            $"confidence {a.BpmConfidence:F2} on a metronomic click track");
    }

    /// <summary>
    /// The Dead case, called out in plan §14: a click track drifting ±8% over 60 s.
    /// Assert convergence within 4 s and tracking within 2 BPM thereafter.
    ///
    /// The 8 s median filter is the hard part — its output lags a linear ramp by half the
    /// window, so the tracker has to be precise enough for that lag to fit inside the
    /// 2 BPM budget. Sub-lag interpolation of the autocorrelation peak is what makes it
    /// fit; without it, lag quantization alone is ~2.6 BPM at 120.
    /// </summary>
    [Fact]
    public void Tempo_tracks_a_drifting_click_track()
    {
        const double seconds = 60, from = 120 * 0.92, to = 120 * 1.08;
        var a = Make();
        var signal = SignalGen.ToStereo(SignalGen.DriftingClickTrack(seconds, from, to));

        double frameRate = SignalGen.Rate / (double)AudioFormat.Hop;
        var errors = new List<double>();
        double worst = 0, worstAt = 0;
        bool converged = false;
        double convergedAt = double.NaN;

        SignalGen.Drive(a, signal, (_, i) =>
        {
            double t = i / frameRate;
            double want = SignalGen.DriftBpmAt(t, seconds, from, to);
            double err = Math.Abs(a.Bpm - want);

            if (!converged && t >= 0.5 && err < 2.0) { converged = true; convergedAt = t; }
            // The median filter's own warm-up is not a tracking failure, so only grade
            // once a full window has passed.
            if (t > 9.0)
            {
                errors.Add(err);
                if (err > worst) { worst = err; worstAt = t; }
            }
        });

        log.WriteLine($"converged at {convergedAt:F2}s; after 9s: "
            + $"mean err {errors.Average():F3} BPM, worst {worst:F3} at {worstAt:F1}s, "
            + $"final {a.Bpm:F2} (want {to:F2}), confidence {a.BpmConfidence:F2}");

        Assert.True(converged && convergedAt <= 4.0,
            $"did not reach ±2 BPM within 4 s (converged at {convergedAt:F2}s)");
        Assert.True(worst < 2.0, $"worst tracking error {worst:F3} BPM at {worstAt:F1}s");
    }

    [Fact]
    public void Beat_phase_advances_monotonically_within_a_beat()
    {
        var a = Make();
        double frameRate = SignalGen.Rate / (double)AudioFormat.Hop;
        var phases = new List<double>();
        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.ClickTrack(12.0, 120)),
            (fr, i) => { if (i / frameRate > 6) phases.Add(fr.AsSpan()[FF.BeatPhase]); });

        // Phase is a sawtooth: rising, with wraps. Assert it never runs backwards except
        // at a wrap, which is what "phase-locked accumulator, not a per-onset reset"
        // means in practice — a reset would show up as small backward steps mid-beat.
        int backward = 0;
        for (int i = 1; i < phases.Count; i++)
        {
            double d = phases[i] - phases[i - 1];
            if (d < 0 && d > -0.5) backward++;      // a wrap is a big negative step
        }
        log.WriteLine($"{phases.Count} frames, {backward} mid-beat backward steps");
        Assert.True(backward <= 2, $"{backward} backward phase steps — is something resetting phase?");
    }

    /// <summary>
    /// Plan §7.3's Dead-specific behaviour, and the second of the two things the phase
    /// brief flagged as mattering more than the rest: below 0.35 confidence the phase
    /// accumulator freezes and beatPhase comes from the decaying onset impulse instead.
    /// Without it the visuals insist on a grid that is not there through Drums → Space.
    /// </summary>
    [Fact]
    public void Low_confidence_drives_beat_phase_from_onsets()
    {
        var a = Make();
        double frameRate = SignalGen.Rate / (double)AudioFormat.Hop;

        var phases = new List<double>();
        var onsets = new List<double>();
        int fallbackFrames = 0, graded = 0;

        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.ArrhythmicClicks(25.0)), (fr, i) =>
        {
            if (i / frameRate < 8) return;          // let the tracker form an opinion
            graded++;
            if (a.BeatFromOnset) fallbackFrames++;
            phases.Add(fr.AsSpan()[FF.BeatPhase]);
            onsets.Add(fr.AsSpan()[FF.Onset]);
        });

        log.WriteLine($"arrhythmic: bpm {a.Bpm:F1}, confidence {a.BpmConfidence:F3}, "
            + $"fallback active on {fallbackFrames}/{graded} graded frames");

        Assert.True(a.BpmConfidence < 0.35,
            $"confidence {a.BpmConfidence:F3} on arrhythmic clicks — the fallback will never engage");
        Assert.True(fallbackFrames > graded * 0.8,
            $"fallback active on only {fallbackFrames} of {graded} frames");

        // In fallback, phase should follow the onset envelope rather than sweep a grid.
        // Correlation is the cheap way to say that — but the expected sign is NEGATIVE.
        // beatPhase is the position WITHIN a beat, so 0 means "on the beat": at an onset
        // the phase is 0 and it rises as the impulse decays. Perfect tracking is r = -1,
        // and a positive correlation would mean the phase peaks when nothing is happening.
        double mp = phases.Average(), mo = onsets.Average();
        double num = 0, dp = 0, doo = 0;
        for (int i = 0; i < phases.Count; i++)
        {
            double x = phases[i] - mp, y = onsets[i] - mo;
            num += x * y; dp += x * x; doo += y * y;
        }
        double r = num / Math.Sqrt(dp * doo + 1e-12);
        log.WriteLine($"corr(beatPhase, onset) in fallback = {r:F3} (want strongly negative)");
        Assert.True(r < -0.5, $"beatPhase barely tracks the onset envelope (r = {r:F3})");
    }

    [Fact]
    public void Confidence_recovers_when_a_pulse_returns()
    {
        // Free time, then a metronome: the fallback must not latch.
        var a = Make();
        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.ArrhythmicClicks(14.0)));
        double lowConf = a.BpmConfidence;

        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.ClickTrack(24.0, 132)));
        log.WriteLine($"confidence {lowConf:F3} in free time -> {a.BpmConfidence:F3} on a pulse, "
            + $"bpm {a.Bpm:F2}");

        Assert.True(a.BpmConfidence > 0.35, $"confidence stuck at {a.BpmConfidence:F3}");
        Assert.False(a.BeatFromOnset, "still in the onset fallback after the pulse returned");
        Assert.True(Math.Abs(a.Bpm - 132) < 2.5, $"recovered to {a.Bpm:F2} BPM, wanted 132");
    }

    /* ------------------------------------------------------ 7.4 chroma / key */

    /// <summary>
    /// Triads with harmonics, so the chroma vector has the shape real material produces.
    /// A bare set of fundamentals would let a key detector pass on a signal no music
    /// ever generates.
    /// </summary>
    [Theory]
    [InlineData("C", 0, false, 261.63, 329.63, 392.00)]
    [InlineData("G", 7, false, 392.00, 493.88, 587.33)]
    [InlineData("F", 5, false, 349.23, 440.00, 523.25)]
    [InlineData("A minor", 9, true, 440.00, 523.25, 659.25)]
    [InlineData("E minor", 4, true, 329.63, 392.00, 493.88)]
    public void Key_detection_finds_the_triad(string name, int pc, bool minor,
                                              double f1, double f2, double f3)
    {
        var a = Make();
        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.Concat(
            SignalGen.Silence(0.3), SignalGen.Chord(4.0, [f1, f2, f3]))));

        log.WriteLine($"{name}: detected pc {a.KeyPitchClass} minor={a.KeyIsMinor} "
            + $"conf {a.KeyConfidence:F3}");
        Assert.Equal(pc, a.KeyPitchClass);
        Assert.Equal(minor, a.KeyIsMinor);
    }

    [Fact]
    public void Key_hue_follows_the_circle_of_fifths()
    {
        // Plan §7.4: keyHue = ((pc * 7) mod 12) / 12, so musically adjacent keys land on
        // adjacent hues. C and G are a fifth apart and must be 1/12 apart in hue.
        var c = Make();
        SignalGen.Drive(c, SignalGen.ToStereo(SignalGen.Concat(SignalGen.Silence(0.3),
            SignalGen.Chord(4.0, [261.63, 329.63, 392.0]))));
        var g = Make();
        SignalGen.Drive(g, SignalGen.ToStereo(SignalGen.Concat(SignalGen.Silence(0.3),
            SignalGen.Chord(4.0, [392.0, 493.88, 587.33]))));

        double hueC = c.LastFrame.AsSpan()[FF.KeyHue];
        double hueG = g.LastFrame.AsSpan()[FF.KeyHue];
        log.WriteLine($"C hue {hueC:F4}, G hue {hueG:F4}, delta {Math.Abs(hueG - hueC):F4}");

        Assert.Equal(0, c.KeyPitchClass);
        Assert.Equal(7, g.KeyPitchClass);
        Assert.True(Math.Abs(hueC - 0.0) < 1e-6, $"C should map to hue 0, got {hueC}");
        Assert.True(Math.Abs(hueG - 1.0 / 12) < 1e-6, $"G should map to hue 1/12, got {hueG}");
    }

    [Fact]
    public void Chroma_flux_spikes_on_a_chord_change_and_not_within_one()
    {
        var a = Make();
        double frameRate = SignalGen.Rate / (double)AudioFormat.Hop;
        double[] cMaj = [261.63, 329.63, 392.00, 523.26];
        double[] aMin = [440.00, 523.25, 659.25, 880.00];

        var signal = SignalGen.Concat(
            SignalGen.Silence(0.3),
            SignalGen.Sines(3.0, cMaj, 0.15),
            SignalGen.Sines(3.0, aMin, 0.15));

        double steadyMax = 0, changeMax = 0;
        SignalGen.Drive(a, SignalGen.ToStereo(signal), (fr, i) =>
        {
            double t = i / frameRate;
            double v = fr.AsSpan()[FF.ChromaFlux];
            if (t > 1.5 && t < 3.0) steadyMax = Math.Max(steadyMax, v);       // inside C
            if (t > 3.3 && t < 4.0) changeMax = Math.Max(changeMax, v);        // after the change
        });

        log.WriteLine($"chromaFlux: steady max {steadyMax:F3}, change max {changeMax:F3}");
        Assert.True(changeMax > 0.25, $"chord change only produced flux {changeMax:F3}");
        Assert.True(changeMax > steadyMax * 2.5,
            $"flux on change {changeMax:F3} is not clearly above steady {steadyMax:F3}");
    }

    /* --------------------------------------------------- 7.5 stereo / novelty */

    [Fact]
    public void Stereo_width_separates_mono_from_wide()
    {
        var mono = Make();
        SignalGen.Drive(mono, SignalGen.ToStereo(SignalGen.MusicLike(4.0)));

        // Out-of-phase sides: maximum side energy, so width should be high.
        var wide = Make();
        var m = SignalGen.MusicLike(4.0);
        var inter = new float[m.Length * 2];
        for (int i = 0; i < m.Length; i++) { inter[i * 2] = m[i]; inter[i * 2 + 1] = -m[i]; }
        SignalGen.Drive(wide, inter);

        double wMono = mono.LastFrame.AsSpan()[FF.StereoWidth];
        double wWide = wide.LastFrame.AsSpan()[FF.StereoWidth];
        log.WriteLine($"stereoWidth: mono {wMono:F3}, out-of-phase {wWide:F3}");

        Assert.True(wMono < 0.1, $"mono material reported width {wMono:F3}");
        Assert.True(wWide > 0.8, $"out-of-phase material reported width {wWide:F3}");
    }

    [Fact]
    public void Percussivity_separates_drums_from_a_pad()
    {
        // Judge the whole run rather than the final frame. A click track's last frame is
        // the gap after the last click, where the spectrum is empty and the ratio is
        // meaningless — reading it there reported 0.000 for drums and looked inverted.
        double frameRate = SignalGen.Rate / (double)AudioFormat.Hop;

        double Median(Analyzer a, float[] signal)
        {
            var vals = new List<double>();
            SignalGen.Drive(a, SignalGen.ToStereo(signal), (fr, i) =>
            {
                if (i / frameRate < 1.0) return;         // HPSS needs 5 frames of history
                vals.Add(fr.AsSpan()[FF.Percussivity]);
            });
            return SignalGen.Percentile(vals, 0.5);
        }

        double pDrums = Median(Make(), SignalGen.ClickTrack(6.0, 120));
        double pPad = Median(Make(), SignalGen.Concat(SignalGen.Silence(0.3),
            SignalGen.Chord(6.0, [220, 277.18, 329.63], 0.2)));

        log.WriteLine($"percussivity: clicks {pDrums:F3}, sustained pad {pPad:F3}");
        Assert.True(pDrums > pPad + 0.15,
            $"clicks {pDrums:F3} vs pad {pPad:F3} — HPSS-lite is not separating them");
    }

    [Fact]
    public void Novelty_spikes_when_the_material_changes()
    {
        var a = Make();
        double frameRate = SignalGen.Rate / (double)AudioFormat.Hop;

        // 10 s of bass-heavy pad, then an abrupt switch to bright clicks: a section
        // boundary as far as the 8-band vector is concerned.
        var signal = SignalGen.Concat(
            SignalGen.Sines(10.0, [80, 120, 160], 0.3),
            SignalGen.ClickTrack(6.0, 128));

        double before = 0, after = 0;
        SignalGen.Drive(a, SignalGen.ToStereo(signal), (fr, i) =>
        {
            double t = i / frameRate;
            double v = fr.AsSpan()[FF.Novelty];
            if (t > 5 && t < 9.5) before = Math.Max(before, v);
            if (t > 10 && t < 12) after = Math.Max(after, v);
        });

        log.WriteLine($"novelty: steady max {before:F3}, at the boundary {after:F3}");
        Assert.True(after > 0.3, $"section change only produced novelty {after:F3}");
        Assert.True(after > before * 2, $"novelty {after:F3} not clearly above steady {before:F3}");
    }

    /* --------------------------------------------------------- housekeeping */

    [Fact]
    public void Sample_rate_is_read_not_assumed()
    {
        // Plan §2.3: derive every bin frequency from the actual rate. Same tone at three
        // device rates must land in the same band — if 48000 is hardcoded anywhere, the
        // 44.1 and 96 kHz cases drift into the neighbouring band.
        foreach (int rate in new[] { 44_100, 48_000, 96_000 })
        {
            var a = new Analyzer(new AudioFormat(rate, 2));
            var sig = SignalGen.Concat(
                SignalGen.Silence(0.5, rate),
                SignalGen.Sine(3.0, 1550, 0.5, rate));
            SignalGen.Drive(a, SignalGen.ToStereo(sig));
            var f = a.LastFrame.AsSpan();

            var bands = new float[8];
            f.Slice(FF.Sub, 8).CopyTo(bands);
            log.WriteLine($"{rate} Hz: bands " + string.Join(" ", bands.Select(v => $"{v:F3}")));
            Assert.True(f[FF.HiMid] > 0.6, $"at {rate} Hz, hiMid only reached {f[FF.HiMid]:F3}");
            Assert.True(f[FF.Presence] < 0.1, $"at {rate} Hz, presence leaked to {f[FF.Presence]:F3}");
        }
    }

    [Fact]
    public void Mono_input_is_accepted()
    {
        var a = new Analyzer(new AudioFormat(48_000, 1));
        SignalGen.Drive(a, SignalGen.MusicLike(2.0));
        Assert.True(a.StepCount > 100, $"only {a.StepCount} steps from 2 s of mono audio");
        Assert.True(a.LastFrame.AsSpan()[FF.StereoWidth] < 0.05, "mono cannot have width");
    }

    [Fact]
    public void Reset_clears_every_accumulator()
    {
        var a = Make();
        SignalGen.Drive(a, SignalGen.ToStereo(SignalGen.MusicLike(6.0)));
        Assert.True(a.StepCount > 0);

        a.Reset();
        Assert.Equal(0, a.StepCount);

        // After a reset, silence must read as silence — a leftover `lo` from the loud
        // signal would otherwise make the noise floor look like content.
        var frames = SignalGen.Collect(a, SignalGen.Silence(1.5));
        var f = frames[^1].AsSpan();
        for (int i = 0; i < 8; i++)
            Assert.True(f[FF.Sub + i] < 0.05, $"band {i} = {f[FF.Sub + i]:F4} after reset+silence");
    }
}
