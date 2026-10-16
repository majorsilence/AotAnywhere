using System.Buffers.Binary;
using System.IO.Compression;

namespace AotAnywhere.AppleTools;

/// <summary>
/// A PNG decoder (W3C PNG specification) to straight-alpha RGBA8, for the asset catalog compiler.
/// Covers every colour type and bit depth, plus tRNS transparency. Interlaced files are rejected.
/// Ancillary colour-management chunks are ignored: the pixels are taken as sRGB.
/// </summary>
public sealed class Png
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>RGBA, 4 bytes per pixel, rows top to bottom, alpha not premultiplied.</summary>
    public byte[] Rgba { get; }

    /// <summary>True when the source had no alpha channel or transparency (every pixel opaque).</summary>
    public bool Opaque { get; }

    Png(int width, int height, byte[] rgba, bool opaque)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
        Opaque = opaque;
    }

    /// <summary>An image from straight-alpha RGBA8 pixels.</summary>
    public static Png FromRgba(int width, int height, byte[] rgba)
    {
        bool opaque = true;
        for (int i = 3; i < rgba.Length; i += 4)
            opaque &= rgba[i] == 255;
        return new Png(width, height, rgba, opaque);
    }

    static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];

    public static (int Width, int Height) ReadSize(byte[] data)
    {
        if (data.Length < 24 || !data.AsSpan(0, 8).SequenceEqual(Signature))
            throw new AppleToolException("AotAnywhere: not a PNG file.");
        return ((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20)));
    }

    public static Png Decode(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature))
            throw new AppleToolException("AotAnywhere: not a PNG file.");

        int width = 0, height = 0, depth = 0, colorType = 0;
        byte[]? palette = null, trns = null;
        var idat = new MemoryStream();
        for (int pos = 8; pos + 12 <= data.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, length);
            pos += 12 + length;
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    depth = body[8];
                    colorType = body[9];
                    if (body[12] != 0)
                        throw new AppleToolException("AotAnywhere: interlaced PNGs are not supported; save the image non-interlaced.");
                    break;
                case "PLTE": palette = body.ToArray(); break;
                case "tRNS": trns = body.ToArray(); break;
                case "IDAT": idat.Write(body); break;
                case "IEND": pos = data.Length; break;
            }
        }
        if (width <= 0 || height <= 0)
            throw new AppleToolException("AotAnywhere: PNG has no image header.");

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new AppleToolException($"AotAnywhere: unknown PNG colour type {colorType}.") };
        int bitsPerPixel = channels * depth;
        int stride = (width * bitsPerPixel + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8);

        idat.Position = 0;
        var raw = new byte[(stride + 1) * height];
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        // Undo the per-row filters in place.
        var prior = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            var row = raw.AsSpan(y * (stride + 1) + 1, stride);
            byte filter = raw[y * (stride + 1)];
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? row[x - bpp] : 0, b = prior[x], c = x >= bpp ? prior[x - bpp] : 0;
                row[x] += filter switch
                {
                    0 => 0,
                    1 => (byte)a,
                    2 => (byte)b,
                    3 => (byte)((a + b) / 2),
                    4 => Paeth(a, b, c),
                    _ => throw new AppleToolException($"AotAnywhere: unknown PNG filter {filter}."),
                };
            }
            row.CopyTo(prior);
        }

        var rgba = new byte[width * height * 4];
        bool opaque = true;
        int max = (1 << Math.Min(depth, 8)) - 1;
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1) + 1;
            for (int x = 0; x < width; x++)
            {
                // Samples are scaled to 8 bits; 16-bit samples keep their high byte.
                int Sample(int channel)
                {
                    if (depth == 16)
                        return raw[row + (x * channels + channel) * 2];
                    if (depth == 8)
                        return raw[row + x * channels + channel];
                    int bit = (x * channels + channel) * depth;
                    return (raw[row + bit / 8] >> (8 - depth - bit % 8)) & max;
                }
                int Raw16(int channel) => depth == 16 ? BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(row + (x * channels + channel) * 2)) : Sample(channel);
                byte Scale(int v) => depth >= 8 ? (byte)v : (byte)(v * 255 / max);

                byte r, g, b, alpha = 255;
                switch (colorType)
                {
                    case 0:
                        r = g = b = Scale(Sample(0));
                        if (trns is { Length: >= 2 } && Raw16(0) == BinaryPrimitives.ReadUInt16BigEndian(trns))
                            alpha = 0;
                        break;
                    case 2:
                        r = Scale(Sample(0)); g = Scale(Sample(1)); b = Scale(Sample(2));
                        if (trns is { Length: >= 6 } && Raw16(0) == BinaryPrimitives.ReadUInt16BigEndian(trns)
                            && Raw16(1) == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2)) && Raw16(2) == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4)))
                            alpha = 0;
                        break;
                    case 3:
                        int i = Sample(0);
                        if (palette is null || i * 3 + 2 >= palette.Length)
                            throw new AppleToolException("AotAnywhere: PNG palette index out of range.");
                        r = palette[i * 3]; g = palette[i * 3 + 1]; b = palette[i * 3 + 2];
                        if (trns is not null && i < trns.Length)
                            alpha = trns[i];
                        break;
                    case 4:
                        r = g = b = (byte)Sample(0);
                        alpha = (byte)Sample(1);
                        break;
                    default:
                        r = (byte)Sample(0); g = (byte)Sample(1); b = (byte)Sample(2);
                        alpha = (byte)Sample(3);
                        break;
                }
                int o = (y * width + x) * 4;
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = alpha;
                opaque &= alpha == 255;
            }
        }
        return new Png(width, height, rgba, opaque);
    }

    static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    /// <summary>
    /// Scales by area averaging (each output pixel is the coverage-weighted mean of the source
    /// pixels under it, in premultiplied space), which suits the downscaling icons need.
    /// </summary>
    public Png Resize(int width, int height)
    {
        var acc = new double[width * height * 4];
        double sx = (double)Width / width, sy = (double)Height / height;
        for (int y = 0; y < height; y++)
        {
            double y0 = y * sy, y1 = y0 + sy;
            for (int x = 0; x < width; x++)
            {
                double x0 = x * sx, x1 = x0 + sx, r = 0, g = 0, b = 0, a = 0, total = 0;
                for (int py = (int)y0; py < Math.Min(Height, (int)Math.Ceiling(y1)); py++)
                {
                    double wy = Math.Min(y1, py + 1) - Math.Max(y0, py);
                    for (int px = (int)x0; px < Math.Min(Width, (int)Math.Ceiling(x1)); px++)
                    {
                        double w = wy * (Math.Min(x1, px + 1) - Math.Max(x0, px));
                        int i = (py * Width + px) * 4;
                        double alpha = Rgba[i + 3] * w;
                        r += Rgba[i] * alpha; g += Rgba[i + 1] * alpha; b += Rgba[i + 2] * alpha;
                        a += alpha;
                        total += w;
                    }
                }
                int o = (y * width + x) * 4;
                acc[o] = a > 0 ? r / a : 0; acc[o + 1] = a > 0 ? g / a : 0; acc[o + 2] = a > 0 ? b / a : 0;
                acc[o + 3] = total > 0 ? a / total : 0;
            }
        }
        var rgba = new byte[acc.Length];
        for (int i = 0; i < acc.Length; i++)
            rgba[i] = (byte)Math.Clamp(Math.Round(acc[i]), 0, 255);
        return new Png(width, height, rgba, Opaque);
    }

    /// <summary>An RGBA8 PNG (unfiltered rows, zlib-compressed).</summary>
    public byte[] Encode()
    {
        var raw = new byte[(Width * 4 + 1) * Height];
        for (int y = 0; y < Height; y++)
            Rgba.AsSpan(y * Width * 4, Width * 4).CopyTo(raw.AsSpan(y * (Width * 4 + 1) + 1));
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(raw);

        var output = new MemoryStream();
        output.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)Height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(output, "IHDR", ihdr);
        Chunk(output, "IDAT", z.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] body)
    {
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)body.Length);
        s.Write(u32);
        var typed = new byte[4 + body.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        body.CopyTo(typed, 4);
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(u32, Crc32(typed));
        s.Write(u32);
    }

    static uint Crc32(byte[] data)
    {
        uint crc = 0xffffffff;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xedb88320 & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    /// <summary>Premultiplied BGRA8, CoreUI's 'ARGB' bitmap layout.</summary>
    public byte[] ToPremultipliedBgra()
    {
        var o = new byte[Rgba.Length];
        for (int i = 0; i < Rgba.Length; i += 4)
        {
            int a = Rgba[i + 3];
            o[i] = (byte)((Rgba[i + 2] * a + 127) / 255);
            o[i + 1] = (byte)((Rgba[i + 1] * a + 127) / 255);
            o[i + 2] = (byte)((Rgba[i] * a + 127) / 255);
            o[i + 3] = (byte)a;
        }
        return o;
    }
}
