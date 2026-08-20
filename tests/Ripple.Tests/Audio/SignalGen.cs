using Ripple.Audio;
using Ripple.Render;

namespace Ripple.Tests.Audio;

/// <summary>
/// Synthetic test signals, and the loop that drives an <see cref="Analyzer"/> over them.
///
/// Everything here is deterministic. The DSP tests assert numeric properties — a tempo
/// within 2 BPM, a band isolated to 0.1 — and a test that fails one run in twenty
/// teaches people to re-run it rather than read it.
/// </summary>
internal static class SignalGen
{
    public const int Rate = 48_000;

    /// <summary>Mono to interleaved stereo, same signal both sides.</summary>
    public static float[] ToStereo(float[] mono)
    {
        var o = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) { o[i * 2] = mono[i]; o[i * 2 + 1] = mono[i]; }
        return o;
    }

    public static float[] Silence(double seconds, int rate = Rate)
        => new float[(int)(seconds * rate)];

    public static float[] Sine(double seconds, double hz, double amp = 0.5, int rate = Rate)
    {
        var o = new float[(int)(seconds * rate)];
        for (int i = 0; i < o.Length; i++)
            o[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate));
        return o;
    }

    /// <summary>
    /// A chord voiced the way an instrument would play it: each note with two harmonics,
    /// plus the root an octave below at a higher level.
    ///
    /// The bass note is what makes the root unambiguous. Krumhansl-Schmuckler templates
    /// are built for musical context, and a bare triad sits almost exactly between a major
    /// key and its relative minor — an F/A/C triad scored F major and A minor within 0.011
    /// of each other, so which one won was down to profile detail rather than the input.
    /// Real music resolves that with a bass line; so does this.
    /// </summary>
    public static float[] Chord(double seconds, double[] roots, double amp = 0.12, int rate = Rate)
    {
        var hz = new List<double>();
        foreach (var f in roots) { hz.Add(f); hz.Add(f * 2); hz.Add(f * 3); }
        var o = Sines(seconds, hz.ToArray(), amp, rate);
        // Root an octave down, at the same level as the chord tones: enough to establish
        // the tonic, not enough to become the only thing in the chroma vector.
        var bass = Sine(seconds, roots[0] * 0.5, amp, rate);
        for (int i = 0; i < o.Length; i++) o[i] += bass[i];
        return o;
    }

    /// <summary>Sum of sines — a chord, or a tone plus its harmonics.</summary>
    public static float[] Sines(double seconds, double[] hz, double amp = 0.3, int rate = Rate)
    {
        var o = new float[(int)(seconds * rate)];
        foreach (var f in hz)
            for (int i = 0; i < o.Length; i++)
                o[i] += (float)(amp * Math.Sin(2 * Math.PI * f * i / rate));
        return o;
    }

    /// <summary>
    /// A click track: short broadband bursts at a fixed BPM. Each click is filtered
    /// noise with a fast exponential decay, which is what the onset detector is looking
    /// for — a clean impulse would be an unrealistically easy target for spectral flux.
    /// </summary>
    public static float[] ClickTrack(double seconds, double bpm, int rate = Rate, int seed = 12345)
    {
        var o = new float[(int)(seconds * rate)];
        var rng = new Mulberry32((uint)seed);
        double period = 60.0 / bpm;
        for (double t = 0; t < seconds; t += period)
            AddClick(o, (int)(t * rate), rate, ref rng);
        return o;
    }

    /// <summary>
    /// A click track whose tempo drifts linearly from <paramref name="bpmFrom"/> to
    /// <paramref name="bpmTo"/> — the Grateful Dead case, and the one the plan calls out
    /// explicitly in §14.
    /// </summary>
    public static float[] DriftingClickTrack(double seconds, double bpmFrom, double bpmTo,
                                             int rate = Rate, int seed = 999)
    {
        var o = new float[(int)(seconds * rate)];
        var rng = new Mulberry32((uint)seed);
        double t = 0;
        while (t < seconds)
        {
            AddClick(o, (int)(t * rate), rate, ref rng);
            double bpm = bpmFrom + (bpmTo - bpmFrom) * (t / seconds);
            t += 60.0 / bpm;
        }
        return o;
    }

    /// <summary>The instantaneous tempo of <see cref="DriftingClickTrack"/> at time t.</summary>
    public static double DriftBpmAt(double t, double seconds, double bpmFrom, double bpmTo)
        => bpmFrom + (bpmTo - bpmFrom) * (t / seconds);

    /// <summary>
    /// Clicks at irregular intervals: onsets with no periodicity at all. This is the
    /// Drums → Space case, and what the bpmConfidence &lt; 0.35 fallback exists for.
    /// </summary>
    public static float[] ArrhythmicClicks(double seconds, int rate = Rate, int seed = 4242)
    {
        var o = new float[(int)(seconds * rate)];
        var rng = new Mulberry32((uint)seed);
        double t = 0.2;
        while (t < seconds)
        {
            AddClick(o, (int)(t * rate), rate, ref rng);
            // 150 ms .. 1.65 s. The earlier 0.12-1.02 s spread was narrow enough that a
            // run of similar intervals looked periodic, and confidence hovered either side
            // of the 0.35 fallback threshold. Drums -> Space is more irregular than that.
            t += 0.15 + 1.5 * rng.NextDouble();
        }
        return o;
    }

    private static void AddClick(float[] o, int at, int rate, ref Mulberry32 rng)
    {
        int len = Math.Min(o.Length - at, rate / 20);       // 50 ms tail
        if (at < 0 || len <= 0) return;
        double lp = 0;
        for (int i = 0; i < len; i++)
        {
            double n = rng.NextDouble() * 2 - 1;
            lp += 0.55 * (n - lp);                          // take the edge off
            o[at + i] += (float)(lp * 0.9 * Math.Exp(-i / (rate * 0.012)));
        }
    }

    /// <summary>
    /// Something music-shaped: a plucked chord with a full harmonic series, a bass line,
    /// kick, hats and a crash every four bars.
    ///
    /// It is not music, but it has to be realistic in two specific ways or the
    /// distribution comparison against the synthetic driver measures the signal rather
    /// than the DSP:
    ///
    /// SPECTRALLY. An earlier version topped out around 500 Hz plus brief 12 ms hats, so
    /// hiMid through air sat more than 35 dB below the loudest band and the analyzer
    /// correctly gated them to zero. Harmonics run to the 9th and the cymbals have real
    /// energy and real decay, so all eight bands carry content.
    ///
    /// DYNAMICALLY. The chord was a continuous pad with a 0.13 Hz tremolo — slower than the
    /// normalizer's own 3 s and 8 s windows, so every band read as steady and normalized to
    /// ~0.95. Real material is plucked and re-struck: notes decay, so the band envelopes
    /// move over the window the normalizer actually looks at.
    /// </summary>
    public static float[] MusicLike(double seconds, double bpm = 108, int rate = Rate, int seed = 7)
    {
        int n = (int)(seconds * rate);
        var o = new float[n];
        var rng = new Mulberry32((uint)seed);

        double[] roots = [110.0, 146.83, 164.81, 130.81];   // A2 D3 E3 C3
        double beatSeconds = 60.0 / bpm;
        double barSeconds = 4 * beatSeconds;

        // Harmonic series with a 1/n rolloff — a plucked string, roughly.
        double[] mult = [1.0, 1.26, 1.5, 2.0, 2.52, 3.0, 4.0, 5.04, 6.0, 8.0, 9.0];

        double hatLp = 0, crashLp = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate;
            int chord = (int)(t / (barSeconds * 2)) % roots.Length;
            double root = roots[chord];

            // Chord re-struck every half bar, each strike decaying over ~1.1 s.
            double strike = t / (barSeconds * 0.5);
            double intoStrike = (strike - Math.Floor(strike)) * barSeconds * 0.5;
            double pluck = Math.Exp(-intoStrike / 1.1) * (1 - Math.Exp(-intoStrike / 0.006));

            double s = 0;
            for (int h = 0; h < mult.Length; h++)
            {
                double f = root * mult[h];
                if (f > rate * 0.45) break;
                s += (0.16 / (1 + h)) * pluck * Math.Sin(2 * Math.PI * f * t);
            }

            // Bass, plucked on the beat.
            double beat = t * bpm / 60.0;
            double intoBeat = (beat - Math.Floor(beat)) * beatSeconds;
            double bassEnv = Math.Exp(-intoBeat / 0.35);
            s += bassEnv * (0.30 * Math.Sin(2 * Math.PI * root * 0.5 * t)
                          + 0.10 * Math.Sin(2 * Math.PI * root * t));

            // Kick: a swept low sine, which is most of the sub and bass transient.
            s += 0.55 * Math.Exp(-intoBeat / 0.045)
                      * Math.Sin(2 * Math.PI * (95 - 45 * Math.Exp(-intoBeat / 0.03)) * intoBeat);

            // Hats on the half beat: high-passed noise with a 60 ms tail, so presence and
            // brilliance have something sustained enough for an envelope to follow.
            double half = beat * 2;
            double intoHalf = (half - Math.Floor(half)) * beatSeconds * 0.5;
            double nz = rng.NextDouble() * 2 - 1;
            double prev = hatLp;
            hatLp += 0.55 * (nz - hatLp);
            double hp = nz - prev;                              // crude high-pass
            s += 0.30 * hp * Math.Exp(-intoHalf / 0.060);

            // Crash every four bars, 1.5 s decay: this is what puts energy in air.
            double bar4 = t / (barSeconds * 4);
            double intoCrash = (bar4 - Math.Floor(bar4)) * barSeconds * 4;
            double cn = rng.NextDouble() * 2 - 1;
            double cprev = crashLp;
            crashLp += 0.75 * (cn - crashLp);
            s += 0.22 * (cn - cprev) * Math.Exp(-intoCrash / 1.5);

            o[i] = (float)(s * 0.6);
        }
        return o;
    }

    /* ------------------------------------------------------------------ driving */

    /// <summary>
    /// Push a whole signal through an analyzer, invoking <paramref name="onFrame"/> for
    /// every completed hop. Chunked at an awkward size on purpose: production feeds
    /// arrive in whatever buffer WASAPI hands over, never in neat hop multiples.
    /// </summary>
    public static int Drive(Analyzer a, float[] interleaved, Action<FeatureFrame, int>? onFrame = null,
                            int chunk = 1777)
    {
        var frame = default(FeatureFrame);
        int steps = 0;
        for (int off = 0; off < interleaved.Length; off += chunk)
        {
            int len = Math.Min(chunk, interleaved.Length - off);
            a.Append(interleaved.AsSpan(off, len));
            while (a.TryStep(ref frame))
                onFrame?.Invoke(frame, steps++);
        }
        return steps;
    }

    /// <summary>Drive a mono signal, returning every frame. Convenience for short runs.</summary>
    public static List<FeatureFrame> Collect(Analyzer a, float[] mono)
    {
        var frames = new List<FeatureFrame>();
        Drive(a, ToStereo(mono), (f, _) => frames.Add(f));
        return frames;
    }

    public static float[] Concat(params float[][] parts)
    {
        int n = 0;
        foreach (var p in parts) n += p.Length;
        var o = new float[n];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(o, at); at += p.Length; }
        return o;
    }

    /// <summary>Percentile of a copy of the values, linear interpolation, 0..1.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var v = values.ToArray();
        Array.Sort(v);
        double x = p * (v.Length - 1);
        int i = (int)x;
        return i + 1 < v.Length ? v[i] + (v[i + 1] - v[i]) * (x - i) : v[i];
    }
}
