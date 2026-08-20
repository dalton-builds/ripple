using Silk.NET.OpenGL;

namespace Ripple.Render;

/// <summary>
/// The ping-pong feedback buffers, the quarter-res bloom pair and the
/// full-res composite target. Mirror of allocTargets() in the harness.
///
/// Plan §8.1: the feedback pair is the whole reason this pipeline is cheap.
/// The image accumulates complexity over time instead of being recomputed, so
/// a long decay does more visual work than a high iteration count and costs
/// one texture sample.
/// </summary>
public sealed class RenderTargets(GL gl) : IDisposable
{
    private readonly GL _gl = gl;

    /// <summary>Output size in pixels.</summary>
    public int W { get; private set; }
    public int H { get; private set; }

    /// <summary>Feedback-buffer size — the output scaled by renderScale.</summary>
    public int Pw { get; private set; }
    public int Ph { get; private set; }

    /// <summary>Bloom size — a quarter of the feedback buffer per axis.</summary>
    public int Bw { get; private set; }
    public int Bh { get; private set; }

    public readonly uint[] Ping = new uint[2];
    public readonly uint[] PingFbo = new uint[2];
    public readonly uint[] Bloom = new uint[2];
    public readonly uint[] BloomFbo = new uint[2];
    public uint Final { get; private set; }
    public uint FinalFbo { get; private set; }

    /// <summary>FBO bound to the 1×1 top mip of <see cref="Final"/>, for the safety loop.</summary>
    public uint MipFbo { get; private set; }
    public int MipLevels { get; private set; } = 1;

    /// <summary>Index of the feedback buffer holding the PREVIOUS frame.</summary>
    public int Cur;

    public bool HasFloatTargets { get; private set; } = true;

    public void Allocate(int w, int h, double scale)
    {
        Release();

        W = w; H = h;
        Pw = Math.Max(64, (int)Math.Floor(w * scale + 0.5));
        Ph = Math.Max(64, (int)Math.Floor(h * scale + 0.5));
        Bw = Math.Max(16, Pw >> 2);
        Bh = Math.Max(16, Ph >> 2);
        Cur = 0;

        for (int i = 0; i < 2; i++)
        {
            Ping[i] = MakeTexture(Pw, Ph, InternalFormat.Rgba16f, PixelType.HalfFloat, false);
            PingFbo[i] = MakeFbo(Ping[i], 0);
            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            Bloom[i] = MakeTexture(Bw, Bh, InternalFormat.Rgba16f, PixelType.HalfFloat, false);
            BloomFbo[i] = MakeFbo(Bloom[i], 0);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        Final = MakeTexture(w, h, InternalFormat.Rgba8, PixelType.UnsignedByte, true);
        FinalFbo = MakeFbo(Final, 0);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        // Mean-luminance measurement rides the composite target's own mip
        // chain: generateMipmap, then read the 1×1 top level.
        MipLevels = 1 + (int)Math.Floor(Math.Log2(Math.Max(w, h)));
        _gl.BindTexture(TextureTarget.Texture2D, Final);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        MipFbo = MakeFbo(Final, MipLevels - 1);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Zero the feedback chain. The buffers ARE the history, so a
    /// deterministic run has to start from a known-black one.</summary>
    public void ClearFeedback()
    {
        _gl.ClearColor(0f, 0f, 0f, 1f);
        for (int i = 0; i < 2; i++)
        {
            Bind(PingFbo[i], Pw, Ph);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            Bind(BloomFbo[i], Bw, Bh);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }
        Bind(FinalFbo, W, H);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        Cur = 0;

        _gl.BindTexture(TextureTarget.Texture2D, Final);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Bind(uint fbo, int w, int h)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.Viewport(0, 0, (uint)w, (uint)h);
    }

    private uint MakeTexture(int w, int h, InternalFormat internalFormat, PixelType type, bool mips)
    {
        uint t = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, t);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, (uint)w, (uint)h, 0,
                PixelFormat.Rgba, type, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)(mips ? GLEnum.LinearMipmapLinear : GLEnum.Linear));
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        if (mips) _gl.GenerateMipmap(TextureTarget.Texture2D);
        return t;
    }

    private uint MakeFbo(uint tex, int level)
    {
        uint f = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, f);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, tex, level);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"framebuffer incomplete: {status}");
        return f;
    }

    private void Release()
    {
        for (int i = 0; i < 2; i++)
        {
            if (Ping[i] != 0) { _gl.DeleteTexture(Ping[i]); Ping[i] = 0; }
            if (PingFbo[i] != 0) { _gl.DeleteFramebuffer(PingFbo[i]); PingFbo[i] = 0; }
            if (Bloom[i] != 0) { _gl.DeleteTexture(Bloom[i]); Bloom[i] = 0; }
            if (BloomFbo[i] != 0) { _gl.DeleteFramebuffer(BloomFbo[i]); BloomFbo[i] = 0; }
        }
        if (FinalFbo != 0) { _gl.DeleteFramebuffer(FinalFbo); FinalFbo = 0; }
        if (MipFbo != 0) { _gl.DeleteFramebuffer(MipFbo); MipFbo = 0; }
        if (Final != 0) { _gl.DeleteTexture(Final); Final = 0; }
    }

    public void Dispose() => Release();
}
