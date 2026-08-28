using Silk.NET.OpenGL;

namespace Ripple.Render;

/// <summary>
/// The render pipeline of plan §8.1 — MilkDrop's architecture, not a modern
/// deferred renderer:
///
/// <code>
/// Pass A  WARP       sample the previous frame at a displaced UV, × decay
/// Pass B  PAINT      additively composite fractal + motifs + particles
///         BLOOM      bright pass then a separable blur at quarter res
/// Pass C  COMPOSITE  tonemap → bloom → grain → vignette → safety clamp
/// </code>
///
/// Pass order, uniform names and draw conditions are a mirror of renderFrame()
/// in the harness. The shaders are shared source; this file is the other half
/// of the contract, and it is where a parity failure is most likely to come
/// from — a uniform left unset here reads as a shader bug there.
/// </summary>
public sealed class FeedbackChain : IDisposable
{
    private readonly GL _gl;
    private readonly PaletteLut _palette;
    private readonly ParticleSystem _particles;
    private readonly uint _emptyVao;

    private ShaderProgram _warp = null!, _bright = null!, _blur = null!, _composite = null!;
    private ShaderProgram _rose = null!, _bolt = null!, _particleProg = null!;
    private readonly Dictionary<string, ShaderProgram> _fractals = new(StringComparer.Ordinal);

    /// <summary>
    /// Families named in the plan but out of Phase 0/1 scope. A preset asking
    /// for one falls back rather than failing: `space` requests kleinian, and a
    /// missing shader must not black the screen.
    /// </summary>
    private static readonly Dictionary<string, string> FractalStubs = new(StringComparer.Ordinal)
    {
        ["kleinian"] = "warp_fbm",
        ["ifs_flame"] = "warp_fbm",
        ["mandelbulb"] = "warp_fbm",
    };

    public RenderTargets Targets { get; }

    /// <summary>The family actually drawn last frame, after stub fallback.</summary>
    public string ResolvedFamily { get; private set; } = "warp_fbm";

    /// <summary>Families already warned about, so the log carries one line rather than 60/s.</summary>
    private readonly HashSet<string> _warnedFamilies = new(StringComparer.Ordinal);

    public FeedbackChain(GL gl, ShaderLoader loader, PaletteLut palette, ParticleSystem particles)
    {
        _gl = gl;
        _palette = palette;
        _particles = particles;
        Targets = new RenderTargets(gl);

        // Core profile requires SOME vertex array to be bound even for the
        // attributeless full-screen triangle.
        _emptyVao = gl.GenVertexArray();

        BuildPrograms(loader);
    }

    private void BuildPrograms(ShaderLoader loader)
    {
        _warp = loader.Program("warp", "fullscreen.vert", "passes/warp.frag");
        _bright = loader.Program("bright", "fullscreen.vert", "passes/bright.frag");
        _blur = loader.Program("blur", "fullscreen.vert", "passes/blur.frag");
        _composite = loader.Program("composite", "fullscreen.vert", "passes/composite.frag");
        _rose = loader.Program("rose", "fullscreen.vert", "motif/rose.frag");
        _bolt = loader.Program("bolt", "fullscreen.vert", "motif/bolt.frag");
        _particleProg = loader.Program("particles", "passes/particles.vert", "passes/particles.frag",
            p => ParticleSystem.BindAttribLocations(_gl, p));

        // One program per family, compiled up front. Plan §8.2 is explicit that
        // this must NOT be an uber-shader with a mode branch: register pressure
        // from the union of all families would slow down the cheap ones.
        foreach (var name in (string[])["warp_fbm", "julia_orbit", "apollonian", "mandel_zoom"])
            _fractals[name] = loader.Program(name, "fullscreen.vert", $"fractal/{name}.frag");
    }

    public void Resize(int w, int h, double renderScale) => Targets.Allocate(w, h, renderScale);

    private void Tri()
    {
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
    }

    private void BindTex(int unit, uint tex)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
    }

    /// <summary>The palette block every painting pass needs, plus the paint gain.</summary>
    private void PaletteUniforms(ShaderProgram p, DrawState d, HostState h)
    {
        BindTex(1, _palette.Texture);
        p.UI("uPalette", 1);
        p.U1("uPalRot", (float)h.PalRotSmooth);
        p.U1("uChromaBlend", (float)d.ChromaBlend);
        p.U1("uSpiral", (float)d.Spiral);
        p.U1("uSpiralSpin", (float)h.SpiralSpin);
        p.U1("uPaint", (float)d.Paint);
    }

    /// <summary>One frame. The Feature Frame UBO must already be uploaded.</summary>
    public void Render(DrawState d, HostState h)
    {
        var rt = Targets;
        int src = rt.Cur, dst = 1 - rt.Cur;

        // ---- PASS A: warp the feedback buffer -----------------------------
        rt.Bind(rt.PingFbo[dst], rt.Pw, rt.Ph);
        _gl.Disable(EnableCap.Blend);
        _warp.Use();
        BindTex(0, rt.Ping[src]);
        _warp.UI("uPrev", 0);
        _warp.U1("uDecay", (float)d.Decay);
        _warp.U1("uZoom", (float)d.Zoom);
        _warp.U1("uRotate", (float)d.Rotate);
        _warp.U1("uWarpAmount", (float)d.WarpAmount);
        _warp.U1("uWarpScale", (float)d.WarpScale);
        _warp.U1("uBassGain", (float)d.BassGain);
        _warp.U1("uSymmetry", (float)d.Symmetry);
        _warp.U1("uOnsetDecay", (float)d.OnsetDecay);
        _warp.U1("uCenterPull", (float)d.CenterPull);
        Tri();

        // ---- PASS B: additive paint ---------------------------------------
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);

        var family = d.Fractal;
        if (!_fractals.ContainsKey(family))
        {
            string sub = FractalStubs.GetValueOrDefault(family, "warp_fbm");

            // Warn ONCE per missing family. Per-frame this would be 60 lines a second, which
            // is not a warning — it is a way of making sure nobody reads the log. The set is
            // the memo: kleinian, ifs_flame and mandelbulb were never built, and a preset
            // asking for one must fall back visibly rather than silently or fatally.
            if (_warnedFamilies.Add(family))
                Console.Error.WriteLine($"[fractal] '{family}' is not built — "
                    + $"falling back to '{sub}' (preset asked for it; this is not fatal)");

            family = sub;
        }
        ResolvedFamily = family;

        var fp = _fractals[family];
        fp.Use();
        PaletteUniforms(fp, d, h);
        fp.U1("uWarp", (float)d.Warp);
        fp.U1("uScale", (float)d.Scale);
        fp.U1("uFlow", (float)d.Flow);
        fp.U1("uCells", (float)d.Cells);
        fp.U1("uRim", (float)d.Rim);
        fp.U1("uIrid", (float)d.Iridescence);
        fp.U1("uMask", (float)d.Mask);
        fp.U1("uOct", (float)d.Octaves);
        fp.U1("uRimWidth", (float)d.RimWidth);
        fp.U1("uIter", (float)d.Iterations);
        fp.U1("uEscape", (float)d.EscapeRadius);
        fp.U1("uOrbitR", (float)d.OrbitRadius);
        fp.U1("uBeatMorph", (float)d.BeatMorph);
        fp.U1("uTrapGain", (float)d.TrapGain);
        fp.U1("uFold", (float)d.Fold);
        fp.U1("uLine", (float)d.Line);
        fp.U1("uZoomPeriod", (float)d.ZoomPeriod);
        fp.U1("uZoomDepth", (float)d.ZoomDepth);
        Tri();

        if (h.Rose > 0.004 && d.RoseGain > 0.001)
        {
            _rose.Use();
            PaletteUniforms(_rose, d, h);
            _rose.U1("uGain", (float)(d.RoseGain * d.MotifOpacity * d.MotifGain));
            _rose.U1("uPetals", (float)d.RosePetals);
            _rose.U1("uBloom", (float)h.Rose);
            _rose.U1("uSeed", (float)h.RoseSeed);
            Tri();
        }

        if (h.Bolt > 0.005 && d.BoltGain > 0.001)
        {
            _bolt.Use();
            PaletteUniforms(_bolt, d, h);
            _bolt.U1("uGain", (float)(d.BoltGain * d.MotifOpacity * d.MotifGain));
            _bolt.U1("uForkDepth", (float)d.BoltFork);
            _bolt.U1("uImpulse", (float)h.Bolt);
            _bolt.U1("uSeed", (float)h.BoltSeed);
            Tri();
        }

        // JS Math.round is half-up; MidpointRounding.AwayFromZero would differ
        // on negatives, and banker's rounding differs on every exact .5.
        int pc = Math.Min(ParticleSystem.MaxParticles, (int)Math.Floor(d.PCount + 0.5));
        if (pc > 64)
        {
            _particleProg.Use();
            PaletteUniforms(_particleProg, d, h);
            _particleProg.U1("uCurlScale", (float)d.PCurl);
            _particleProg.U1("uLifetime", (float)d.PLife);
            _particleProg.U1("uSize", (float)d.PSize);
            _particleProg.U1("uEmit", (float)d.PEmit);
            _particleProg.U1("uRise", (float)d.PRise);
            _particles.Draw(pc);
        }
        _gl.Disable(EnableCap.Blend);

        // ---- bloom: bright pass + separable blur at quarter res ------------
        rt.Bind(rt.BloomFbo[0], rt.Bw, rt.Bh);
        _bright.Use();
        BindTex(0, rt.Ping[dst]);
        _bright.UI("uSrc", 0);
        _bright.U1("uThreshold", (float)d.BloomThreshold);
        _bright.U1("uKnee", (float)d.BloomKnee);
        Tri();

        if (d.BloomTaps >= 1)
        {
            _blur.Use();
            _blur.U1("uTaps", (float)d.BloomTaps);
            rt.Bind(rt.BloomFbo[1], rt.Bw, rt.Bh);
            BindTex(0, rt.Bloom[0]);
            _blur.UI("uSrc", 0);
            _blur.U2("uDir", 1f / rt.Bw, 0f);
            Tri();

            rt.Bind(rt.BloomFbo[0], rt.Bw, rt.Bh);
            BindTex(0, rt.Bloom[1]);
            _blur.U2("uDir", 0f, 1f / rt.Bh);
            Tri();
        }

        // ---- PASS C: composite + safety -----------------------------------
        rt.Bind(rt.FinalFbo, rt.W, rt.H);
        _composite.Use();
        BindTex(0, rt.Ping[dst]);
        _composite.UI("uScene", 0);
        BindTex(1, rt.Bloom[0]);
        _composite.UI("uBloom", 1);
        _composite.U1("uExposure", (float)d.Exposure);
        _composite.U1("uBloomGain", (float)d.BloomGain);
        _composite.U1("uGrain", (float)d.Grain);
        _composite.U1("uVignette", (float)d.Vignette);
        _composite.U1("uSaturate", (float)d.Saturate);
        _composite.U1("uIdle", (float)h.IdleDim);
        _composite.U1("uIntensity", (float)h.MasterIntensity);
        _composite.U1("uSafetyScale", (float)h.SafeScale);
        _composite.U1("uMaxBright", (float)d.MaxBright);
        _composite.U1("uHueRate", (float)h.HueRate);
        Tri();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        rt.Cur = dst;
    }

    /// <summary>Blit the composite target to the default framebuffer.</summary>
    public void Present(int windowWidth, int windowHeight)
    {
        var rt = Targets;
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, rt.FinalFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        _gl.BlitFramebuffer(0, 0, rt.W, rt.H, 0, 0, windowWidth, windowHeight,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Write the composite target to a PNG. Reads the composite FBO rather than the back
    /// buffer, for the same reason the parity dump does: nothing from the window compositor
    /// is in the path, and it works with the window hidden or occluded.
    ///
    /// Allocates a full readback buffer per call. That is fine and deliberate — this runs on
    /// a keypress, not per frame, so keeping a permanent buffer around would cost the
    /// footprint budget something for a feature used a few times a day.
    /// </summary>
    public void SavePng(GL gl, string path)
    {
        var rt = Targets;
        var pixels = new byte[rt.W * rt.H * 4];
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.FinalFbo);
        unsafe
        {
            fixed (byte* p = pixels)
                gl.ReadPixels(0, 0, (uint)rt.W, (uint)rt.H, PixelFormat.Rgba,
                              PixelType.UnsignedByte, p);
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        PngWriter.WriteRgbaBottomUp(path, pixels, rt.W, rt.H);
    }

    public void Dispose()
    {
        Targets.Dispose();
        _gl.DeleteVertexArray(_emptyVao);
    }
}
