using System.Text;

namespace AotAnywhere.AppleTools.Xip;

/// <summary>
/// Reads the "odc" (POSIX.1, magic 070707) cpio archive that a .xip's pbzx stream decompresses to.
/// Entries are streamed: the callback decides per entry whether to read its data or skip it.
/// </summary>
public sealed class Cpio(Stream input)
{
    public enum Kind { File, Directory, Symlink, Other }

    public sealed record Entry(string Name, Kind Type, int Mode, long Size, long Device, long Inode, int Links);

    const int HeaderSize = 76;

    /// <summary>Calls <paramref name="onEntry"/> for each entry with a stream over its data (valid only during the call).</summary>
    public void ReadAll(Action<Entry, Stream> onEntry)
    {
        var header = new byte[HeaderSize];
        while (true)
        {
            input.ReadExactly(header);
            string magic = Encoding.ASCII.GetString(header, 0, 6);
            if (magic != "070707")
                throw new XipFormatException(magic is "070701" or "070702"
                    ? "the archive uses the 'newc' cpio format, which is not supported"
                    : "corrupt cpio header");

            long dev = Octal(header, 6, 6);
            long ino = Octal(header, 12, 6);
            int mode = (int)Octal(header, 18, 6);
            int nlink = (int)Octal(header, 36, 6);
            int nameSize = (int)Octal(header, 59, 6);
            long fileSize = Octal(header, 65, 11);

            var nameBytes = new byte[nameSize];
            input.ReadExactly(nameBytes);
            string name = Encoding.UTF8.GetString(nameBytes, 0, Math.Max(0, nameSize - 1));
            if (name == "TRAILER!!!")
                return;

            var kind = (mode & 0xf000) switch
            {
                0x8000 => Kind.File,
                0x4000 => Kind.Directory,
                0xa000 => Kind.Symlink,
                _ => Kind.Other,
            };

            var data = new BoundedStream(input, fileSize);
            onEntry(new Entry(name, kind, mode & 0xfff, fileSize, dev, ino, nlink), data);
            data.Drain();
        }
    }

    static long Octal(byte[] b, int at, int len)
    {
        long v = 0;
        for (int i = at; i < at + len; i++)
        {
            int d = b[i] - '0';
            if (d is < 0 or > 7)
                throw new XipFormatException("corrupt cpio header field");
            v = (v << 3) | (uint)d;
        }
        return v;
    }

    sealed class BoundedStream(Stream inner, long length) : Stream
    {
        long _remaining = length;
        static readonly byte[] Scratch = new byte[1 << 16];

        public void Drain()
        {
            while (_remaining > 0)
            {
                int n = inner.Read(Scratch, 0, (int)Math.Min(Scratch.Length, _remaining));
                if (n == 0)
                    throw new XipFormatException("truncated cpio entry");
                _remaining -= n;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
                return 0;
            if (buffer.Length > _remaining)
                buffer = buffer[..(int)_remaining];
            int n = inner.Read(buffer);
            if (n == 0)
                throw new XipFormatException("truncated cpio entry");
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

/// <summary>Presents a sequence of byte arrays as one forward-only stream.</summary>
public sealed class ChunkStream(IEnumerable<byte[]> chunks) : Stream
{
    readonly IEnumerator<byte[]> _chunks = chunks.GetEnumerator();
    byte[] _current = [];
    int _pos;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (_pos >= _current.Length)
        {
            if (!_chunks.MoveNext())
                return 0;
            _current = _chunks.Current;
            _pos = 0;
        }
        int n = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
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
