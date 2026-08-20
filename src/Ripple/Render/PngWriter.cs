using System.Buffers.Binary;
using System.IO.Compression;

namespace Ripple.Render;

/// <summary>
/// A minimal PNG encoder for frame dumps.
///
/// Hand-rolled rather than pulled from a package: the only consumer is the
/// parity gate, the input is always 8-bit non-interlaced RGB, and
/// <see cref="ZLibStream"/> already provides the one non-trivial part. Adding
/// an imaging dependency to the render executable to write a debug artefact
/// would be a poor trade against the §11 footprint budget.
/// </summary>
public static class PngWriter
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Write an RGBA8 buffer as an opaque RGB8 PNG, flipping bottom-up to
    /// top-down. GL reads with the origin at bottom-left; PNG rows run the
    /// other way, and getting this backwards is a silent vertical mirror that
    /// SSIM would report as a total mismatch.
    /// </summary>
    public static void WriteRgbaBottomUp(string path, ReadOnlySpan<byte> rgba, int width, int height)
    {
        int stride = width * 4;
        if (rgba.Length < stride * height)
            throw new ArgumentException($"buffer holds {rgba.Length} bytes, need {stride * height}", nameof(rgba));

        // Raw PNG scanlines: one filter byte (0 = None) then RGB triples.
        var raw = new byte[height * (1 + width * 3)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;
            int src = (height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                raw[o++] = rgba[src + x * 4 + 0];
                raw[o++] = rgba[src + x * 4 + 1];
                raw[o++] = rgba[src + x * 4 + 2];
            }
        }

        byte[] compressed;
        using (var ms = new MemoryStream(raw.Length / 2 + 64))
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            compressed = ms.ToArray();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 16);
        fs.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 2;    // colour type 2 = truecolour RGB
        ihdr[10] = 0;   // deflate
        ihdr[11] = 0;   // adaptive filtering
        ihdr[12] = 0;   // no interlace
        WriteChunk(fs, "IHDR", ihdr);
        WriteChunk(fs, "IDAT", compressed);
        WriteChunk(fs, "IEND", []);
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);

        Span<byte> tag = stackalloc byte[4];
        for (int i = 0; i < 4; i++) tag[i] = (byte)type[i];
        s.Write(tag);
        s.Write(data);

        // The CRC covers the type tag then the data, in that order.
        uint crc = 0xFFFFFFFF;
        crc = Crc32(tag, crc);
        crc = Crc32(data, crc);
        crc ^= 0xFFFFFFFF;

        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint running)
    {
        uint c = running;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }
}
