using System.Diagnostics;
using Ripple.Audio;
using Ripple.Render;
using Xunit;
using Xunit.Abstractions;

namespace Ripple.Tests.Audio;

/// <summary>
/// Plan §11 budgets the analysis chain at "under 1% of one core". This measures it, and
/// breaks the cost down by stage so a regression says WHICH stage got expensive.
///
/// Run under Release for a number that means anything — a Debug build measured 2.8x the
/// Release cost on the same machine, which is a property of the JIT, not of the DSP.
/// </summary>
public sealed class DspCostTests(ITestOutputHelper log)
{
    private const double Seconds = 20;

    [Fact]
    public void Analysis_chain_cost()
    {
        var fmt = new AudioFormat(SignalGen.Rate, 2);
        var signal = SignalGen.ToStereo(SignalGen.MusicLike(Seconds));

        // Warm the JIT before measuring; the first few hops are compilation, not DSP.
        var warm = new Analyzer(fmt);
        SignalGen.Drive(warm, SignalGen.ToStereo(SignalGen.MusicLike(2.0)));

        var a = new Analyzer(fmt);
        var frame = default(FeatureFrame);
        int steps = 0;

        var sw = Stopwatch.StartNew();
        for (int off = 0; off < signal.Length; off += 4096)
        {
            int len = Math.Min(4096, signal.Length - off);
            a.Append(signal.AsSpan(off, len));
            while (a.TryStep(ref frame)) steps++;
        }
        sw.Stop();

        double msPerHop = sw.Elapsed.TotalMilliseconds / steps;
        double hopsPerSecond = fmt.FrameRate;
        double coreFraction = msPerHop * hopsPerSecond / 1000.0;

        log.WriteLine($"{steps} hops over {Seconds}s of audio");
        log.WriteLine($"  {msPerHop:F4} ms/hop");
        log.WriteLine($"  {hopsPerSecond:F1} hops/s");
        log.WriteLine($"  {coreFraction * 100:F2}% of one core   (§11 budget: 1.00%)");
        log.WriteLine("");
        log.WriteLine("  per stage, ms/hop:");
        foreach (var (name, ms) in a.StageCostMs())
            log.WriteLine($"    {name,-14} {ms,8:F4}  {ms / msPerHop * 100,5:F1}%");

        // Deliberately generous next to the 1% budget: this runs on whatever machine the
        // suite runs on, often Debug, often alongside other work. It is a guard against a
        // stage becoming an order of magnitude more expensive, not the budget itself —
        // that number is measured in Release and recorded in the README.
        Assert.True(coreFraction < 0.20,
            $"analysis chain at {coreFraction * 100:F2}% of a core is far past the 1% budget");
    }
}
