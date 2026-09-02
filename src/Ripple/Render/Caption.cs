using System.Drawing;
using System.Drawing.Drawing2D;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Silk.NET.OpenGL;

namespace Ripple.Render;

/// <summary>
/// §8.5's now-playing caption: fades in for 8 s on track change, bottom-left, small.
///
/// This is the single element the plan says converts a screensaver into a talking point, and
/// its whole job is to be READ FROM THREE FEET. That drives every decision here:
///
///   - a real system UI font at 15 px, hinted and antialiased, not a bitmap atlas and
///     emphatically not a psychedelic display face. §8.5 is explicit that a period-appropriate
///     display face defeats the purpose, because an unreadable caption conveys nothing;
///   - drawn with a soft dark shadow underneath, because the thing behind it is a
///     high-contrast animated fractal and white-on-anything is otherwise a coin flip;
///   - composited AFTER the safety limiter, as a straight alpha blend, so it is never part of
///     the luminance the limiter measures. A caption that made the picture flicker would be a
///     §12 violation caused by chrome.
///
/// The text is rasterized ONCE per track change with GDI+ and uploaded as a texture, so the
/// per-frame cost is one textured quad. Nothing here allocates per frame.
/// </summary>
public sealed class Caption : IDisposable
{
    /// <summary>§8.5: eight seconds.</summary>
    public const double HoldSeconds = 8.0;

    private const double FadeIn = 0.45, FadeOut = 1.2;

    /// <summary>Pixels from the bottom-left corner of the window.</summary>
    private const int MarginX = 28, MarginY = 26;

    private readonly GL _gl;
    private uint _tex;
    private int _texW, _texH;
    private double _age = double.PositiveInfinity;
    private string _text = "";

    public Caption(GL gl) => _gl = gl;

    /// <summary>Currently visible at all?</summary>
    public bool Visible => Alpha > 0.004;

    /// <summary>Current opacity, 0..1.</summary>
    public double Alpha
    {
        get
        {
            if (_tex == 0 || _age > HoldSeconds + FadeOut) return 0;
            if (_age < FadeIn) return _age / FadeIn;
            if (_age <= HoldSeconds) return 1;
            return 1 - (_age - HoldSeconds) / FadeOut;
        }
    }

    /// <summary>
    /// Set the caption and restart its 8 s life. Re-rasterizes; call on track change, not per
    /// frame. Setting the same text again still restarts the fade, which is what you want when
    /// the same song comes round again.
    /// </summary>
    public void Show(string text)
    {
        _text = text ?? "";
        _age = 0;
        if (_text.Length == 0) { _age = double.PositiveInfinity; return; }

        try { Rasterize(_text); }
        catch (Exception ex)
        {
            // A font that will not load must cost the caption, not the frame.
            Console.Error.WriteLine($"[caption] could not render: {ex.Message}");
            _age = double.PositiveInfinity;
        }
    }

    public void Hide() => _age = double.PositiveInfinity;

    public void Advance(double dt)
    {
        if (_age <= HoldSeconds + FadeOut) _age += dt;
    }

    // --------------------------------------------------------------- raster

    private void Rasterize(string text)
    {
        // Measure first so the texture is exactly the size of the line. A fixed-size texture
        // would either clip a long segue title or waste most of its area.
        using var probe = new Bitmap(1, 1, GdiPixelFormat.Format32bppArgb);
        using var pg = Graphics.FromImage(probe);
        pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var font = PickFont();
        var measured = pg.MeasureString(text, font);

        // Room for the shadow offset and a little bleed for the blur.
        int w = Math.Max(1, (int)Math.Ceiling(measured.Width) + 8);
        int h = Math.Max(1, (int)Math.Ceiling(measured.Height) + 6);

        using var bmp = new Bitmap(w, h, GdiPixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Shadow first, offset and semi-transparent. Three passes at decreasing alpha give a
        // cheap soft edge — enough to hold the text off a bright fractal without looking like
        // a drop shadow from a slide deck.
        foreach (var (dx, dy, a) in (( int, int, int )[])[(2, 2, 150), (1, 1, 110), (2, 1, 90)])
        {
            using var shadow = new SolidBrush(Color.FromArgb(a, 0, 0, 0));
            g.DrawString(text, font, shadow, 3 + dx, 2 + dy);
        }

        using var white = new SolidBrush(Color.FromArgb(255, 245, 245, 240));
        g.DrawString(text, font, white, 3, 2);

        Upload(bmp);
    }

    /// <summary>
    /// A legible UI face, in preference order. Not a period display face — see the class note.
    /// Falls back through the list because a machine without Segoe UI is unusual but possible,
    /// and the last entry is guaranteed present.
    /// </summary>
    private static Font PickFont()
    {
        foreach (var name in (string[])["Segoe UI Semibold", "Segoe UI", "Tahoma", "Arial"])
        {
            try
            {
                var f = new Font(name, 14.5f, FontStyle.Regular, GraphicsUnit.Pixel);
                // GDI+ silently substitutes when a family is missing; check we got it.
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
                    || name == "Arial") return f;
                f.Dispose();
            }
            catch (Exception) { /* try the next */ }
        }
        return new Font(FontFamily.GenericSansSerif, 14.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private unsafe void Upload(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
        try
        {
            if (_tex == 0) _tex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _tex);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                             (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                             (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                             (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                             (int)TextureWrapMode.ClampToEdge);

            // GDI+ hands back BGRA; ask GL for exactly that rather than swizzling on the CPU.
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                           (uint)bmp.Width, (uint)bmp.Height, 0,
                           PixelFormat_Bgra, PixelType.UnsignedByte,
                           (void*)data.Scan0);
            _texW = bmp.Width;
            _texH = bmp.Height;
        }
        finally
        {
            bmp.UnlockBits(data);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }
    }

    private const Silk.NET.OpenGL.PixelFormat PixelFormat_Bgra
        = Silk.NET.OpenGL.PixelFormat.Bgra;

    // ---------------------------------------------------------------- draw

    /// <summary>
    /// The caption's own shader, INLINE rather than in shaders/.
    ///
    /// Everything under shaders/ is extracted from harness/index.html and guarded by
    /// `extract-shaders.mjs --check`, because it is shared render contract. The caption is not
    /// — it is native-host chrome that the harness has no equivalent of, drawn after the
    /// composite and outside the parity dump. Putting it in the shared tree would add a file
    /// the harness could never own and would make the contract check a lie.
    ///
    /// A blit was tried first and is wrong: `glBlitFramebuffer` ignores blend state entirely,
    /// so the caption composited opaquely and its transparent background came through as a
    /// black box. Alpha blending needs a real draw.
    /// </summary>
    private const string VertexSource = """
        #version 430 core
        // Position and UV from gl_VertexID: a two-triangle quad with no buffers at all.
        uniform vec4 uRect;      // x, y, w, h in pixels
        uniform vec2 uViewport;
        out vec2 vUv;
        void main() {
            vec2 c = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 0.5;   // 0,0 1,0 0,1 1,1
            vUv = vec2(c.x, 1.0 - c.y);
            vec2 px = uRect.xy + c * uRect.zw;
            gl_Position = vec4(px / uViewport * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 430 core
        uniform sampler2D uText;
        uniform float uAlpha;
        in vec2 vUv;
        out vec4 fragColor;
        void main() {
            vec4 t = texture(uText, vUv);
            // Straight alpha in, premultiplied out — the blend below is (ONE,
            // ONE_MINUS_SRC_ALPHA), which is what keeps the antialiased edges from
            // developing a dark fringe over a bright fractal.
            float a = t.a * uAlpha;
            fragColor = vec4(t.rgb * a, a);
        }
        """;

    private uint _prog, _vao;

    private void EnsureProgram()
    {
        if (_prog != 0) return;
        _prog = ShaderProgram.Link(_gl, "caption", VertexSource, FragmentSource);
        _vao = _gl.GenVertexArray();   // core profile needs one bound even for attributeless
    }

    /// <summary>
    /// Draw the caption into whatever is currently bound, bottom-left, alpha-blended.
    ///
    /// Called AFTER the composite blit, so it is never part of the luminance §12's limiter
    /// measures — chrome must not be able to make the picture flicker.
    /// </summary>
    public void Draw(int windowW, int windowH)
    {
        if (_tex == 0 || !Visible || _texW == 0 || windowW <= 0 || windowH <= 0) return;

        try { EnsureProgram(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[caption] shader failed: {ex.Message}");
            _tex = 0;                 // stop trying; the picture is more important
            return;
        }

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        _gl.UseProgram(_prog);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        Uniform1i("uText", 0);
        Uniform1f("uAlpha", (float)Alpha);
        Uniform4f("uRect", MarginX, MarginY, _texW, _texH);
        Uniform2f("uViewport", windowW, windowH);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        _gl.BindVertexArray(0);

        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(0);
    }

    private void Uniform1i(string n, int v) => _gl.Uniform1(_gl.GetUniformLocation(_prog, n), v);
    private void Uniform1f(string n, float v) => _gl.Uniform1(_gl.GetUniformLocation(_prog, n), v);
    private void Uniform2f(string n, float a, float b)
        => _gl.Uniform2(_gl.GetUniformLocation(_prog, n), a, b);
    private void Uniform4f(string n, float a, float b, float c, float d)
        => _gl.Uniform4(_gl.GetUniformLocation(_prog, n), a, b, c, d);

    public void Dispose()
    {
        if (_tex != 0) { _gl.DeleteTexture(_tex); _tex = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_prog != 0) { _gl.DeleteProgram(_prog); _prog = 0; }
    }
}
