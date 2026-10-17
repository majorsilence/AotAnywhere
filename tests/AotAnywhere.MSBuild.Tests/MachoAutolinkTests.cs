using System.Buffers.Binary;
using System.Text;
using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// zig ignores LC_LINKER_OPTION; MAUI's Swift interop library (a static, fat xcframework
// slice) only links once its autolink options (-lswiftCompatibility56, -lswiftMetal, ...)
// are passed explicitly.
public class MachoAutolinkTests
{
    const int Arm64 = 0x0100000c, X64 = 0x01000007;

    // A minimal MH_OBJECT: one LC_LINKER_OPTION per option, and an LC_SYMTAB with external
    // symbols - defined ("_x"), hidden ("!_x"), undefined ("-_x") or weakly undefined ("~_x").
    static byte[] Object(int cpu, string[] symbols, params string[][] options)
    {
        var cmds = new List<byte>();
        foreach (var option in options)
        {
            var strings = option.SelectMany(o => Encoding.UTF8.GetBytes(o).Append((byte)0)).ToList();
            while ((12 + strings.Count) % 8 != 0)
                strings.Add(0);
            cmds.AddRange(U32(0x2d));
            cmds.AddRange(U32((uint)(12 + strings.Count)));
            cmds.AddRange(U32((uint)option.Length));
            cmds.AddRange(strings);
        }
        int symtabCmd = cmds.Count;
        cmds.AddRange(new byte[24]); // LC_SYMTAB, filled in below
        int symoff = 32 + cmds.Count;
        var nlists = new List<byte>();
        var strtab = new List<byte> { 0 };
        foreach (var sym in symbols)
        {
            string name = sym.TrimStart('-', '~', '!');
            nlists.AddRange(U32((uint)strtab.Count));
            strtab.AddRange(Encoding.UTF8.GetBytes(name).Append((byte)0));
            bool undefined = sym[0] is '-' or '~';
            nlists.Add((byte)((undefined ? 0x0 : 0xe) | 0x1 | (sym[0] == '!' ? 0x10 : 0))); // N_UNDF/N_SECT | N_EXT [| N_PEXT]
            nlists.Add((byte)(undefined ? 0 : 1));
            nlists.AddRange([(byte)(sym[0] == '~' ? 0x40 : 0), 0]); // N_WEAK_REF
            nlists.AddRange(new byte[8]);
        }
        var symtab = new List<byte>();
        symtab.AddRange(U32(0x2));
        symtab.AddRange(U32(24));
        symtab.AddRange(U32((uint)symoff));
        symtab.AddRange(U32((uint)symbols.Length));
        symtab.AddRange(U32((uint)(symoff + nlists.Count)));
        symtab.AddRange(U32((uint)strtab.Count));
        for (int i = 0; i < 24; i++)
            cmds[symtabCmd + i] = symtab[i];

        var header = new List<byte>();
        header.AddRange(U32(0xfeedfacf));
        header.AddRange(U32((uint)cpu));
        header.AddRange(U32(0));
        header.AddRange(U32(1)); // MH_OBJECT
        header.AddRange(U32((uint)options.Length + 1));
        header.AddRange(U32((uint)cmds.Count));
        header.AddRange(U32(0));
        header.AddRange(U32(0));
        return [.. header, .. cmds, .. nlists, .. strtab];
    }

    static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    // A BSD ar archive, with "#1/<n>" long names as Apple's libtool writes them.
    static byte[] Archive(params (string Name, byte[] Data)[] members)
    {
        var a = new List<byte>(Encoding.ASCII.GetBytes("!<arch>\n"));
        foreach (var (name, data) in members)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name);
            int size = nameBytes.Length + data.Length;
            a.AddRange(Encoding.ASCII.GetBytes($"{"#1/" + nameBytes.Length,-16}{0,-12}{0,-6}{0,-6}{"100644",-8}{size,-10}`\n"));
            a.AddRange(nameBytes);
            a.AddRange(data);
            if (a.Count % 2 != 0)
                a.Add((byte)'\n');
        }
        return [.. a];
    }

    static byte[] Fat(params (int Cpu, byte[] Data)[] slices)
    {
        var f = new List<byte>();
        void Be(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); f.AddRange(b); }
        Be(0xcafebabe);
        Be((uint)slices.Length);
        int offset = 4096;
        foreach (var (cpu, data) in slices)
        {
            Be((uint)cpu); Be(0); Be((uint)offset); Be((uint)data.Length); Be(12);
            offset += (data.Length + 4095) / 4096 * 4096;
        }
        foreach (var (_, data) in slices)
        {
            while (f.Count % 4096 != 0)
                f.Add(0);
            f.AddRange(data);
        }
        return [.. f];
    }

    static string Temp(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), "aotanywhere-autolink-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, data);
        return path;
    }

    [Test]
    public async Task CollectsOptionsFromTheLoadedMembersOfTheMatchingSlice()
    {
        // main.o needs _interop; Interop.o defines it (and needs _other), so both load. Unused.o
        // defines nothing anyone needs - like ld64, its options don't count.
        var main = Temp(Object(Arm64, ["-_interop"]));
        var lib = Temp(Fat(
            (X64, Archive(("a.o", Object(X64, ["_interop"], ["-lswiftX64Only"])))),
            (Arm64, Archive(
                ("Interop.o", Object(Arm64, ["_interop", "-_other"], ["-lswiftCompatibility56"], ["-framework", "UIKit"])),
                ("Other.o", Object(Arm64, ["_other"], ["-lswiftCompatibility56"], ["-lswiftMetal"])),
                ("Unused.o", Object(Arm64, ["_unused"], ["-lswiftObservation"]))))));

        var options = MachoAutolink.Collect([main, lib], "arm64");

        await Assert.That(options.Select(o => string.Join(' ', o))).IsEquivalentTo(
            new[] { "-lswiftCompatibility56", "-framework UIKit", "-lswiftMetal" });
    }

    [Test]
    public async Task HiddenDefinitionsDoNotLoadAMember()
    {
        // Private externs aren't in an archive's table of contents.
        var main = Temp(Object(Arm64, ["-_hidden"]));
        var lib = Temp(Archive(("Hidden.o", Object(Arm64, ["!_hidden"], ["-lswiftUIKit"]))));

        await Assert.That(MachoAutolink.Collect([main, lib], "arm64")).IsEmpty();
    }

    [Test]
    public async Task AnalysisReportsWhatIsOnlyReferencedWeakly()
    {
        var a = Temp(Object(Arm64, ["~__swift_FORCE_LOAD_$_swiftMetal", "-_strong", "~_both"]));
        var b = Temp(Object(Arm64, ["-_both"]));

        var analysis = MachoAutolink.Analyze([a, b], "arm64");

        await Assert.That(analysis.Undefined).IsEquivalentTo(new[] { "__swift_FORCE_LOAD_$_swiftMetal", "_strong", "_both" });
        await Assert.That(analysis.WeakOnly).IsEquivalentTo(new[] { "__swift_FORCE_LOAD_$_swiftMetal" });
    }

    [Test]
    public async Task ResolveKeepsOnlyWhatTheSearchPathsHaveAndIsNotAlreadyThere()
    {
        var root = Path.Combine(Path.GetTempPath(), "aotanywhere-autolink-" + Guid.NewGuid().ToString("N"));
        var lib = Directory.CreateDirectory(Path.Combine(root, "lib")).FullName;
        var fw = Directory.CreateDirectory(Path.Combine(root, "fw")).FullName;
        File.WriteAllText(Path.Combine(lib, "libswiftMetal.tbd"), "");
        File.WriteAllText(Path.Combine(lib, "libswiftCompatibility56.a"), "");
        Directory.CreateDirectory(Path.Combine(fw, "UIKit.framework"));
        Directory.CreateDirectory(Path.Combine(fw, "Metal.framework"));

        var added = MachoAutolink.Resolve(
            [["-lswiftCompatibility56"], ["-lswiftMetal"], ["-lswiftNotThere"], ["-framework", "UIKit"], ["-framework", "Metal"], ["-framework", "NotThere"]],
            [lib], [fw], ["-lswiftMetal", "-framework", "UIKit"]);

        await Assert.That(added).IsEquivalentTo(new[] { "-lswiftCompatibility56", "-framework", "Metal" });
    }

    static string Stub(string dir, string file, string installName, string[] own, string[]? reexported = null)
    {
        var path = Path.Combine(dir, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Tbd.Write(installName, StubLink.Targets,
            new Dictionary<string, IReadOnlyCollection<string>> { ["symbols"] = own },
            reexported: reexported is null ? null : new Dictionary<string, IReadOnlyCollection<string>> { ["symbols"] = reexported }));
        return path;
    }

    [Test]
    public async Task WithUndefinedSymbolsOnlyLibrariesThatSupplyOneAreAdded()
    {
        // ld64 treats autolinked libraries as dead-strippable.
        var lib = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aotanywhere-autolink-" + Guid.NewGuid().ToString("N"))).FullName;
        Stub(lib, "libLinked.tbd", "/usr/lib/libLinked.dylib", ["_a"]);
        Stub(lib, "libReexporter.tbd", "/usr/lib/libReexporter.dylib", [], ["_a"]);   // only re-exports what's already there
        Stub(lib, "libImplementer.tbd", "/usr/lib/libImplementer.dylib", ["_a"]);     // implements it: ld64 binds there
        Stub(lib, "libUnused.tbd", "/usr/lib/libUnused.dylib", ["_z"]);
        Stub(lib, "libNew.tbd", "/usr/lib/libNew.dylib", [], ["_b"]);                 // re-exports something nothing else has

        var added = MachoAutolink.Resolve(
            [["-lReexporter"], ["-lImplementer"], ["-lUnused"], ["-lNew"], ["-lMissing"]],
            [lib], [], ["-lLinked"], out var missing, new HashSet<string> { "_a", "_b" });

        await Assert.That(added).IsEquivalentTo(new[] { "-lImplementer", "-lNew" });
        await Assert.That(missing).IsEquivalentTo(new[] { "Missing" });
    }
}
