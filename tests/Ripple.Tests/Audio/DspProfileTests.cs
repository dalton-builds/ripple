using Ripple.Audio;
using Ripple.Render;
using Xunit;
using Xunit.Abstractions;

namespace Ripple.Tests.Audio;

/// <summary>
/// Prints the DSP's response profile across a set of reference signals, and asserts the
/// separations the calibration constants in <see cref="Analyzer"/> were fitted against.
///
/// This is the regression guard on those constants. The numbers in the analyzer's comments
/// — confidence 0.88-1.00 on click tracks against 0.00 in free time, percussivity 0.87 on
/// drums against 0.13 on a pad — came from measurements, and a change that quietly moves
/// them should fail here rather than three phases later when the visuals feel wrong.
/// </summary>
public sealed class DspProfileTests(ITestOutputHelper log)
{
    private static Analyzer Run(float[] mono)
    {
        var a = new Analyzer(new AudioFormat(SignalGen.Rate, 2));
        SignalGen.Drive(a, SignalGen.ToStereo(mono));
        return a;
    }

    [Fact]
    public void Tempo_confidence_profile()
    {
        log.WriteLine($"  {"signal",-16} {"bpm",8} {"conf",7} {"acf peak",9}");
        log.WriteLine("  " + new string('-', 44));

        var conf = new Dictionary<string, double>();
        var bpm = new Dictionary<string, double>();
        foreach (var (name, sig) in new (string, float[])[]
        {
            ("click 60", SignalGen.ClickTrack(20.0, 60)),
            ("click 90", SignalGen.ClickTrack(20.0, 90)),
            ("click 120", SignalGen.ClickTrack(20.0, 120)),
            ("click 132", SignalGen.ClickTrack(20.0, 132)),
            ("click 150", SignalGen.ClickTrack(20.0, 150)),
            ("click 180", SignalGen.ClickTrack(20.0, 180)),
            ("arrhythmic", SignalGen.ArrhythmicClicks(20.0)),
            ("pad", SignalGen.Sines(20.0, [220, 277, 330], 0.2)),
        })
        {
            var a = Run(sig);
            conf[name] = a.BpmConfidence;
            bpm[name] = a.Bpm;
            log.WriteLine($"  {name,-16} {a.Bpm,8:F2} {a.BpmConfidence,7:F3} {a.AcfPeak,9:F4}");
        }

        // Every metronomic tempo must clear the §7.3 fallback threshold, and by a margin —
        // the point of the correlation-coefficient form is that certainty does not depend
        // on tempo. The old peak/mean ratio slid from 3.62 at 60 BPM to 1.28 at 180.
        foreach (var name in new[] { "click 60", "click 90", "click 120", "click 132",
                                     "click 150", "click 180" })
        {
            Assert.True(conf[name] > 0.6,
                $"{name}: confidence {conf[name]:F3} — a metronome should be unambiguous");
            Assert.True(Math.Abs(bpm[name] - double.Parse(name["click ".Length..])) < 2.5,
                $"{name}: tracked {bpm[name]:F2}");
        }

        // And free time must fall well below it, or the visuals insist on a grid that is
        // not there through Drums -> Space.
        Assert.True(conf["arrhythmic"] < 0.3,
            $"arrhythmic confidence {conf["arrhythmic"]:F3} is too close to the 0.35 threshold");
        Assert.True(conf["pad"] < 0.05, $"a pad with no transients reported {conf["pad"]:F3}");

        double margin = conf.Where(k => k.Key.StartsWith("click")).Min(k => k.Value)
                      - conf["arrhythmic"];
        log.WriteLine($"\n  margin between the weakest click track and free time: {margin:F3}");
        Assert.True(margin > 0.35, $"only {margin:F3} separates rhythm from free time");
    }

    [Fact]
    public void Percussivity_and_key_profile()
    {
        log.WriteLine($"  {"signal",-16} {"perc",7} {"key",7} {"keyConf",8}");
        log.WriteLine("  " + new string('-', 42));

        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        double frameRate = SignalGen.Rate / (double)AudioFormat.Hop;

        double Perc(float[] sig)
        {
            var a = new Analyzer(new AudioFormat(SignalGen.Rate, 2));
            var vals = new List<double>();
            SignalGen.Drive(a, SignalGen.ToStereo(sig), (fr, i) =>
            {
                if (i / frameRate >= 1.0) vals.Add(fr.AsSpan()[FF.Percussivity]);
            });
            return SignalGen.Percentile(vals, 0.5);
        }

        var rows = new (string Name, float[] Sig)[]
        {
            ("drums", SignalGen.ClickTrack(6.0, 120)),
            ("pad (A minor)", SignalGen.Concat(SignalGen.Silence(0.3),
                SignalGen.Chord(6.0, [440.0, 523.25, 659.25]))),
            ("music-like", SignalGen.MusicLike(6.0)),
        };

        var perc = new Dictionary<string, double>();
        foreach (var (name, sig) in rows)
        {
            perc[name] = Perc(sig);
            var a = Run(sig);
            log.WriteLine($"  {name,-16} {perc[name],7:F3} "
                + $"{names[a.KeyPitchClass] + (a.KeyIsMinor ? "m" : ""),7} {a.KeyConfidence,8:F3}");
        }

        // HPSS-lite must put drums above a sustained chord by a wide margin. It reported
        // the two the wrong way round twice: once from summing the medians globally instead
        // of masking per bin, once from a 53 ms temporal median that a drum hit filled.
        Assert.True(perc["drums"] > 0.6, $"drums measured {perc["drums"]:F3}");
        Assert.True(perc["pad (A minor)"] < 0.3, $"a sustained chord measured {perc["pad (A minor)"]:F3}");
        Assert.True(perc["drums"] - perc["pad (A minor)"] > 0.4,
            "HPSS-lite is not separating transients from sustain");
    }
}
