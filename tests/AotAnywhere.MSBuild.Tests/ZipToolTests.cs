using System.Buffers.Binary;
using System.IO.Compression;
using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// The .ipa must carry Unix file modes (an app whose executable isn't executable doesn't launch).
// Checked on Windows: the workload's System.IO.Compression .ipa has none; after ipa-modes the
// executable is 0755 and every entry is marked as made on Unix.
public class ZipToolTests
{
    static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aotanywhere-zip-" + Guid.NewGuid().ToString("N"))).FullName;

    static readonly byte[] MachO = [0xcf, 0xfa, 0xed, 0xfe, 0x0c, 0x00, 0x00, 0x01];

    static int Mode(ZipArchiveEntry e) => (e.ExternalAttributes >> 16) & 0xffff;

    // Host byte ("version made by" high byte) of every central directory header.
    static List<byte> MadeBy(byte[] zip)
    {
        var hosts = new List<byte>();
        for (int i = 0; i + 4 <= zip.Length; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x02014b50)
                hosts.Add(zip[i + 5]);
        return hosts;
    }

    [Test]
    public async Task FixModesMarksExecutablesAndMarksEntriesAsMadeOnUnix()
    {
        var dir = Temp();
        string ipa = Path.Combine(dir, "App.ipa");
        using (var zip = ZipFile.Open(ipa, ZipArchiveMode.Create))
        {
            zip.CreateEntry("Payload/");
            using (var s = zip.CreateEntry("Payload/App.app/App").Open()) s.Write(MachO);
            using (var s = zip.CreateEntry("Payload/App.app/Info.plist").Open()) s.Write("<plist/>"u8);
        }

        ZipTool.FixModes(ipa);

        using var read = ZipFile.OpenRead(ipa);
        await Assert.That(Mode(read.GetEntry("Payload/")!)).IsEqualTo(0x41ed);              // dir 0755
        await Assert.That(Mode(read.GetEntry("Payload/App.app/App")!)).IsEqualTo(0x81ed);   // 0755
        await Assert.That(Mode(read.GetEntry("Payload/App.app/Info.plist")!)).IsEqualTo(0x81a4); // 0644
        await Assert.That(MadeBy(File.ReadAllBytes(ipa)).All(h => h == 3)).IsTrue();
        using var data = read.GetEntry("Payload/App.app/App")!.Open();
        var copy = new MemoryStream();
        data.CopyTo(copy);
        await Assert.That(copy.ToArray()).IsEquivalentTo(MachO);
    }

    [Test]
    public async Task ZipRecursesWithRelativeNamesLikeTheWorkloadsCall()
    {
        var dir = Temp();
        Directory.CreateDirectory(Path.Combine(dir, "Payload", "App.app"));
        File.WriteAllBytes(Path.Combine(dir, "Payload", "App.app", "App"), MachO);
        if (!OperatingSystem.IsWindows()) // as the linker leaves it; on Unix zip keeps the file's mode
            File.SetUnixFileMode(Path.Combine(dir, "Payload", "App.app", "App"), (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(dir, "Payload", "App.app", "Info.plist"), "x");
        string ipa = Path.Combine(dir, "out", "App.ipa");

        // Relative to dir, as the workload runs it from the app's folder (not by changing this
        // process's current directory: other tests' in-process MSBuild builds change it too).
        ZipTool.Run(["-r", "-y", ipa, "Payload"], dir);

        using var read = ZipFile.OpenRead(ipa);
        await Assert.That(read.Entries.Select(e => e.FullName)).IsEquivalentTo(new[] { "Payload/", "Payload/App.app/", "Payload/App.app/App", "Payload/App.app/Info.plist" });
        int exec = Mode(read.GetEntry("Payload/App.app/App")!);
        await Assert.That(exec & 0x49).IsEqualTo(0x49); // executable bits set (from the file, or inferred on Windows)
        await Assert.That(MadeBy(File.ReadAllBytes(ipa)).All(h => h == 3)).IsTrue();
    }

    [Test]
    public async Task UnknownOptionsAreRejected()
    {
        await Assert.That(() => ZipTool.Run(["-x", "a.zip", "b"])).Throws<AppleToolException>();
    }
}
