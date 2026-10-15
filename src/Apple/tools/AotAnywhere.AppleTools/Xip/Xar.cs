using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;

namespace AotAnywhere.AppleTools.Xip;

/// <summary>
/// Locates a file inside a xar archive (an Xcode .xip is a xar holding "Content" and "Metadata"):
/// a big-endian header { "xar!", header size, version, compressed TOC length, uncompressed TOC
/// length, checksum algorithm }, a zlib-compressed XML table of contents, then the heap the TOC's
/// data offsets are relative to.
/// </summary>
public static class Xar
{
    public sealed record Entry(long Offset, long Length, string Encoding);

    public static Entry Find(Stream archive, string name)
    {
        Span<byte> header = stackalloc byte[28];
        archive.Position = 0;
        archive.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0x78617221)   // "xar!"
            throw new XipFormatException("not a xar archive");
        int headerSize = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        long tocCompressed = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        if (tocCompressed <= 0 || tocCompressed > 256 * 1024 * 1024)
            throw new XipFormatException("implausible xar table of contents size");

        archive.Position = headerSize;
        var tocBytes = new byte[tocCompressed];
        archive.ReadExactly(tocBytes);
        var doc = new XmlDocument { XmlResolver = null };
        using (var z = new ZLibStream(new MemoryStream(tocBytes), CompressionMode.Decompress))
        using (var reader = XmlReader.Create(z, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        long heap = headerSize + tocCompressed;

        foreach (XmlElement file in doc.SelectNodes("/xar/toc/file")!)
        {
            if (file["name"]?.InnerText != name)
                continue;
            var data = file["data"] ?? throw new XipFormatException($"xar entry '{name}' has no data");
            long offset = long.Parse(data["offset"]!.InnerText);
            long length = long.Parse(data["length"]!.InnerText);
            string encoding = data["encoding"]?.GetAttribute("style") ?? "application/octet-stream";
            return new Entry(heap + offset, length, encoding);
        }
        throw new XipFormatException($"the archive has no '{name}' entry");
    }

    /// <summary>A read-only window onto [offset, offset + length) of the archive.</summary>
    public static Stream Open(Stream archive, Entry entry)
    {
        if (entry.Encoding != "application/octet-stream")
            throw new XipFormatException($"unsupported xar data encoding '{entry.Encoding}'");
        archive.Position = entry.Offset;
        return new WindowStream(archive, entry.Length);
    }

    sealed class WindowStream(Stream inner, long length) : Stream
    {
        long _remaining = length;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
                return 0;
            if (buffer.Length > _remaining)
                buffer = buffer[..(int)_remaining];
            int n = inner.Read(buffer);
            _remaining -= n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
