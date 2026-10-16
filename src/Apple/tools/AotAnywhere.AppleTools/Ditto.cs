using System.Buffers.Binary;

namespace AotAnywhere.AppleTools;

/// <summary>
/// The subset of macOS's <c>ditto</c> the iOS workload uses (embedding frameworks, cloning the app
/// bundle into an .ipa payload): <c>ditto [options] &lt;source&gt;... &lt;destination&gt;</c>, where a
/// source directory's contents are merged into the destination directory and a source file is
/// copied to the destination path (or into it, if it is a directory). Symbolic links are copied as
/// links where the host supports them, as ditto does. <c>--arch &lt;arch&gt;</c> (repeatable) thins
/// universal Mach-O files to those architectures while copying: one left gives a thin file.
/// </summary>
public static class Ditto
{
    // Options that only concern macOS file metadata (resource forks, extended attributes, ACLs,
    // quarantine, HFS compression) or verbosity, which an iOS bundle doesn't carry or need.
    static readonly HashSet<string> MetadataOptions = new(StringComparer.Ordinal)
    {
        "-V", "-v", "-X", "--rsrc", "-rsrc", "--norsrc", "-norsrc", "--extattr", "--noextattr", "--qtn", "--noqtn",
        "--acl", "--noacl", "--preserveHFSCompression", "--nopreserveHFSCompression", "--nocache",
    };

    public static void Run(IReadOnlyList<string> args)
    {
        var paths = new List<string>();
        var archs = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--arch" && i + 1 < args.Count)
            {
                archs.Add(args[++i]);
                continue;
            }
            if (MetadataOptions.Contains(a))
                continue;
            if (a.StartsWith('-'))
                throw new AppleToolException($"AotAnywhere: ditto option '{a}' is not supported off-Mac.");
            paths.Add(a);
        }
        if (paths.Count < 2)
            throw new AppleToolException("usage: ditto <source>... <destination>");

        string destination = paths[^1];
        foreach (var source in paths.Take(paths.Count - 1))
        {
            if (Directory.Exists(source) && !IsLink(source))
                CopyDirectory(source, destination, archs);
            else if (File.Exists(source) || IsLink(source))
                CopyEntry(source, Directory.Exists(destination) && !IsLink(destination)
                    ? Path.Combine(destination, Path.GetFileName(source.TrimEnd('/', '\\')))
                    : destination, archs);
            else
                throw new AppleToolException($"AotAnywhere: ditto: '{source}' does not exist.");
        }
    }

    static void CopyDirectory(string source, string destination, IReadOnlyCollection<string> archs)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry) && !IsLink(entry))
                CopyDirectory(entry, target, archs);
            else
                CopyEntry(entry, target, archs);
        }
    }

    static void CopyEntry(string source, string destination, IReadOnlyCollection<string> archs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var info = new FileInfo(source);
        if (info.LinkTarget is { } link && !OperatingSystem.IsWindows())
        {
            if (File.Exists(destination) || Directory.Exists(destination) || new FileInfo(destination).LinkTarget is not null)
                File.Delete(destination);
            File.CreateSymbolicLink(destination, link);
            return;
        }
        if (Directory.Exists(source))
        {
            // A link to a directory on a host without symlinks: copy what it points at.
            CopyDirectory(source, destination, archs);
            return;
        }
        if (archs.Count > 0 && Thin(File.ReadAllBytes(source), archs) is { } thinned)
            File.WriteAllBytes(destination, thinned);
        else
            File.Copy(source, destination, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    const uint FatMagic = 0xcafebabe; // big-endian fat header with 32-bit offsets

    /// <summary>
    /// A universal Mach-O reduced to <paramref name="keep"/> (ditto's arch names), or null when the data
    /// isn't universal or nothing would change.
    /// </summary>
    public static byte[]? Thin(byte[] data, IReadOnlyCollection<string> keep)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(data) != FatMagic)
            return null;
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        var slices = new List<(int Cpu, int Sub, int Offset, int Size, int Align)>();
        for (int i = 0; i < count; i++)
        {
            var e = data.AsSpan(8 + i * 20, 20);
            slices.Add((BinaryPrimitives.ReadInt32BigEndian(e), BinaryPrimitives.ReadInt32BigEndian(e[4..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(e[8..]), (int)BinaryPrimitives.ReadUInt32BigEndian(e[12..]), (int)BinaryPrimitives.ReadUInt32BigEndian(e[16..])));
        }
        var kept = slices.Where(sl => keep.Any(a => Matches(a, sl.Cpu, sl.Sub))).ToList();
        if (kept.Count == 0 || kept.Count == slices.Count)
            return null;
        if (kept.Count == 1)
            return data.AsSpan(kept[0].Offset, kept[0].Size).ToArray();

        var output = new MemoryStream();
        var header = new byte[8 + kept.Count * 20];
        BinaryPrimitives.WriteUInt32BigEndian(header, FatMagic);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)kept.Count);
        long offset = header.Length;
        var offsets = new List<long>();
        foreach (var sl in kept)
        {
            long alignment = 1L << sl.Align;
            offset = (offset + alignment - 1) / alignment * alignment;
            offsets.Add(offset);
            offset += sl.Size;
        }
        for (int i = 0; i < kept.Count; i++)
        {
            var e = header.AsSpan(8 + i * 20, 20);
            BinaryPrimitives.WriteInt32BigEndian(e, kept[i].Cpu);
            BinaryPrimitives.WriteInt32BigEndian(e[4..], kept[i].Sub);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(e[12..], (uint)kept[i].Size);
            BinaryPrimitives.WriteUInt32BigEndian(e[16..], (uint)kept[i].Align);
        }
        output.Write(header);
        for (int i = 0; i < kept.Count; i++)
        {
            output.Write(new byte[offsets[i] - output.Length]);
            output.Write(data, kept[i].Offset, kept[i].Size);
        }
        return output.ToArray();
    }

    // ditto/lipo arch names: arm64 is the arm64 cputype with subtype ALL (arm64e is subtype 2).
    static bool Matches(string arch, int cpu, int sub) => (arch, cpu, sub & 0x00ffffff) switch
    {
        ("arm64", 0x0100000c, 0) => true,
        ("arm64e", 0x0100000c, 2) => true,
        ("x86_64", 0x01000007, _) => true,
        ("i386", 7, _) => true,
        ("armv7", 12, 9) => true,
        _ => false,
    };

    static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;
}
