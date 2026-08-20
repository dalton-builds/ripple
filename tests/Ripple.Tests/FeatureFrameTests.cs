using System.Numerics;
using System.Runtime.InteropServices;
using Ripple.Render;
using Silk.NET.OpenGL;
using Xunit;

namespace Ripple.Tests;

/// <summary>
/// The two Phase 1 tests from plan §14. Both guard CONTRACTS.md §1, which is
/// the contract every later phase is built on: if the UBO layout is wrong, the
/// shaders read the wrong fields and the failure shows up as "the visuals look
/// a bit off" rather than as anything diagnosable.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class FeatureFrameTests(GlFixture gl)
{
    private readonly GlFixture _fx = gl;

    [Fact]
    public void Size_is_224_bytes()
    {
        Assert.Equal(224, FeatureFrame.ByteSize);
        Assert.Equal(224, Marshal.SizeOf<FeatureFrame>());
        Assert.Equal(56, FeatureFrame.FloatCount);
        // 14 vec4. If this drifts, the struct grew a member that std140 will
        // pad differently from the GLSL block.
        Assert.Equal(14, Marshal.SizeOf<FeatureFrame>() / 16);
    }

    [Fact]
    public void Field_names_cover_every_float()
        => Assert.Equal(FeatureFrame.FloatCount, FF.Names.Length);

    /// <summary>
    /// Upload a known frame, then have a fragment shader that includes the REAL
    /// features.glsl write each of the 56 floats back out, and compare field by
    /// field.
    ///
    /// This is the test that tells a Mode A parity failure apart from a shader
    /// bug: if the UBO round-trips exactly and the images still differ, the
    /// difference is downstream of the contract.
    /// </summary>
    [Fact]
    public void Std140_round_trips_every_field()
    {
        var g = _fx.Gl;

        // Distinct, exactly representable values, so a mismatch identifies the
        // offending index rather than just failing. i/256 is exact in binary32.
        var frame = default(FeatureFrame);
        var src = frame.AsSpan();
        for (int i = 0; i < FeatureFrame.FloatCount; i++) src[i] = 1f + i / 256f;

        uint ubo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.UniformBuffer, ubo);
        g.BufferData<float>(BufferTargetARB.UniformBuffer,
            (nuint)FeatureFrame.ByteSize, src, BufferUsageARB.DynamicDraw);
        g.BindBufferBase(BufferTargetARB.UniformBuffer, 0, ubo);

        // 56x1 RGBA32F: one texel per float, red channel carries the value.
        // Float target, not RGBA8 — the whole point is an exact comparison.
        uint tex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, tex);
        unsafe
        {
            g.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f,
                (uint)FeatureFrame.FloatCount, 1, 0, PixelFormat.Rgba, PixelType.Float, null);
        }
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

        uint fbo = g.GenFramebuffer();
        g.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        g.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, tex, 0);
        Assert.Equal(GLEnum.FramebufferComplete,
            g.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

        uint prog = ShaderProgram.Link(g, "std140-probe",
            ShaderLoader.Preamble + _fx.Shaders.Resolve("fullscreen.vert"),
            ShaderLoader.Preamble + _fx.Shaders.ResolveText("probe.frag", ProbeFrag));

        uint vao = g.GenVertexArray();
        g.BindVertexArray(vao);
        g.UseProgram(prog);
        g.Viewport(0, 0, (uint)FeatureFrame.FloatCount, 1);
        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.Blend);
        g.DrawArrays(PrimitiveType.Triangles, 0, 3);

        var read = new float[FeatureFrame.FloatCount * 4];
        g.ReadPixels<float>(0, 0, (uint)FeatureFrame.FloatCount, 1,
            PixelFormat.Rgba, PixelType.Float, read);

        var failures = new List<string>();
        for (int i = 0; i < FeatureFrame.FloatCount; i++)
        {
            float got = read[i * 4], want = src[i];
            if (got != want)
                failures.Add($"  [{i,2}] {FF.Names[i],-13} want {want:R}  got {got:R}");
        }

        g.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        g.DeleteFramebuffer(fbo);
        g.DeleteTexture(tex);
        g.DeleteProgram(prog);
        g.DeleteVertexArray(vao);
        g.DeleteBuffer(ubo);

        Assert.True(failures.Count == 0,
            $"std140 round-trip mismatched {failures.Count} of {FeatureFrame.FloatCount} fields:\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Reads the block through the same names the real shaders use, and in
    /// declaration order, so the 14 vec4s are laid out here exactly as
    /// features.glsl declares them. A reordering on either side fails.
    /// </summary>
    private const string ProbeFrag = """
        #include "common/features.glsl"

        out vec4 fragColor;

        void main() {
            int i = int(gl_FragCoord.x);
            vec4 blocks[14] = vec4[14](
                uTime, uLevel, uBands[0], uBands[1], uRhythm, uTempo,
                uChroma[0], uChroma[1], uChroma[2], uTonal, uSpace, uCtl,
                uRes, uReserved);
            vec4 b = blocks[i >> 2];
            int c = i & 3;
            float v = (c == 0) ? b.x : ((c == 1) ? b.y : ((c == 2) ? b.z : b.w));
            fragColor = vec4(v, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// AsSpan() is how the whole host reads and writes the frame, so an alias
    /// bug there would corrupt every field while both tests above still pass.
    /// </summary>
    [Fact]
    public void AsSpan_aliases_the_struct()
    {
        var f = default(FeatureFrame);
        f.AsSpan()[FF.Bpm] = 137.5f;
        Assert.Equal(137.5f, f.Tempo.X);

        f.Res = new Vector4(960, 540, 960f / 540f, 0.75f);
        Assert.Equal(960f, f.AsSpan()[FF.Width]);
        Assert.Equal(0.75f, f.AsSpan()[FF.RenderScale]);
    }
}
