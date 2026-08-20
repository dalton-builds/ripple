using Silk.NET.OpenGL;

namespace Ripple.Render;

/// <summary>
/// The 256×1 palette lookup texture, built from a preset's hex stops.
/// Mirror of buildPalette() in the harness, including the two details that
/// matter visually:
///
/// 1. The ramp WRAPS — stop N interpolates back into stop 0 — so palette
///    rotation is continuous. A hard seam reads as a flash, which the safety
///    limiter would then spend its budget fighting.
/// 2. Interpolation happens in a gamma-2.2 space, which keeps mid-blends from
///    going muddy the way a naive sRGB lerp does.
/// </summary>
public sealed class PaletteLut : IDisposable
{
    public const int Size = 256;

    private readonly GL _gl;
    private readonly byte[] _data = new byte[Size * 4];
    public uint Texture { get; }

    public PaletteLut(GL gl)
    {
        _gl = gl;
        Texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Texture);
        unsafe
        {
            fixed (byte* p = _data)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, Size, 1, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
    }

    public void Build(IReadOnlyList<string> stops)
    {
        int n = stops.Count > 0 ? stops.Count : 2;
        Span<double> r = stackalloc double[n];
        Span<double> g = stackalloc double[n];
        Span<double> b = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            var (rr, gg, bb) = HexToRgb(stops.Count > 0 ? stops[i] : (i == 0 ? "#000000" : "#ffffff"));
            r[i] = rr; g[i] = gg; b[i] = bb;
        }

        for (int i = 0; i < Size; i++)
        {
            double t = ((double)i / Size) * n;
            int a = (int)Math.Floor(t) % n;
            int c = (a + 1) % n;
            double k = t - Math.Floor(t);
            k = k * k * (3 - 2 * k);                        // smoothstep
            _data[i * 4 + 0] = Blend(r[a], r[c], k);
            _data[i * 4 + 1] = Blend(g[a], g[c], k);
            _data[i * 4 + 2] = Blend(b[a], b[c], k);
            _data[i * 4 + 3] = 255;
        }

        _gl.BindTexture(TextureTarget.Texture2D, Texture);
        unsafe
        {
            fixed (byte* p = _data)
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, Size, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
    }

    private static byte Blend(double c0, double c1, double k)
    {
        double a = Math.Pow(c0 / 255.0, 2.2);
        double b = Math.Pow(c1 / 255.0, 2.2);
        // JS Math.round is half-up, not banker's rounding — MidpointRounding
        // matters here because a palette stop landing exactly on .5 is common.
        return (byte)Math.Clamp(Math.Floor(255 * Math.Pow(a + (b - a) * k, 1 / 2.2) + 0.5), 0, 255);
    }

    /// <summary>Mirror of hex2rgb(): '#RGB' and '#RRGGBB', junk degrades to black.</summary>
    public static (int R, int G, int B) HexToRgb(string hex)
    {
        var h = hex.Replace("#", string.Empty).Trim();
        if (h.Length == 3) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
        if (!int.TryParse(h, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int n)) n = 0;
        return ((n >> 16) & 255, (n >> 8) & 255, n & 255);
    }

    public void Dispose() => _gl.DeleteTexture(Texture);
}
