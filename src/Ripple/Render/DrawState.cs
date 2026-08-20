using Ripple.Presets;

namespace Ripple.Render;

/// <summary>
/// The per-frame draw parameters — the C# mirror of the harness's <c>D</c>
/// object, its <c>resetDraw()</c> and its <c>applyMapping()</c>.
///
/// Rewritten in place every frame; never reallocated. That is the same
/// discipline the harness follows and what the Phase 4 zero-Gen0 assertion
/// will hold this code to.
/// </summary>
public sealed class DrawState
{
    // feedback
    public double Decay, Zoom, Rotate, WarpAmount, WarpScale, BassGain;
    public double Symmetry, Paint, CenterPull, OnsetDecay;

    // fractal
    public double Iterations, EscapeRadius, OrbitRadius, BeatMorph;
    public double Warp, Scale, Flow, Cells, Rim, Iridescence, Mask, Octaves, RimWidth;
    public double TrapGain, ZoomPeriod, ZoomDepth, Fold, Line;

    // motifs
    public double BoltGain, BoltThreshold, BoltFork;
    public double RoseGain, RoseThreshold, RosePetals;
    public double MotifOpacity = 1, MotifGain = 1;

    // particles
    public double PCount, PCurl, PLife, PSize, PEmit = 1, PRise;

    // post
    public double BloomThreshold, BloomGain, BloomTaps = 2, BloomKnee = 0.35;
    public double Grain, Vignette, Exposure = 1, Saturate = 1.3;

    // palette
    public double ChromaBlend, Spiral;

    /// <summary>
    /// Palette rotation accumulator. NOTE: this is the one field
    /// <c>resetDraw()</c> does not clear — it accumulates across frames, and
    /// advancePalette() consumes it modulo 1 along the shortest circular path.
    /// The harness behaves this way and the presets were tuned against it, so
    /// resetting it here would change every authored look. Double, because it
    /// grows without bound and a float would start losing the fraction.
    /// </summary>
    public double PalRot;

    // safety
    public double MaxLumDelta = 0.08, MaxBright = 0.85;

    public string Fractal = "warp_fbm";

    /// <summary>Restore the authored values for this frame, before routing.</summary>
    public void Reset(PresetValues p)
    {
        Decay = p.Decay; Zoom = p.Zoom; Rotate = p.Rotate;
        WarpAmount = p.WarpAmount; WarpScale = p.WarpScale; BassGain = p.BassGain;
        Symmetry = p.Symmetry; CenterPull = p.CenterPull; OnsetDecay = p.OnsetDecay;

        Iterations = p.Iterations; EscapeRadius = p.EscapeRadius;
        OrbitRadius = p.OrbitRadius; BeatMorph = p.BeatMorph;
        Warp = p.Warp; Scale = p.Scale; Flow = p.Flow; Cells = p.Cells;
        Rim = p.Rim; Iridescence = p.Iridescence; Mask = p.Mask; Octaves = p.Octaves;
        RimWidth = p.RimWidth;
        TrapGain = p.TrapGain; ZoomPeriod = p.ZoomPeriod; ZoomDepth = p.ZoomDepth;
        Fold = p.Fold; Line = p.Line;

        BoltGain = p.BoltGain; BoltThreshold = p.BoltThreshold; BoltFork = p.BoltFork;
        RoseGain = p.RoseGain; RoseThreshold = p.RoseThreshold; RosePetals = p.RosePetals;
        MotifOpacity = 1; MotifGain = 1;

        PCount = p.ParticleCount; PCurl = p.CurlScale; PLife = p.Lifetime; PSize = p.ParticleSize;
        PEmit = 1; PRise = p.Rise;

        BloomThreshold = p.BloomThreshold; BloomGain = p.BloomGain;
        BloomTaps = p.BloomTaps; BloomKnee = p.BloomKnee;
        Grain = p.Grain; Vignette = p.Vignette; Exposure = p.Exposure;
        Saturate = p.Saturation;

        ChromaBlend = p.ChromaBlend; Spiral = p.Spiral;

        // Plan §12: a preset may be STRICTER than the limiter floor, never
        // looser. The clamp lives here so there is no code path that relaxes it.
        MaxLumDelta = Math.Min(p.MaxLuminanceDelta, 0.12);
        MaxBright = Math.Min(p.MaxBrightness, 0.92);

        Fractal = p.Fractal;

        // paintGain is normalized by (1 - decay) so that changing decay does
        // not change exposure: the feedback buffer's equilibrium is
        // paint/(1-decay), so folding (1-decay) into the paint cancels it.
        // Equilibrium ends up at roughly paintGain * 30.
        Paint = p.PaintGain * (1 - Math.Min(p.Decay, 0.9995)) * 30;
    }

    /// <summary>
    /// Route feature values into draw parameters. The §10 mapping block is
    /// DATA, not code — this switch is the entire routing surface, and it must
    /// stay identical to MAP_TARGETS in the harness, arithmetic included.
    /// Applied in the mapping block's own key order.
    /// </summary>
    public void ApplyMapping(PresetValues p, ReadOnlySpan<float> f)
    {
        foreach (var (field, target) in p.Mapping)
        {
            double v = f[field];
            switch (target)
            {
                case MapTarget.FeedbackZoom: Zoom += v * 0.0055; break;
                case MapTarget.FeedbackDecay: Decay = Math.Min(0.9995, Decay + v * 0.006); break;
                case MapTarget.WarpAmount: WarpAmount *= 0.45 + 1.15 * v; break;
                case MapTarget.WarpScale: WarpScale *= 0.7 + 0.6 * v; break;
                case MapTarget.FractalIterations: Iterations *= 0.62 + 0.66 * v; break;
                case MapTarget.FractalMorph: BeatMorph *= 0.6 + 0.9 * v; Cells *= 0.75 + 0.6 * v; break;
                case MapTarget.MotifOpacity: MotifOpacity *= 0.35 + 1.1 * v; break;
                case MapTarget.MotifGain: MotifGain *= 0.4 + 1.3 * v; break;
                case MapTarget.ParticleEmit: PEmit *= 0.25 + 1.5 * v; break;
                case MapTarget.Grain: Grain *= 0.4 + 1.6 * v; break;
                case MapTarget.Symmetry: Symmetry = Math.Max(Symmetry, v); break;
                case MapTarget.PaletteRotate: PalRot += v; break;
                case MapTarget.BloomGain: BloomGain *= 0.5 + 1.2 * v; break;
                case MapTarget.Exposure: Exposure *= 0.7 + 0.6 * v; break;
                case MapTarget.Rotate: Rotate *= 0.4 + 1.4 * v; break;
            }
        }
    }
}
