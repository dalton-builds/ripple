using Ripple.Audio;
using Xunit;

namespace Ripple.Tests.Audio;

/// <summary>
/// Plan §14: "FFT against known signals (impulse, DC, single-bin sine) with a magnitude
/// tolerance."
///
/// The FFT is the part most likely to be subtly wrong while looking plausible — a
/// mis-ordered bit reversal or a sign error still produces a spectrum-shaped array, and
/// every feature downstream then drifts in a way that reads as a tuning problem. These
/// three signals have analytically exact answers, so there is nothing to interpret.
///
/// Convention: unnormalized forward DFT, X[k] = sum x[n] exp(-2*pi*i*k*n/N).
/// </summary>
public sealed class FftTests
{
    private const int N = 2048;
    private const double Tol = 1e-3;

    [Fact]
    public void Impulse_is_flat()
    {
        // delta at n=0 transforms to 1 at every bin — a flat magnitude response is the
        // cleanest possible check that no bin is scaled or swapped.
        var fft = new Fft(N);
        var x = new float[N];
        x[0] = 1f;
        var mag = new float[Fft.Bins(N)];
        fft.Magnitude(x, mag);

        for (int k = 0; k < mag.Length; k++)
            Assert.True(Math.Abs(mag[k] - 1.0) < Tol, $"bin {k} = {mag[k]}, expected 1");
    }

    [Fact]
    public void Shifted_impulse_is_still_flat()
    {
        // Magnitude is shift-invariant; only the phase moves. Catches a bit-reversal bug
        // that the n=0 impulse happens to be blind to.
        var fft = new Fft(N);
        var x = new float[N];
        x[7] = 1f;
        var mag = new float[Fft.Bins(N)];
        fft.Magnitude(x, mag);

        for (int k = 0; k < mag.Length; k++)
            Assert.True(Math.Abs(mag[k] - 1.0) < Tol, $"bin {k} = {mag[k]}, expected 1");
    }

    [Fact]
    public void Dc_lands_only_in_bin_zero()
    {
        var fft = new Fft(N);
        var x = new float[N];
        Array.Fill(x, 1f);
        var mag = new float[Fft.Bins(N)];
        fft.Magnitude(x, mag);

        Assert.True(Math.Abs(mag[0] - N) < N * 1e-5, $"bin 0 = {mag[0]}, expected {N}");
        for (int k = 1; k < mag.Length; k++)
            Assert.True(mag[k] < N * 1e-5, $"bin {k} = {mag[k]}, expected ~0");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(511)]
    [InlineData(1023)]
    public void Single_bin_sine_lands_in_that_bin(int k0)
    {
        // A sine exactly on bin centre k0 has magnitude N/2 there and nothing anywhere
        // else — no window, no leakage, so any energy off-bin is an implementation bug.
        var fft = new Fft(N);
        var x = new float[N];
        for (int n = 0; n < N; n++) x[n] = (float)Math.Sin(2 * Math.PI * k0 * n / N);

        var mag = new float[Fft.Bins(N)];
        fft.Magnitude(x, mag);

        Assert.True(Math.Abs(mag[k0] - N / 2.0) < N * 1e-4,
            $"bin {k0} = {mag[k0]}, expected {N / 2.0}");

        for (int k = 0; k < mag.Length; k++)
        {
            if (Math.Abs(k - k0) <= 1) continue;
            Assert.True(mag[k] < N * 1e-4, $"leak into bin {k}: {mag[k]}");
        }
    }

    [Fact]
    public void Cosine_on_bin_centre_is_real_and_positive()
    {
        // Checks the sign convention of the imaginary half: a cosine is purely real.
        var fft = new Fft(N);
        const int k0 = 32;
        var x = new float[N];
        for (int n = 0; n < N; n++) x[n] = (float)Math.Cos(2 * Math.PI * k0 * n / N);

        var re = new float[Fft.Bins(N)];
        var im = new float[Fft.Bins(N)];
        fft.Forward(x, re, im);

        Assert.True(re[k0] > N / 2.0 - N * 1e-4, $"re[{k0}] = {re[k0]}, expected {N / 2.0}");
        Assert.True(Math.Abs(im[k0]) < N * 1e-4, $"im[{k0}] = {im[k0]}, expected ~0");
    }

    [Fact]
    public void Parseval_holds()
    {
        // Total energy is conserved. A single global scale error passes every test above
        // if it is consistent; this one pins the absolute scale.
        var fft = new Fft(N);
        var rng = new Render.Mulberry32(0xC0FFEE);
        var x = new float[N];
        for (int n = 0; n < N; n++) x[n] = (float)(rng.NextDouble() * 2 - 1);

        var re = new float[Fft.Bins(N)];
        var im = new float[Fft.Bins(N)];
        fft.Forward(x, re, im);

        double time = 0;
        for (int n = 0; n < N; n++) time += (double)x[n] * x[n];

        // Sum over the full spectrum: the bins between 1 and N/2-1 each stand for a
        // conjugate pair, so they count twice.
        double freq = (double)re[0] * re[0] + (double)im[0] * im[0];
        int last = Fft.Bins(N) - 1;
        freq += (double)re[last] * re[last] + (double)im[last] * im[last];
        for (int k = 1; k < last; k++)
            freq += 2 * ((double)re[k] * re[k] + (double)im[k] * im[k]);
        freq /= N;

        Assert.True(Math.Abs(time - freq) / time < 1e-4,
            $"Parseval: time {time:F4} vs freq {freq:F4}");
    }

    [Fact]
    public void Size_must_be_a_power_of_two()
        => Assert.Throws<ArgumentException>(() => new Fft(1000));
}
