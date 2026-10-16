using System.Buffers.Binary;
using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// The workload embeds frameworks with `ditto --arch <arch> <framework>/ <bundle>/Frameworks/<framework>`.
// Checked against Apple's ditto on SkiaSharp's universal simulator framework: byte-identical output.
public class DittoTests
{
    static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aotanywhere-ditto-" + Guid.NewGuid().ToString("N"))).FullName;

    // A universal file with the given (cputype, cpusubtype) slices, each filled with its own byte.
    static byte[] Fat(params (int Cpu, int Sub)[] slices)
    {
        const int align = 12; // 4 KiB
        var data = new byte[(slices.Length + 1) << align];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0xcafebabe);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)slices.Length);
        for (int i = 0; i < slices.Length; i++)
        {
            var e = data.AsSpan(8 + i * 20, 20);
            int offset = (i + 1) << align;
            BinaryPrimitives.WriteInt32BigEndian(e, slices[i].Cpu);
            BinaryPrimitives.WriteInt32BigEndian(e[4..], slices[i].Sub);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(e[12..], 100);
            BinaryPrimitives.WriteUInt32BigEndian(e[16..], align);
            data.AsSpan(offset, 100).Fill((byte)(i + 1));
        }
        return data;
    }

    const int Arm64 = 0x0100000c, X64 = 0x01000007;

    [Test]
    public async Task ADirectorysContentsAreMergedIntoTheDestination()
    {
        var src = Temp();
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "Info.plist"), "a");
        File.WriteAllText(Path.Combine(src, "sub", "x"), "b");
        var dst = Path.Combine(Temp(), "out");

        Ditto.Run([src + "/", dst]);

        await Assert.That(File.ReadAllText(Path.Combine(dst, "Info.plist"))).IsEqualTo("a");
        await Assert.That(File.ReadAllText(Path.Combine(dst, "sub", "x"))).IsEqualTo("b");
    }

    [Test]
    public async Task ArchThinsUniversalFilesToASingleThinSlice()
    {
        var src = Temp();
        File.WriteAllBytes(Path.Combine(src, "lib"), Fat((X64, 3), (Arm64, 0)));
        var dst = Path.Combine(Temp(), "out");

        Ditto.Run(["--arch", "arm64", src + "/", dst]);

        var thin = File.ReadAllBytes(Path.Combine(dst, "lib"));
        await Assert.That(thin.Length).IsEqualTo(100);
        await Assert.That(thin.All(b => b == 2)).IsTrue();
    }

    [Test]
    public async Task ArchKeepsSeveralSlicesAsAUniversalFile()
    {
        var thinned = Ditto.Thin(Fat((X64, 3), (Arm64, 0), (Arm64, 2)), ["arm64", "x86_64"])!;

        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(thinned)).IsEqualTo(0xcafebabeu);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(thinned.AsSpan(4))).IsEqualTo(2u);
    }

    [Test]
    public async Task NonUniversalOrUnchangedFilesAreLeftAlone()
    {
        await Assert.That(Ditto.Thin([0xcf, 0xfa, 0xed, 0xfe, 0, 0, 0, 0], ["arm64"])).IsNull();
        await Assert.That(Ditto.Thin(Fat((Arm64, 0)), ["arm64"])).IsNull();
    }

    [Test]
    public async Task SymbolicLinksAreCopiedAsLinks()
    {
        if (OperatingSystem.IsWindows())
            return;
        var src = Temp();
        File.WriteAllText(Path.Combine(src, "real"), "x");
        File.CreateSymbolicLink(Path.Combine(src, "link"), "real");
        var dst = Path.Combine(Temp(), "out");

        Ditto.Run([src + "/", dst]);

        await Assert.That(new FileInfo(Path.Combine(dst, "link")).LinkTarget).IsEqualTo("real");
    }

    [Test]
    public async Task ArchiveModesAreRejected()
    {
        await Assert.That(() => Ditto.Run(["-c", "-k", "a", "b.zip"])).Throws<AppleToolException>();
    }
}
