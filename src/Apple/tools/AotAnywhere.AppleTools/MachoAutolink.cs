using System.Buffers.Binary;
using System.Text;

namespace AotAnywhere.AppleTools;

/// <summary>
/// ld64's autolinking, which zig's linker doesn't do: objects compiled from Swift (or with
/// <c>-fmodules</c>) name the libraries and frameworks they need in <c>LC_LINKER_OPTION</c> load
/// commands (<c>-lswiftCore</c>, <c>-framework UIKit</c>, <c>-lswiftCompatibility56</c>), and ld64
/// adds whichever of them it can find. This collects them from the link's object and archive
/// inputs so they can be passed to zig explicitly.
/// </summary>
public static class MachoAutolink
{
    const uint MH_MAGIC_64 = 0xfeedfacf;
    const uint FAT_MAGIC = 0xcafebabe; // big-endian
    const uint LC_LINKER_OPTION = 0x2d;
    const int CPU_TYPE_ARM64 = 0x0100000c;
    const int CPU_TYPE_X86_64 = 0x01000007;

    /// <summary>
    /// The linker options (each a list of arguments, e.g. <c>["-framework", "UIKit"]</c>) in the
    /// given objects/archives, in first-seen order, for the slice of <paramref name="arch"/>.
    /// </summary>
    public static List<string[]> Collect(IEnumerable<string> inputs, string arch)
    {
        int cpu = arch switch
        {
            "arm64" => CPU_TYPE_ARM64,
            "x86_64" => CPU_TYPE_X86_64,
            _ => throw new AppleToolException($"AotAnywhere: unsupported architecture '{arch}'."),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string[]>();
        foreach (var path in inputs)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (IOException) { continue; }
            foreach (var option in FromFile(data, cpu))
                if (seen.Add(string.Join('\0', option)))
                    result.Add(option);
        }
        return result;
    }

    static IEnumerable<string[]> FromFile(byte[] data, int cpu)
    {
        var span = data.AsSpan();
        if (span.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(span) == FAT_MAGIC)
        {
            uint n = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
            for (int i = 0; i < n && 8 + (i + 1) * 20 <= span.Length; i++)
            {
                var entry = span.Slice(8 + i * 20, 20);
                if (BinaryPrimitives.ReadInt32BigEndian(entry) != cpu)
                    continue;
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
                return FromFile(data.AsSpan(offset, size).ToArray(), cpu);
            }
            return [];
        }
        if (span.StartsWith("!<arch>\n"u8))
            return FromArchive(data, cpu);
        return FromObject(data, cpu);
    }

    static List<string[]> FromArchive(byte[] data, int cpu)
    {
        var result = new List<string[]>();
        int pos = 8;
        while (pos + 60 <= data.Length)
        {
            string name = Encoding.ASCII.GetString(data, pos, 16).TrimEnd();
            if (!long.TryParse(Encoding.ASCII.GetString(data, pos + 48, 10).Trim(), out long size))
                break;
            int body = pos + 60, length = (int)size;
            // BSD long names: "#1/<n>" - the name is the first n bytes of the member.
            if (name.StartsWith("#1/", StringComparison.Ordinal) && int.TryParse(name[3..], out int nameLength))
            {
                body += nameLength;
                length -= nameLength;
            }
            if (length > 0 && body + length <= data.Length)
                result.AddRange(FromObject(data.AsSpan(body, length).ToArray(), cpu));
            pos += 60 + (int)size;
            pos += pos & 1; // members are 2-byte aligned
        }
        return result;
    }

    static List<string[]> FromObject(byte[] data, int cpu)
    {
        var result = new List<string[]>();
        var span = data.AsSpan();
        if (span.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(span) != MH_MAGIC_64 || BinaryPrimitives.ReadInt32LittleEndian(span[4..]) != cpu)
            return result;
        uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        int pos = 32;
        for (uint i = 0; i < ncmds && pos + 8 <= span.Length; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
            int cmdsize = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
            if (cmdsize < 8 || pos + cmdsize > span.Length)
                break;
            if (cmd == LC_LINKER_OPTION && cmdsize >= 12)
            {
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 8)..]);
                var strings = new List<string>();
                int s = pos + 12, end = pos + cmdsize;
                for (uint k = 0; k < count && s < end; k++)
                {
                    int z = span[s..end].IndexOf((byte)0);
                    if (z < 0)
                        break;
                    strings.Add(Encoding.UTF8.GetString(span.Slice(s, z)));
                    s += z + 1;
                }
                if (strings.Count > 0)
                    result.Add([.. strings]);
            }
            pos += cmdsize;
        }
        return result;
    }

    /// <summary>
    /// The autolink options that resolve on the link's search paths, as zig arguments - ld64
    /// silently skips the ones it can't find (a framework the SDK doesn't have, a toolchain
    /// back-deployment library the deployment target doesn't need), and so does this.
    /// </summary>
    public static List<string> Resolve(IEnumerable<string[]> options, IReadOnlyList<string> libraryDirs, IReadOnlyList<string> frameworkDirs, IReadOnlyCollection<string> existingArgs)
    {
        var result = new List<string>();
        foreach (var option in options)
        {
            if (option is [var l] && l.StartsWith("-l", StringComparison.Ordinal))
            {
                string lib = l[2..];
                if (existingArgs.Contains(l) || result.Contains(l))
                    continue;
                if (libraryDirs.Any(d => new[] { ".tbd", ".dylib", ".a" }.Any(ext => File.Exists(Path.Combine(d, "lib" + lib + ext)))))
                    result.Add(l);
            }
            else if (option is [var kind, var fw] && kind is "-framework" or "-weak_framework")
            {
                if (frameworkDirs.Any(d => Directory.Exists(Path.Combine(d, fw + ".framework"))) && !AlreadyHasFramework(existingArgs, result, fw))
                {
                    result.Add(option[0]);
                    result.Add(fw);
                }
            }
        }
        return result;
    }

    static bool AlreadyHasFramework(IReadOnlyCollection<string> existing, List<string> added, string fw)
    {
        foreach (var list in new[] { existing.ToList(), added })
            for (int i = 0; i + 1 < list.Count; i++)
                if (list[i] is "-framework" or "-weak_framework" && list[i + 1] == fw)
                    return true;
        return false;
    }
}
