namespace Ripple.Audio;

/// <summary>
/// The capture format, read from the device at runtime.
///
/// Plan §2.3: device format varies — 44.1 / 48 / 96 kHz, usually IEEE float32 but
/// sometimes 16-bit PCM — so every bin frequency is derived from
/// <see cref="SampleRate"/>. Nothing in the DSP may assume 48000; the analyzer tests
/// drive 44.1, 48 and 96 kHz through the same assertions for exactly that reason.
/// </summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    /// <summary>Analysis window, plan §7. 2048 at 48 kHz is 42.7 ms.</summary>
    public const int Window = 2048;

    /// <summary>Hop between analyses, plan §7. 512 gives ~93 analyses/s at 48 kHz.</summary>
    public const int Hop = 512;

    /// <summary>Analyses per second at this rate — the DSP's own frame rate.</summary>
    public double FrameRate => SampleRate / (double)Hop;

    /// <summary>Seconds per hop. The dt every envelope in the analyzer integrates over.</summary>
    public double HopSeconds => Hop / (double)SampleRate;

    /// <summary>Hz per FFT bin.</summary>
    public double BinHz => SampleRate / (double)Window;

    public override string ToString() => $"{SampleRate} Hz, {Channels} ch";
}
