using Silk.NET.OpenGL;

namespace Ripple.Render;

/// <summary>
/// The host-side state a frame depends on beyond the Feature Frame itself:
/// motif envelopes, palette rotation, and the closed-loop safety limiter.
///
/// This is the part of the port that is easy to get almost right. The shaders
/// are stateless by design — every envelope, every smoothed rotation and the
/// limiter's feedback gain live here, which means a subtle difference in this
/// file shows up as drift late in a run rather than as an obvious error on
/// frame one. Each block is a line-for-line mirror of advanceMotifs(),
/// advancePalette() and measureLuminance() in the harness.
/// </summary>
public sealed class HostState
{
    // --- motif envelopes (host side, so the shaders stay stateless) --------
    public double Rose, RoseSeed = 1.3, RoseCool;
    public double Bolt, BoltSeed = 7.1, BoltCool;
    private Mulberry32 _rng = new(Mulberry32.MotifSeed);

    // --- palette rotation --------------------------------------------------
    public double PalRotSmooth, PalRotPrev, HueRate, SpiralSpin;

    // --- safety limiter (plan §12) ----------------------------------------
    public double SafeMean, SafePrev, SafeScale = 1, SafeLastD, FlashRate;

    /// <summary>
    /// §11's idle dim (1.0 live, 0.30 quiet) and §12's master intensity.
    ///
    /// Both multiply the composite, and both live here rather than in DrawState because
    /// neither is authored: a preset cannot set them, which is what makes master intensity a
    /// safety control rather than a look control. Intensity is clamped to 1.0 by everything
    /// that writes it — it takes brightness away and never adds any.
    /// </summary>
    public double IdleDim = 1.0, MasterIntensity = 1.0;
    private readonly List<double> _zeroCrossings = [];
    private readonly byte[] _pixel = new byte[4];

    /// <summary>
    /// Put every accumulator back to its cold-start value and reseed the motif
    /// RNG. Called at the top of a deterministic run — miss one field here and
    /// the parity diff silently measures whatever the host was doing before.
    /// </summary>
    public void Reset()
    {
        Rose = Bolt = 0;
        RoseCool = BoltCool = 0;
        RoseSeed = 1.3; BoltSeed = 7.1;
        _rng = new Mulberry32(Mulberry32.MotifSeed);

        PalRotSmooth = PalRotPrev = HueRate = SpiralSpin = 0;

        SafeMean = SafePrev = 0;
        SafeScale = 1;
        SafeLastD = 0;
        FlashRate = 0;
        IdleDim = 1.0;
        MasterIntensity = 1.0;
        _zeroCrossings.Clear();
    }

    /// <summary>Mirror of advanceMotifs(). Roses bloom on chord change, bolts on onsets.</summary>
    public void AdvanceMotifs(double dt, DrawState d, ReadOnlySpan<float> f)
    {
        Rose *= Math.Exp(-dt / 1.15);
        RoseCool -= dt;
        if (f[FF.ChromaFlux] > d.RoseThreshold && RoseCool <= 0)
        {
            Rose = 1;
            RoseSeed = _rng.NextDouble() * 90;
            RoseCool = 0.85;
        }

        Bolt *= Math.Exp(-dt / 0.155);
        BoltCool -= dt;
        if (f[FF.Onset] > 0.82 && f[FF.Percussivity] > d.BoltThreshold && BoltCool <= 0)
        {
            Bolt = 1;
            BoltSeed = _rng.NextDouble() * 90;
            BoltCool = 0.14;
        }
    }

    /// <summary>
    /// Mirror of advancePalette(). The rotation is smoothed along the SHORTEST
    /// circular path, because taking the long way round the wheel reads as a
    /// hue sweep rather than a modulation. HueRate feeds the red↔blue guard in
    /// safety.glsl, so it has to be measured here rather than guessed there.
    /// </summary>
    public void AdvancePalette(double dt, DrawState d, bool rotateByKey, ReadOnlySpan<float> f)
    {
        double wantRaw = rotateByKey ? d.PalRot : 0;
        double delta = (wantRaw - PalRotSmooth) % 1;
        if (delta > 0.5) delta -= 1;
        if (delta < -0.5) delta += 1;
        PalRotSmooth += delta * Math.Min(1, dt * 1.6);
        PalRotSmooth = ((PalRotSmooth % 1) + 1) % 1;

        double hd = Math.Abs(PalRotSmooth - PalRotPrev);
        if (hd > 0.5) hd = 1 - hd;
        HueRate = hd / Math.Max(dt, 1e-4);
        PalRotPrev = PalRotSmooth;

        SpiralSpin += dt * (f[FF.Bpm] / 60) * 0.25 * 0.18;
    }

    /// <summary>
    /// Mirror of measureLuminance() — plan §12's closed loop. Mean frame
    /// luminance comes from the composite target's own 1×1 mip: four bytes of
    /// readback instead of a full-resolution stall.
    /// </summary>
    public void MeasureLuminance(GL gl, RenderTargets rt, double dt, double tSec, DrawState d)
    {
        gl.BindTexture(TextureTarget.Texture2D, rt.Final);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.MipFbo);
        unsafe
        {
            fixed (byte* p = _pixel)
                gl.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        SafePrev = SafeMean;
        SafeMean = (0.2126 * _pixel[0] + 0.7152 * _pixel[1] + 0.0722 * _pixel[2]) / 255;
        double delta = SafeMean - SafePrev;

        // A reversal only counts as a flash when the swing is large enough to
        // read as one. Sign changes alone fire on any lively scene, and a
        // limiter that dims a merely busy image is a bug, not caution.
        double swing = Math.Max(0.05, d.MaxLumDelta * 0.8);
        if (Math.Sign(delta) != Math.Sign(SafeLastD)
            && Math.Abs(delta) > swing && Math.Abs(SafeLastD) > swing)
        {
            _zeroCrossings.Add(tSec);
        }
        while (_zeroCrossings.Count > 0 && tSec - _zeroCrossings[0] > 1.0) _zeroCrossings.RemoveAt(0);
        FlashRate = _zeroCrossings.Count * 0.5;
        if (Math.Abs(delta) > swing * 0.5) SafeLastD = delta;

        double want = 1;
        if (delta > d.MaxLumDelta)
            want = Math.Max(0.20, (SafePrev + d.MaxLumDelta) / Math.Max(SafeMean, 1e-3));
        // Above 3 Hz, back off proportionally rather than slamming to a floor.
        if (FlashRate > 3)
            want = Math.Min(want, Math.Max(0.45, 1 - 0.14 * (FlashRate - 3)));

        SafeScale = want < SafeScale ? want : Math.Min(1, SafeScale + dt * 1.2);
    }
}
