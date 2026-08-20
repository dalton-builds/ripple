using System.Numerics;

namespace Ripple.Audio;

/// <summary>
/// Radix-2 iterative FFT, real input, zero allocation after construction.
///
/// Plan §7 allows "a straightforward radix-2 with the imaginary half zeroed — ~80 lines,
/// no external dependency", which is what this is. A 2048-point transform ~93 times a
/// second is nothing next to the 1%-of-a-core budget, so there is no reason to reach for a
/// split-radix or a real-only packing and give up the clarity.
///
/// Convention: unnormalized forward transform,
/// <c>X[k] = sum over n of x[n] * exp(-2*pi*i*k*n/N)</c>. So a unit impulse gives 1 in
/// every bin, DC gives N in bin 0, and a full-scale sine on a bin centre gives N/2. The
/// tests pin all three; they are the reason a scale error cannot survive here quietly.
///
/// All scratch is preallocated and every method is span-based. Nothing in this file may
/// allocate: it runs on the analysis thread inside the Phase 4 zero-Gen0 budget.
/// </summary>
public sealed class Fft
{
    private readonly int _n;
    private readonly int _log2N;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly int[] _reverse;
    private readonly float[] _cos;
    private readonly float[] _sin;

    public int Size => _n;

    /// <summary>Bins in a real spectrum: 0..N/2 inclusive.</summary>
    public static int Bins(int size) => size / 2 + 1;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException($"FFT size must be a power of two, got {size}", nameof(size));

        _n = size;
        _log2N = BitOperations.Log2((uint)size);
        _re = new float[size];
        _im = new float[size];

        // Bit-reversal permutation, precomputed: the alternative is recomputing a reversal
        // per sample per transform, which is most of the cost of a small FFT.
        _reverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < _log2N; b++) r |= ((i >> b) & 1) << (_log2N - 1 - b);
            _reverse[i] = r;
        }

        // Twiddles for every stage, laid out contiguously so the inner loop walks forward.
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(-2 * Math.PI * i / size);
            _sin[i] = (float)Math.Sin(-2 * Math.PI * i / size);
        }
    }

    /// <summary>
    /// Forward transform of real input. <paramref name="re"/> and <paramref name="im"/>
    /// receive the first <see cref="Bins"/> bins — the rest of the spectrum is the
    /// conjugate mirror and carries no extra information for real input.
    /// </summary>
    public void Forward(ReadOnlySpan<float> input, Span<float> re, Span<float> im)
    {
        Transform(input);
        int bins = Bins(_n);
        if (re.Length < bins || im.Length < bins)
            throw new ArgumentException($"output spans must hold {bins} bins");
        _re.AsSpan(0, bins).CopyTo(re);
        _im.AsSpan(0, bins).CopyTo(im);
    }

    /// <summary>
    /// Magnitude spectrum of real input, <see cref="Bins"/> values.
    /// </summary>
    public void Magnitude(ReadOnlySpan<float> input, Span<float> magnitude)
    {
        Transform(input);
        int bins = Bins(_n);
        if (magnitude.Length < bins)
            throw new ArgumentException($"magnitude span must hold {bins} bins");
        for (int k = 0; k < bins; k++)
            magnitude[k] = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
    }

    private void Transform(ReadOnlySpan<float> input)
    {
        if (input.Length != _n)
            throw new ArgumentException($"input must be {_n} samples, got {input.Length}");

        // Load in bit-reversed order; the imaginary half starts at zero because the input
        // is real.
        for (int i = 0; i < _n; i++) _re[_reverse[i]] = input[i];
        Array.Clear(_im);

        // Iterative Cooley-Tukey. `step` is the half-size of the butterfly at this stage.
        for (int step = 1; step < _n; step <<= 1)
        {
            int jump = step << 1;
            int twiddleStride = _n / jump;

            for (int group = 0; group < step; group++)
            {
                int t = group * twiddleStride;
                float wr = _cos[t], wi = _sin[t];

                for (int pair = group; pair < _n; pair += jump)
                {
                    int match = pair + step;
                    float xr = _re[match], xi = _im[match];
                    float tr = wr * xr - wi * xi;
                    float ti = wr * xi + wi * xr;

                    _re[match] = _re[pair] - tr;
                    _im[match] = _im[pair] - ti;
                    _re[pair] += tr;
                    _im[pair] += ti;
                }
            }
        }
    }
}
