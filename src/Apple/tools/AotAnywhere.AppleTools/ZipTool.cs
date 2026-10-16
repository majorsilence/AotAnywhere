using System.Buffers.Binary;
using System.IO.Compression;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Zip archives (.ipa) that keep Unix file modes, which iOS needs: an app whose main executable
/// isn't marked executable doesn't launch. The workload makes the .ipa with `zip -r -y` on macOS and
/// Linux, and with System.IO.Compression on Windows, which records no modes at all.
/// <list type="bullet">
/// <item><see cref="Run"/>: the `zip -r -y [-9] [-q] archive paths...` the workload runs.</item>
/// <item><see cref="FixModes"/>: rewrites a Windows-made archive with modes inferred from the content.</item>
/// </list>
/// Entries are marked as made on Unix ("version made by" host 3), the only way unzip and iOS's
/// installer read the mode in the high 16 bits of the external attributes.
/// </summary>
public static class ZipTool
{
    const int DirMode = 0x41ed /* S_IFDIR | 0755 */, ExecMode = 0x81ed /* S_IFREG | 0755 */, FileMode = 0x81a4 /* S_IFREG | 0644 */, LinkMode = 0xa1ed /* S_IFLNK | 0755 */;

    sealed record Entry(string Name, int Mode, Func<byte[]>? Data, string? LinkTarget);

    // zip [-r] [-y] [-q] [-9|-0..] <archive> <path>...   (paths relative to the current directory)
    public static void Run(IReadOnlyList<string> args)
    {
        bool recurse = false, symlinks = false;
        var level = CompressionLevel.Optimal;
        var operands = new List<string>();
        foreach (var a in args)
        {
            if (a.Length > 1 && a[0] == '-' && operands.Count == 0)
            {
                foreach (char c in a[1..])
                    switch (c)
                    {
                        case 'r': recurse = true; break;
                        case 'y': symlinks = true; break;
                        case 'q': break;
                        case '9': level = CompressionLevel.SmallestSize; break;
                        case '0': level = CompressionLevel.NoCompression; break;
                        case >= '1' and <= '8': break;
                        default: throw new AppleToolException($"AotAnywhere: zip option '-{c}' is not supported.");
                    }
                continue;
            }
            operands.Add(a);
        }
        if (operands.Count < 2)
            throw new AppleToolException("usage: zip [-r] [-y] [-9] <archive> <path>...");

        var entries = new List<Entry>();
        foreach (var path in operands.Skip(1))
            Collect(path.TrimEnd('/', '\\'), recurse, symlinks, entries);
        // `zip` adds to an existing archive; the workload deletes it first, so a fresh one is right.
        Write(operands[0], entries, level);
    }

    static void Collect(string path, bool recurse, bool symlinks, List<Entry> entries)
    {
        string name = path.Replace('\\', '/');
        var info = new FileInfo(path);
        if (symlinks && info.LinkTarget is { } target)
        {
            entries.Add(new Entry(name, LinkMode, null, target));
            return;
        }
        if (Directory.Exists(path))
        {
            entries.Add(new Entry(name + "/", ModeOf(path, DirMode), null, null));
            if (recurse)
                foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
                    Collect(child, recurse, symlinks, entries);
            return;
        }
        if (!info.Exists)
            throw new AppleToolException($"AotAnywhere: zip: '{path}' does not exist.");
        entries.Add(new Entry(name, ModeOf(path, InferredMode(File.ReadAllBytes(path))), () => File.ReadAllBytes(path), null));
    }

    static int ModeOf(string path, int fallback)
    {
        if (OperatingSystem.IsWindows())
            return fallback;
        int type = fallback & 0xf000;
        return type | (int)File.GetUnixFileMode(path);
    }

    /// <summary>Executables (Mach-O, universal, #! scripts) 0755; everything else 0644.</summary>
    static int InferredMode(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (magic is 0xfeedfacf or 0xfeedface or 0xbebafeca /* cafebabe */ or 0xbfbafeca /* cafebabf */)
                return ExecMode;
        }
        return data.Length >= 2 && data[0] == '#' && data[1] == '!' ? ExecMode : FileMode;
    }

    /// <summary>Rewrites <paramref name="archive"/> with Unix modes inferred from each entry's content.</summary>
    public static void FixModes(string archive)
    {
        var entries = new List<Entry>();
        using (var zip = ZipFile.OpenRead(archive))
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/'))
                {
                    entries.Add(new Entry(e.FullName, DirMode, null, null));
                    continue;
                }
                using var s = e.Open();
                var ms = new MemoryStream();
                s.CopyTo(ms);
                var data = ms.ToArray();
                // Keep modes already recorded on Unix; infer the rest.
                int mode = (e.ExternalAttributes >> 16) & 0xffff;
                entries.Add(new Entry(e.FullName, mode != 0 ? mode : InferredMode(data), () => data, null));
            }
        Write(archive, entries, CompressionLevel.Optimal);
    }

    static void Write(string archive, List<Entry> entries, CompressionLevel level)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(archive));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        string temp = archive + ".tmp";
        using (var file = File.Create(temp))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var e in entries)
            {
                var entry = zip.CreateEntry(e.Name, e.Data is null ? CompressionLevel.NoCompression : level);
                entry.ExternalAttributes = e.Mode << 16;
                if (e.LinkTarget is not null)
                {
                    using var s = entry.Open();
                    s.Write(System.Text.Encoding.UTF8.GetBytes(e.LinkTarget));
                }
                else if (e.Data is not null)
                {
                    using var s = entry.Open();
                    s.Write(e.Data());
                }
            }
        }
        var bytes = File.ReadAllBytes(temp);
        MarkMadeOnUnix(bytes);
        File.WriteAllBytes(archive, bytes);
        File.Delete(temp);
    }

    // System.IO.Compression records the writing host (FAT on Windows); set host = Unix (3) in every
    // central directory header so readers honour the mode bits.
    static void MarkMadeOnUnix(byte[] zip)
    {
        int eocd = -1;
        for (int i = zip.Length - 22; i >= Math.Max(0, zip.Length - 22 - 65535); i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x06054b50)
            {
                eocd = i;
                break;
            }
        if (eocd < 0)
            throw new AppleToolException("AotAnywhere: zip: end of central directory not found.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(eocd + 10));
        int pos = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 16));
        for (int i = 0; i < count; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(pos)) != 0x02014b50)
                throw new AppleToolException("AotAnywhere: zip: bad central directory header.");
            zip[pos + 5] = 3;
            int n = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(pos + 28));
            int m = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(pos + 30));
            int k = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(pos + 32));
            pos += 46 + n + m + k;
        }
    }
}
