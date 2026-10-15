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

    // A minimal MH_OBJECT with one LC_LINKER_OPTION per option.
    static byte[] Object(int cpu, params string[][] options)
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
        var header = new List<byte>();
        header.AddRange(U32(0xfeedfacf));
        header.AddRange(U32((uint)cpu));
        header.AddRange(U32(0));
        header.AddRange(U32(1)); // MH_OBJECT
        header.AddRange(U32((uint)options.Length));
        header.AddRange(U32((uint)cmds.Count));
        header.AddRange(U32(0));
        header.AddRange(U32(0));
        return [.. header, .. cmds];
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
    public async Task CollectsOptionsFromTheMatchingSliceOfAFatArchive()
    {
        var lib = Temp(Fat(
            (X64, Archive(("a.o", Object(X64, ["-lswiftX64Only"])))),
            (Arm64, Archive(
                ("Interop.o", Object(Arm64, ["-lswiftCompatibility56"], ["-framework", "UIKit"])),
                ("Other.o", Object(Arm64, ["-lswiftCompatibility56"], ["-lswiftMetal"]))))));

        var options = MachoAutolink.Collect([lib], "arm64");

        await Assert.That(options.Select(o => string.Join(' ', o))).IsEquivalentTo(
            new[] { "-lswiftCompatibility56", "-framework UIKit", "-lswiftMetal" });
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
}
