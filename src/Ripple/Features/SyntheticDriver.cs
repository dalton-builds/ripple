using Ripple.Render;

namespace Ripple.Features;

/// <summary>
/// The synthetic feature driver, ported from the harness.
///
/// This is the primary input mode for Phase 1 and the reason the phase can be
/// built with no audio at all: a slider and an optional LFO per Feature Frame
/// field, plus an auto-rhythm block that fabricates plausible beats, onsets and
/// chord changes. It decouples "make it look right" from "make capture work",
/// and later it reproduces any audio condition on demand.
/// </summary>
/// <remarks>
/// NOT part of the cross-host parity contract — the recorded timeline is (plan
/// §15). The harness seeds its LFO phases and its auto-rhythm coin flips from
/// Math.random(), so the two drivers agree in behaviour and statistics but not
/// frame for frame. This one is seeded from <see cref="Mulberry32"/> so at
/// least it is repeatable run to run on this host.
/// </remarks>
public sealed class SyntheticDriver
{
    public enum Shape { Sine, Tri, Saw, Sqr, Rnd }

    public struct Lfo
    {
        public bool On;
        public double Rate, Depth, Seed;
        public Shape Shape;
    }

    public sealed class Field
    {
        public required string Key;
        public double Min, Max, Default;
        public bool Raw, Auto;
        public double Value;
        public Lfo Lfo;
    }

    /// <summary>
    /// The FIELDS table from the harness, in Feature Frame order. Defaults are
    /// the values the presets were authored against — a still frame of this
    /// driver is the state the harness opens in.
    /// </summary>
    public readonly Field[] Fields;

    public bool AutoRhythm = true;
    private Mulberry32 _rng = new(0x5F356495);

    // rhythm accumulator (§7.3)
    private double _phase, _beats, _sectionPhase;
    private int _bar, _lastBar = -1;
    private double _lastBeats;

    // synthetic envelopes
    private double _onsetEnv, _cfluxEnv, _novEnv;

    public SyntheticDriver()
    {
        Fields = BuildFields();
        SeedDefaultLfos();
    }

    private Field[] BuildFields()
    {
        Field F(string key, double min, double max, double def, bool raw = false, bool auto = false)
            => new()
            {
                Key = key, Min = min, Max = max, Default = def, Raw = raw, Auto = auto,
                Value = def,
                // Same defaults the harness gives every field before seeding.
                Lfo = new Lfo { On = false, Rate = 0.12, Depth = 0.35, Shape = Shape.Sine, Seed = _rng.NextDouble() * 100 },
            };

        double[] chroma = [1.00, 0.18, 0.42, 0.15, 0.66, 0.30, 0.12, 0.72, 0.14, 0.48, 0.16, 0.35];
        var list = new List<Field>(FeatureFrame.FloatCount)
        {
            F("time", 0, 1e6, 0, raw: true, auto: true),
            F("dt", 0, 0.2, 0.016, raw: true, auto: true),
            F("trackPos", 0, 3600, 0, raw: true, auto: true),
            F("trackFrac", 0, 1, 0),

            F("rms", 0, 1, 0.38),
            F("peak", 0, 1, 0.55),
            F("loudSlow", 0, 1, 0.42),
            F("dynamics", 0, 1, 0.28),

            F("sub", 0, 1, 0.35),
            F("bass", 0, 1, 0.45),
            F("lowMid", 0, 1, 0.40),
            F("mid", 0, 1, 0.38),
            F("hiMid", 0, 1, 0.34),
            F("presence", 0, 1, 0.30),
            F("brilliance", 0, 1, 0.25),
            F("air", 0, 1, 0.20),

            F("flux", 0, 1, 0.14),
            F("onset", 0, 1, 0),
            F("beatPhase", 0, 1, 0, auto: true),
            F("barPhase", 0, 1, 0, auto: true),

            F("bpm", 40, 200, 112, raw: true),
            F("bpmConf", 0, 1, 0.72),
            F("beatCount", 0, 1e6, 0, raw: true, auto: true),
            F("percussivity", 0, 1, 0.45),
        };
        for (int i = 0; i < 12; i++) list.Add(F($"ch{i}", 0, 1, chroma[i]));
        list.AddRange(
        [
            F("keyHue", 0, 1, 0.58),
            F("keyConf", 0, 1, 0.62),
            F("chromaFlux", 0, 1, 0.10),
            F("centroid", 0, 1, 0.40),

            F("stereoWidth", 0, 1, 0.45),
            F("stereoBal", -1, 1, 0, raw: true),
            F("novelty", 0, 1, 0.10),
            F("sectionPhase", 0, 1, 0, auto: true),

            F("intensity", 0, 1, 0.92),
            F("era", 0, 7, 2, raw: true),
            F("songMode", 0, 32, 0, raw: true),
            F("paletteMix", 0, 1, 0),

            F("width", 0, 8192, 0, raw: true, auto: true),
            F("height", 0, 8192, 0, raw: true, auto: true),
            F("aspect", 0, 8, 1.777, raw: true, auto: true),
            F("renderScale", 0.4, 1, 0.75, raw: true, auto: true),
        ]);

        if (list.Count != 52)
            throw new InvalidOperationException($"synthetic driver has {list.Count} fields, expected 52 (+4 reserved)");
        return [.. list];
    }

    /// <summary>
    /// A default LFO set, so the host is already alive on launch rather than a
    /// still frame that has to be poked into motion.
    /// </summary>
    private void SeedDefaultLfos()
    {
        void Set(string key, double rate, double depth, Shape shape = Shape.Sine)
        {
            var f = Fields.First(x => x.Key == key);
            f.Lfo.On = true;
            f.Lfo.Rate = rate;
            f.Lfo.Depth = depth;
            f.Lfo.Shape = shape;
        }

        Set("sub", 0.09, 0.55); Set("bass", 0.23, 0.60); Set("lowMid", 0.17, 0.42);
        Set("mid", 0.13, 0.38); Set("hiMid", 0.31, 0.40);
        Set("presence", 0.41, 0.45, Shape.Rnd);
        Set("brilliance", 0.55, 0.40, Shape.Rnd);
        Set("air", 0.07, 0.30);
        Set("rms", 0.05, 0.30); Set("dynamics", 0.11, 0.45);
        Set("stereoWidth", 0.037, 0.60); Set("centroid", 0.083, 0.40);
        Set("keyHue", 0.008, 0.30, Shape.Saw);
        Set("flux", 0.29, 0.5, Shape.Rnd);
    }

    private static double Fract(double x) => x - Math.Floor(x);

    private static double LfoValue(in Lfo l, double t)
    {
        double ph = t * l.Rate + l.Seed;
        switch (l.Shape)
        {
            case Shape.Tri: return 4 * Math.Abs(Fract(ph) - 0.5) - 1;
            case Shape.Saw: return 2 * Fract(ph) - 1;
            case Shape.Sqr: return Fract(ph) < 0.5 ? -1 : 1;
            case Shape.Rnd:
            {
                // l is an `in` parameter and a local function may not capture one
                // (CS1628), so the seed is hoisted. The constants must stay
                // identical to the harness hash or the Rnd LFO desynchronises.
                double seed = l.Seed;
                double i = Math.Floor(ph), k = ph - i;
                double H(double n)
                {
                    double x = Math.Sin(n * 127.1 + seed * 311.7) * 43758.5453;
                    return 2 * (x - Math.Floor(x)) - 1;
                }
                double sm = k * k * (3 - 2 * k);
                return H(i) * (1 - sm) + H(i + 1) * sm;
            }
            default: return Math.Sin(ph * 6.283185307);
        }
    }

    private static double Sample(Field f, double t)
    {
        double v = f.Value;
        if (f.Lfo.On) v += LfoValue(f.Lfo, t) * f.Lfo.Depth * (f.Max - f.Min) * 0.5;
        return Math.Clamp(v, f.Min, f.Max);
    }

    /// <summary>
    /// Phase-locked beat accumulator (§7.3). Deliberately NOT a per-onset
    /// reset — resetting produces visible jitter. When confidence collapses,
    /// beat phase is driven from the decaying onset impulse instead, which is
    /// what makes a free-time passage work rather than insisting on a grid that
    /// is not there.
    /// </summary>
    private void AdvanceRhythm(double dt, double bpm, double conf, double onsetImpulse)
    {
        if (conf >= 0.35)
        {
            _phase += dt * bpm / 60;
            if (onsetImpulse > 0.85)
            {
                // Math.Floor(x + 0.5), not Math.Round: JS rounds halves up.
                double d = _phase - Math.Floor(_phase + 0.5);
                if (Math.Abs(d) < 0.25) _phase -= d * 0.08;
            }
            while (_phase >= 1) { _phase -= 1; _beats++; _bar = (_bar + 1) % 4; }
        }
        else
        {
            _phase = 1 - onsetImpulse;
        }
        _sectionPhase = (_sectionPhase + dt / 45) % 1;
    }

    /// <summary>Fill the frame from the driver. Host-owned fields are left alone.</summary>
    public void Fill(Span<float> f, double dt, double tSec)
    {
        for (int i = 0; i < Fields.Length; i++)
        {
            var fld = Fields[i];
            if (fld.Auto) continue;
            f[i] = (float)Sample(fld, tSec);
        }
        f[FF.TrackPos] = (float)tSec;
        f[FF.TrackFrac] = (float)Fields[FF.TrackFrac].Value;

        if (AutoRhythm)
        {
            AdvanceRhythm(dt, f[FF.Bpm], f[FF.BpmConf], _onsetEnv);
            _onsetEnv *= Math.Exp(-dt / 0.150);

            if (_beats != _lastBeats)
            {
                _lastBeats = _beats;
                _onsetEnv = 0.75 + 0.25 * f[FF.Percussivity];
                if (_bar != _lastBar)
                {
                    _lastBar = _bar;
                    // A chord change roughly every second bar. This is what
                    // makes the rose bloom testable with no audio at all.
                    if (_bar == 0 && _rng.NextDouble() < 0.55)
                        _cfluxEnv = 0.85 + 0.15 * _rng.NextDouble();
                }
            }
            else if (_rng.NextDouble() < dt * 3.2 * f[FF.Percussivity])
            {
                _onsetEnv = Math.Max(_onsetEnv, 0.45 + 0.4 * _rng.NextDouble());
            }

            _cfluxEnv *= Math.Exp(-dt / 0.45);
            _novEnv *= Math.Exp(-dt / 1.6);
            if (_rng.NextDouble() < dt / 22) _novEnv = 0.9;

            f[FF.Onset] = Math.Max(f[FF.Onset], (float)_onsetEnv);
            f[FF.ChromaFlux] = Math.Max(f[FF.ChromaFlux], (float)_cfluxEnv);
            f[FF.Novelty] = Math.Max(f[FF.Novelty], (float)_novEnv);
            f[FF.BeatPhase] = (float)_phase;
            f[FF.BarPhase] = (float)((_bar + _phase) / 4);
            f[FF.BeatCount] = (float)_beats;
            f[FF.SectionPhase] = (float)_sectionPhase;
        }
        else
        {
            f[FF.BeatPhase] = (float)Fields[FF.BeatPhase].Value;
            f[FF.BarPhase] = (float)Fields[FF.BarPhase].Value;
        }
    }

    /// <summary>Set a field's base value by name. Returns false if unknown.</summary>
    public bool Set(string key, double value)
    {
        foreach (var f in Fields)
        {
            if (!string.Equals(f.Key, key, StringComparison.Ordinal)) continue;
            f.Value = Math.Clamp(value, f.Min, f.Max);
            return true;
        }
        return false;
    }
}
