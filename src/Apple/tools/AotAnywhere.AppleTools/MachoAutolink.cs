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
    /// The linker options (each a list of arguments, e.g. <c>["-framework", "UIKit"]</c>) of the
    /// objects the link loads, in first-seen order, for the slice of <paramref name="arch"/>. Like
    /// ld64, only archive members that get loaded count: a member is loaded when it defines a
    /// symbol that is still undefined, repeated until nothing changes - so an unused Swift object
    /// deep in a runtime archive doesn't drag in the libraries it would need.
    /// </summary>
    public static List<string[]> Collect(IEnumerable<string> inputs, string arch) => Analyze(inputs, arch).Options;

    /// <summary>
    /// What the link's loaded objects ask for (<see cref="Collect"/>), what they still leave
    /// undefined, and which of those they only reference weakly (weak_import).
    /// </summary>
    public sealed record Analysis(List<string[]> Options, HashSet<string> Undefined, HashSet<string> WeakOnly);

    /// <inheritdoc cref="Collect"/>
    public static Analysis Analyze(IEnumerable<string> inputs, string arch)
    {
        int cpu = arch switch
        {
            "arm64" => CPU_TYPE_ARM64,
            "x86_64" => CPU_TYPE_X86_64,
            _ => throw new AppleToolException($"AotAnywhere: unsupported architecture '{arch}'."),
        };
        var objects = new List<ObjectFile>();
        foreach (var path in inputs)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (IOException) { continue; }
            objects.AddRange(FromFile(data, cpu, inArchive: false));
        }

        var loaded = objects.Where(o => !o.InArchive).ToList();
        var pending = objects.Where(o => o.InArchive).ToList();
        var defined = new HashSet<string>(loaded.SelectMany(o => o.Defined), StringComparer.Ordinal);
        var undefined = new HashSet<string>(loaded.SelectMany(o => o.Undefined).Where(u => !defined.Contains(u)), StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < pending.Count; i++)
            {
                var member = pending[i];
                if (!member.Defined.Any(undefined.Contains))
                    continue;
                loaded.Add(member);
                pending.RemoveAt(i--);
                defined.UnionWith(member.Defined);
                undefined.ExceptWith(member.Defined);
                foreach (var u in member.Undefined)
                    if (!defined.Contains(u))
                        undefined.Add(u);
                changed = true;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string[]>();
        foreach (var option in loaded.SelectMany(o => o.LinkerOptions))
            if (seen.Add(string.Join('\0', option)))
                result.Add(option);
        var strong = new HashSet<string>(loaded.SelectMany(o => o.Undefined.Where(u => !o.WeakUndefined.Contains(u))), StringComparer.Ordinal);
        var weakOnly = new HashSet<string>(undefined.Where(u => !strong.Contains(u)), StringComparer.Ordinal);
        return new Analysis(result, undefined, weakOnly);
    }

    sealed record ObjectFile(bool InArchive, List<string[]> LinkerOptions, List<string> Defined, List<string> Undefined, HashSet<string> WeakUndefined);

    static List<ObjectFile> FromFile(byte[] data, int cpu, bool inArchive)
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
                return FromFile(data.AsSpan(offset, size).ToArray(), cpu, inArchive);
            }
            return [];
        }
        if (span.StartsWith("!<arch>\n"u8))
            return FromArchive(data, cpu);
        return FromObject(data, cpu, inArchive) is { } o ? [o] : [];
    }

    static List<ObjectFile> FromArchive(byte[] data, int cpu)
    {
        var result = new List<ObjectFile>();
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
            if (length > 0 && body + length <= data.Length && FromObject(data.AsSpan(body, length).ToArray(), cpu, inArchive: true) is { } o)
                result.Add(o);
            pos += 60 + (int)size;
            pos += pos & 1; // members are 2-byte aligned
        }
        return result;
    }

    const uint LC_SYMTAB = 0x2;
    const ushort N_WEAK_REF = 0x40;
    const byte N_STAB = 0xe0, N_PEXT = 0x10, N_TYPE = 0x0e, N_EXT = 0x01, N_UNDF = 0x0, N_SECT = 0xe, N_ABS = 0x2;

    // A relocatable object (MH_OBJECT): its LC_LINKER_OPTIONs and external symbols. Dylibs and
    // anything else return null - their imports resolve at load time, not in this link.
    static ObjectFile? FromObject(byte[] data, int cpu, bool inArchive)
    {
        var span = data.AsSpan();
        if (span.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(span) != MH_MAGIC_64 || BinaryPrimitives.ReadInt32LittleEndian(span[4..]) != cpu
            || BinaryPrimitives.ReadUInt32LittleEndian(span[12..]) != 1 /* MH_OBJECT */)
            return null;
        var options = new List<string[]>();
        var defined = new List<string>();
        var undefined = new List<string>();
        var weakUndefined = new HashSet<string>(StringComparer.Ordinal);
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
                    options.Add([.. strings]);
            }
            else if (cmd == LC_SYMTAB && cmdsize >= 24)
            {
                int symoff = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 8)..]);
                int nsyms = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 12)..]);
                int stroff = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 16)..]);
                for (int k = 0; k < nsyms && symoff + (k + 1) * 16 <= span.Length; k++)
                {
                    var nl = span.Slice(symoff + k * 16, 16);
                    byte type = nl[4];
                    if ((type & N_STAB) != 0 || (type & N_EXT) == 0)
                        continue;
                    int strx = (int)BinaryPrimitives.ReadUInt32LittleEndian(nl);
                    if (stroff + strx >= span.Length)
                        continue;
                    var rest = span[(stroff + strx)..];
                    int z = rest.IndexOf((byte)0);
                    string name = Encoding.UTF8.GetString(z < 0 ? rest : rest[..z]);
                    int kind = type & N_TYPE;
                    if (kind == N_UNDF && BinaryPrimitives.ReadUInt64LittleEndian(nl[8..]) == 0)
                    {
                        undefined.Add(name);
                        if ((BinaryPrimitives.ReadUInt16LittleEndian(nl[6..]) & N_WEAK_REF) != 0)
                            weakUndefined.Add(name);
                    }
                    // Private externs (hidden symbols) aren't in an archive's table of contents,
                    // so they never cause a member to load.
                    else if (kind is N_SECT or N_ABS && (type & N_PEXT) == 0)
                        defined.Add(name);
                }
            }
            pos += cmdsize;
        }
        return new ObjectFile(inArchive, options, defined, undefined, weakUndefined);
    }

    /// <summary>
    /// The autolink options that resolve on the link's search paths, as zig arguments - ld64
    /// silently skips the ones it can't find (a framework the SDK doesn't have, a toolchain
    /// back-deployment library the deployment target doesn't need), and so does this.
    /// </summary>
    public static List<string> Resolve(IEnumerable<string[]> options, IReadOnlyList<string> libraryDirs, IReadOnlyList<string> frameworkDirs, IReadOnlyCollection<string> existingArgs)
        => Resolve(options, libraryDirs, frameworkDirs, existingArgs, out _);

    /// <inheritdoc cref="Resolve(IEnumerable{string[]}, IReadOnlyList{string}, IReadOnlyList{string}, IReadOnlyCollection{string})"/>
    /// <param name="missingLibraries">The <c>-l</c> libraries no search path has.</param>
    /// <param name="undefined">
    /// When given, the symbols the loaded objects leave undefined: like ld64, which treats
    /// autolinked libraries as dead-strippable, a library is then only added if its stub
    /// supplies one of them that no library already on the link line (or added before it) does.
    /// </param>
    public static List<string> Resolve(IEnumerable<string[]> options, IReadOnlyList<string> libraryDirs, IReadOnlyList<string> frameworkDirs, IReadOnlyCollection<string> existingArgs, out List<string> missingLibraries,
        IReadOnlySet<string>? undefined = null)
    {
        var result = new List<string>();
        missingLibraries = [];
        // ld64's rule (see StubLink): an autolinked library is used - and so linked - when it
        // implements an undefined symbol, or re-exports one that nothing on the line provides.
        HashSet<string>? undefinedSet = undefined is null ? null : new(undefined, StringComparer.Ordinal);
        var provided = new HashSet<string>(StringComparer.Ordinal);
        if (undefinedSet is not null)
        {
            var args = existingArgs.ToList();
            for (int i = 0; i < args.Count; i++)
            {
                string? stub = args[i] switch
                {
                    "-framework" or "-weak_framework" when i + 1 < args.Count => FrameworkStub(frameworkDirs, args[++i]),
                    var a when a.StartsWith("-l", StringComparison.Ordinal) && a.Length > 2 => LibraryFile(libraryDirs, a[2..]),
                    var a when a.EndsWith(".tbd", StringComparison.Ordinal) => a,
                    _ => null,
                };
                if (stub is not null && stub.EndsWith(".tbd", StringComparison.Ordinal) && File.Exists(stub))
                    provided.UnionWith(StubExports(stub).All);
            }
        }

        // Non-stub libraries (static archives) are always needed.
        bool Needed(string? file)
        {
            if (undefinedSet is null || file is null || !file.EndsWith(".tbd", StringComparison.Ordinal))
                return true;
            var (all, own) = StubExports(file);
            bool needed = own.Overlaps(undefinedSet) || all.Any(s => undefinedSet.Contains(s) && !provided.Contains(s));
            if (needed)
                provided.UnionWith(all);
            return needed;
        }

        foreach (var option in options)
        {
            if (option is [var l] && l.StartsWith("-l", StringComparison.Ordinal))
            {
                string lib = l[2..];
                if (existingArgs.Contains(l) || result.Contains(l))
                    continue;
                if (LibraryFile(libraryDirs, lib) is { } file)
                {
                    if (Needed(file))
                        result.Add(l);
                }
                else if (!missingLibraries.Contains(lib))
                    missingLibraries.Add(lib);
            }
            else if (option is [var kind, var fw] && kind is "-framework" or "-weak_framework")
            {
                if (frameworkDirs.Any(d => Directory.Exists(Path.Combine(d, fw + ".framework"))) && !AlreadyHasFramework(existingArgs, result, fw)
                    && Needed(FrameworkStub(frameworkDirs, fw)))
                {
                    result.Add(option[0]);
                    result.Add(fw);
                }
            }
        }
        return result;
    }

    static string? LibraryFile(IReadOnlyList<string> dirs, string lib) =>
        dirs.SelectMany(d => new[] { ".tbd", ".dylib", ".a" }.Select(ext => Path.Combine(d, "lib" + lib + ext))).FirstOrDefault(File.Exists);

    static string? FrameworkStub(IReadOnlyList<string> dirs, string fw) =>
        dirs.Select(d => Path.Combine(d, fw + ".framework", fw + ".tbd")).FirstOrDefault(File.Exists);

    static readonly Dictionary<string, (HashSet<string> All, HashSet<string> Own)> StubCache = new(StringComparer.Ordinal);

    // A stub's exports, and those it implements itself (not under reexports:).
    static (HashSet<string> All, HashSet<string> Own) StubExports(string path)
    {
        if (!StubCache.TryGetValue(path, out var exports))
        {
            var libs = Tbd.Parse(File.ReadAllText(path));
            var all = libs.SelectMany(l => l.Exports.Keys).ToHashSet(StringComparer.Ordinal);
            var own = libs.SelectMany(l => l.Exports.Keys.Where(k => !l.ReexportedSymbols.Contains(k))).ToHashSet(StringComparer.Ordinal);
            StubCache[path] = exports = (all, own);
        }
        return exports;
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
