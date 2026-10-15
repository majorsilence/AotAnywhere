using System.Buffers.Binary;

namespace AotAnywhere.AppleTools.Xip;

/// <summary>
/// Decodes a single-stream .xz buffer whose filters are LZMA2 only (what Apple's pbzx chunks
/// contain) into an output of known size. Integrity checks are skipped: the xip's chunks are
/// covered by the archive signature, and a corrupt chunk fails LZMA decoding or the size check.
/// </summary>
public static class XzDecoder
{
    static ReadOnlySpan<byte> Magic => [0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00];
    const ulong FilterLzma2 = 0x21;

    public static bool IsXz(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    public static byte[] Decode(ReadOnlySpan<byte> data, int uncompressedSize)
    {
        if (data.Length < 12 + 12 || !IsXz(data))
            throw new XipFormatException("chunk is not an xz stream");
        int checkType = data[7] & 0x0f;
        int checkSize = checkType == 0 ? 0 : 4 << ((checkType - 1) / 3);

        var output = new byte[uncompressedSize];
        int outPos = 0;
        int at = 12;
        while (true)
        {
            byte headerSizeByte = data[at];
            if (headerSizeByte == 0x00)
                break;   // the index: no more blocks

            int blockStart = at;
            int headerSize = (headerSizeByte + 1) * 4;
            if (at + headerSize > data.Length)
                throw new XipFormatException("truncated xz block header");
            byte flags = data[at + 1];
            int numFilters = (flags & 0x03) + 1;
            int p = at + 2;
            if ((flags & 0x40) != 0)
                ReadVarint(data, ref p);   // compressed size
            if ((flags & 0x80) != 0)
                ReadVarint(data, ref p);   // uncompressed size
            for (int f = 0; f < numFilters; f++)
            {
                ulong id = ReadVarint(data, ref p);
                ulong propsSize = ReadVarint(data, ref p);
                if (id != FilterLzma2 || numFilters != 1)
                    throw new XipFormatException($"unsupported xz filter 0x{id:x} (only LZMA2 is supported)");
                p += (int)propsSize;
            }
            at += headerSize;

            outPos = Lzma2Decoder.Decode(data, ref at, output, outPos);

            // Block padding (to a multiple of four from the block start), then the check.
            at += (4 - (at - blockStart) % 4) % 4;
            at += checkSize;
        }

        if (outPos != uncompressedSize)
            throw new XipFormatException($"xz chunk decoded to {outPos} bytes, expected {uncompressedSize}");
        return output;
    }

    static ulong ReadVarint(ReadOnlySpan<byte> data, ref int at)
    {
        ulong v = 0;
        for (int shift = 0; shift < 63; shift += 7)
        {
            byte b = data[at++];
            v |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return v;
        }
        throw new XipFormatException("invalid xz varint");
    }
}

/// <summary>
/// Reads Apple's pbzx stream (the .xip "Content" file): "pbzx", a big-endian 64-bit chunk size,
/// then chunks of { uint64 uncompressed size, uint64 compressed size, data }, where data is an xz
/// stream, or stored as-is when the two sizes are equal. Chunks are independent, so they are
/// decoded in parallel and handed back in order.
/// </summary>
public static class Pbzx
{
    public static IEnumerable<byte[]> ReadChunks(Stream input, int parallelism)
    {
        Span<byte> header = stackalloc byte[12];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("pbzx"u8))
            throw new XipFormatException("the archive's Content is not a pbzx stream");

        var pending = new Queue<Task<byte[]>>();
        var sizes = new byte[16];
        while (true)
        {
            int got = input.ReadAtLeast(sizes, 16, throwOnEndOfStream: false);
            if (got == 0)
                break;
            if (got < 16)
                throw new XipFormatException("truncated pbzx chunk header");
            ulong uncompressed = BinaryPrimitives.ReadUInt64BigEndian(sizes);
            ulong compressed = BinaryPrimitives.ReadUInt64BigEndian(sizes.AsSpan(8));
            if (uncompressed > int.MaxValue || compressed > int.MaxValue)
                throw new XipFormatException("pbzx chunk too large");

            var data = new byte[compressed];
            input.ReadExactly(data);
            int size = (int)uncompressed;
            pending.Enqueue(compressed == uncompressed
                ? Task.FromResult(data)
                : Task.Run(() => XzDecoder.Decode(data, size)));

            while (pending.Count >= parallelism)
                yield return pending.Dequeue().GetAwaiter().GetResult();
        }
        while (pending.Count > 0)
            yield return pending.Dequeue().GetAwaiter().GetResult();
    }
}
