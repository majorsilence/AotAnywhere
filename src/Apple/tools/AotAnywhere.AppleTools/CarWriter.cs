using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Writes CoreUI's compiled asset catalog (<c>Assets.car</c>): a <see cref="Bom"/> holding a
/// header, the key format (which attributes make up a rendition key, in order), the facets
/// (asset names and their identifying attributes) and the renditions (one bitmap per scale,
/// idiom, size ...), each a "CTSI" structured image. Format as implemented by the BSD-licensed
/// facebook/xcbuild (Libraries/libcar: car_format.h, Writer.cpp, Rendition.cpp).
/// </summary>
public sealed class CarWriter
{
    // Rendition attribute identifiers (car_format.h, plus appearance = 7 and localization = 13).
    public const ushort Element = 1, Part = 2, Appearance = 7, Dimension1 = 8, Dimension2 = 9, Scale = 12, Localization = 13,
        Idiom = 15, Subtype = 16, Identifier = 17;

    // The key format: every rendition key lists these attributes' values, in this order.
    static readonly ushort[] KeyFormat = [Appearance, Localization, Scale, Idiom, Subtype, Dimension2, Dimension1, Identifier, Element, Part];

    /// <summary>
    /// A rendition: a bitmap (<paramref name="Image"/>), a multisize image set listing an icon's sizes
    /// (<paramref name="Sizes"/>), a color (<paramref name="Color"/>: sRGB red, green, blue, alpha), or
    /// raw data stored as-is (<paramref name="Raw"/> with its four-character <paramref name="RawFormat"/>:
    /// "PDF " and "SVG " vector images, "DATA" data assets with their <paramref name="Uti"/>).
    /// </summary>
    public sealed record Rendition(Dictionary<ushort, ushort> Attributes, string FileName, Png? Image, double ScaleFactor,
        IReadOnlyList<(int Width, int Height, int Index)>? Sizes = null,
        double[]? Color = null, byte[]? Raw = null, string? RawFormat = null, string? Uti = null, uint Flags = 0);

    // UIKit's appearance names, by the Appearance attribute value renditions carry.
    static readonly (string Name, ushort Value)[] Appearances = [("UIAppearanceAny", 0), ("UIAppearanceDark", 1)];
    public const ushort AppearanceDark = 1;

    readonly List<(string Name, Dictionary<ushort, ushort> Attributes)> _facets = [];
    readonly List<Rendition> _renditions = [];

    public void AddFacet(string name, Dictionary<ushort, ushort> attributes) => _facets.Add((name, attributes));

    public void AddRendition(Rendition rendition) => _renditions.Add(rendition);

    public byte[] Write()
    {
        var bom = new Bom();

        var header = new byte[436];
        "RATC"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x131);  // ui_version
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 17);     // storage_version
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)_renditions.Count);
        Encoding.ASCII.GetBytes("AotAnywhere asset catalog compiler\n").CopyTo(header, 20);
        Encoding.ASCII.GetBytes("version 1.0").CopyTo(header, 148);
        // uuid (404..419) left zero so output is reproducible; associated checksum 0.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(424), 2);  // schema_version
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(428), 1);  // color_space_id
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(432), 2);  // key_semantics
        bom.AddVariable("CARHEADER", header);

        var keyFormat = new byte[12 + 4 * KeyFormat.Length];
        "tmfk"u8.CopyTo(keyFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(keyFormat.AsSpan(8), (uint)KeyFormat.Length);
        for (int i = 0; i < KeyFormat.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(keyFormat.AsSpan(12 + 4 * i), KeyFormat[i]);
        bom.AddVariable("KEYFORMAT", keyFormat);

        bom.AddTree("FACETKEYS", _facets.Select(f => (Encoding.UTF8.GetBytes(f.Name), FacetValue(f.Attributes))));
        bom.AddTree("RENDITIONS", _renditions.Select(r => (RenditionKey(r.Attributes), RenditionValue(r))));
        if (_renditions.Any(r => r.Attributes.GetValueOrDefault(Appearance) != 0))
            bom.AddTree("APPEARANCEKEYS", Appearances.Select(a =>
            {
                var v = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(v, a.Value);
                return (Encoding.ASCII.GetBytes(a.Name), v);
            }));
        return bom.Write();
    }

    static byte[] FacetValue(Dictionary<ushort, ushort> attributes)
    {
        var sorted = attributes.OrderBy(a => a.Key).ToList();
        var value = new byte[6 + 4 * sorted.Count];
        // hot spot (x, y) = 0
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(4), (ushort)sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(6 + 4 * i), sorted[i].Key);
            BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(8 + 4 * i), sorted[i].Value);
        }
        return value;
    }

    static byte[] RenditionKey(Dictionary<ushort, ushort> attributes)
    {
        foreach (var key in attributes.Keys)
            if (Array.IndexOf(KeyFormat, key) < 0)
                throw new ArgumentException($"attribute {key} is not in the key format");
        var key2 = new byte[2 * KeyFormat.Length];
        for (int i = 0; i < KeyFormat.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(key2.AsSpan(2 * i), attributes.GetValueOrDefault(KeyFormat[i]));
        return key2;
    }

    // A "CTSI" structured image: header, info blocks (slices, metrics, composition, bitmap info,
    // bytes per row), then one "CELM" bitmap of premultiplied BGRA compressed with gzip.
    static byte[] RenditionValue(Rendition r)
    {
        if (r.Sizes is not null)
            return MultisizeValue(r);
        if (r.Color is not null)
            return ColorValue(r);
        if (r.Raw is not null)
            return RawValue(r);
        var image = r.Image!;
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(image.ToPremultipliedBgra());
            compressed = ms.ToArray();
        }
        compressed[9] = 0; // gzip OS field, for reproducible output

        var info = new MemoryStream();
        void Info(uint magic, params uint[] words)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, magic); info.Write(b);
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)(words.Length * 4)); info.Write(b);
            foreach (var w in words)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, w);
                info.Write(b);
            }
        }
        uint w = (uint)image.Width, h = (uint)image.Height;
        Info(1001, 1, 0, 0, w, h);                // slices: one, the whole image
        Info(1003, 1, 0, 0, 0, 0, w, h);          // metrics: no insets, image size
        Info(1004, 0, BitConverter.SingleToUInt32Bits(1f)); // composition: normal blend, opacity 1
        Info(1006, 1);                            // bitmap info: EXIF orientation up
        Info(1007, w * 4);                        // bytes per row

        var value = new byte[184];
        "ISTC"u8.CopyTo(value);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), 1);
        // flags: is_opaque (bit 3), bitmap_encoding 1 (bits 4-7)
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), (image.Opaque ? 1u << 3 : 0) | (1u << 4) | r.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(12), w);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), h);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(20), (uint)Math.Round(r.ScaleFactor * 100));
        "BGRA"u8.CopyTo(value.AsSpan(24));                                // pixel format 'ARGB'
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(28), 1);   // colour space
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(36), 12);  // layout: one part, scale
        var name = Encoding.UTF8.GetBytes(r.FileName);
        name.AsSpan(0, Math.Min(name.Length, 127)).CopyTo(value.AsSpan(40));
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(168), (uint)info.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(172), 1);  // bitmap count
        int payload = 16 + compressed.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(180), (uint)payload);

        var output = new MemoryStream();
        output.Write(value);
        info.WriteTo(output);
        var celm = new byte[16];
        "MLEC"u8.CopyTo(celm);
        BinaryPrimitives.WriteUInt32LittleEndian(celm.AsSpan(8), 2); // compression: zlib (gzip stream)
        BinaryPrimitives.WriteUInt32LittleEndian(celm.AsSpan(12), (uint)compressed.Length);
        output.Write(celm);
        output.Write(compressed);
        return output.ToArray();
    }

    // A multisize image set (layout 1010): the point sizes an icon has for one idiom, each with the
    // index (Dimension2) of its bitmap renditions. iOS's icon services read this to find app icons.
    // Payload layout as parsed by the MIT-licensed blacktop/ipsw (pkg/car/types.go, csiMultisizeImageSet).
    static byte[] MultisizeValue(Rendition r)
    {
        var payload = new byte[12 + 12 * r.Sizes!.Count];
        "SISM"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)r.Sizes.Count);
        for (int i = 0; i < r.Sizes.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12 + 12 * i), (uint)r.Sizes[i].Width);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16 + 12 * i), (uint)r.Sizes[i].Height);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20 + 12 * i), (uint)r.Sizes[i].Index);
        }

        var value = new byte[184 + 28];
        "ISTC"u8.CopyTo(value);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(36), 1010); // layout: multisize image set
        var name = Encoding.UTF8.GetBytes(r.FileName);
        name.AsSpan(0, Math.Min(name.Length, 127)).CopyTo(value.AsSpan(40));
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(168), 28); // info: composition + bitmap info
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(172), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(180), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(184), 1004);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(188), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(200), 1006);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(204), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(208), 1);
        return [.. value, .. payload];
    }

    // --- reading (tests and diagnostics) ---------------------------------------------------

    /// <summary>Facet names and their attributes.</summary>
    public static Dictionary<string, Dictionary<ushort, ushort>> ReadFacets(byte[] car)
    {
        var result = new Dictionary<string, Dictionary<ushort, ushort>>(StringComparer.Ordinal);
        foreach (var (key, value) in new Bom.Reader(car).Tree("FACETKEYS"))
        {
            var attributes = new Dictionary<ushort, ushort>();
            int count = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(4));
            for (int i = 0; i < count; i++)
                attributes[BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(6 + 4 * i))] = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(8 + 4 * i));
            result[Encoding.UTF8.GetString(key)] = attributes;
        }
        return result;
    }

    /// <summary>Each rendition's attributes (from its key), layout, pixel size and name.</summary>
    public static List<(Dictionary<ushort, ushort> Attributes, int Layout, int Width, int Height, string Name, byte[] Value)> ReadRenditions(byte[] car)
    {
        var bom = new Bom.Reader(car);
        var format = bom.Variable("KEYFORMAT");
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
        var ids = Enumerable.Range(0, n).Select(i => (ushort)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(12 + 4 * i))).ToArray();
        return bom.Tree("RENDITIONS").Select(e =>
        {
            var attributes = new Dictionary<ushort, ushort>();
            for (int i = 0; i < n; i++)
            {
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(e.Key.AsSpan(2 * i));
                if (v != 0)
                    attributes[ids[i]] = v;
            }
            var v2 = e.Value;
            return (attributes, (int)BinaryPrimitives.ReadUInt16LittleEndian(v2.AsSpan(36)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(v2.AsSpan(12)), (int)BinaryPrimitives.ReadUInt32LittleEndian(v2.AsSpan(16)),
                Encoding.UTF8.GetString(v2, 40, 128).TrimEnd('\0'), v2);
        }).ToList();
    }

    public static string Dump(byte[] car)
    {
        var sb = new StringBuilder();
        foreach (var (name, attributes) in ReadFacets(car))
            sb.Append("facet ").Append(name).Append(' ').AppendLine(string.Join(' ', attributes.Select(a => $"{a.Key}={a.Value}")));
        foreach (var r in ReadRenditions(car))
            sb.Append("rendition ").Append(r.Name).Append($" layout={r.Layout} {r.Width}x{r.Height} ")
                .AppendLine(string.Join(' ', r.Attributes.Select(a => $"{a.Key}={a.Value}")));
        return sb.ToString();
    }

    // The structured-image header every non-bitmap rendition starts with, then its info blocks.
    static MemoryStream Header(uint flags, uint scaleFactor, string pixelFormat, ushort layout, string name, List<(uint Magic, byte[] Data)> info, int payload)
    {
        var infoBytes = new MemoryStream();
        foreach (var (magic, data) in info)
        {
            infoBytes.Write(BitConverter.GetBytes(magic));
            infoBytes.Write(BitConverter.GetBytes((uint)data.Length));
            infoBytes.Write(data);
        }
        var value = new byte[184];
        "ISTC"u8.CopyTo(value);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(20), scaleFactor);
        if (pixelFormat.Length == 4)
            Encoding.ASCII.GetBytes(new string(pixelFormat.Reverse().ToArray())).CopyTo(value, 24); // a little-endian four-char code
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(36), layout);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 127)).CopyTo(value.AsSpan(40));
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(168), (uint)infoBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(172), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(180), (uint)payload);
        var output = new MemoryStream();
        output.Write(value);
        infoBytes.WriteTo(output);
        return output;
    }

    static byte[] Words(params uint[] words) => words.SelectMany(BitConverter.GetBytes).ToArray();

    // A color (layout 1009): "COLR", version 1, color space 1 (sRGB), the component count, then the
    // components as doubles (single-precision values widened, as Xcode writes them).
    static byte[] ColorValue(Rendition r)
    {
        var payload = new MemoryStream();
        payload.Write("RLOC"u8);
        payload.Write(Words(1, 1, (uint)r.Color!.Length));
        foreach (var c in r.Color)
            payload.Write(BitConverter.GetBytes((double)(float)c));
        var output = Header(0, 0, "", 1009, r.FileName, [(1004, Words(0, 0)), (1006, Words(1))], (int)payload.Length);
        payload.WriteTo(output);
        return output.ToArray();
    }

    // Raw data (vector images: layout 9, flagged vector; data assets: layout 1000 with their type
    // identifier), stored behind a "DWAR" header.
    static byte[] RawValue(Rendition r)
    {
        bool data = r.RawFormat == "DATA";
        var info = new List<(uint, byte[])> { (1004, Words(0, BitConverter.SingleToUInt32Bits(1f))) };
        if (data && r.Uti is not null)
        {
            var uti = Encoding.UTF8.GetBytes(r.Uti + "\0");
            info.Add((1005, [.. Words((uint)uti.Length, 0), .. uti]));
        }
        info.Add((1006, Words(1)));
        var payload = new byte[12 + r.Raw!.Length];
        "DWAR"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)r.Raw.Length);
        r.Raw.CopyTo(payload, 12);
        var output = Header(data ? 0u : 1u << 2, data ? 100u : 0u, r.RawFormat!, (ushort)(data ? 1000 : 9), data ? "CoreStructuredImage" : r.FileName, info, payload.Length);
        output.Write(payload);
        return output.ToArray();
    }
}
