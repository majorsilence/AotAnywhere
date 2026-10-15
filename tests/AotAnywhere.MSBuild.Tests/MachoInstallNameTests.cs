using System.Buffers.Binary;
using System.Text;
using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// install_name_tool was checked byte-for-byte against Apple's on a real runtime-pack
// dylib (libSystem.Native.dylib, -id @executable_path/...). These cover the edge
// cases on a synthetic dylib: one __TEXT segment with a section at `textOffset`,
// an LC_ID_DYLIB and one LC_LOAD_DYLIB.
public class MachoInstallNameTests
{
    const uint LcSegment64 = 0x19, LcIdDylib = 0xd, LcLoadDylib = 0xc;

    static byte[] DylibCommand(uint cmd, string name)
    {
        var n = Encoding.UTF8.GetBytes(name);
        int size = (24 + n.Length + 1 + 7) & ~7;
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, cmd);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 24);
        n.CopyTo(b, 24);
        return b;
    }

    static byte[] MakeDylib(string id, string dependency, int textOffset)
    {
        var seg = new byte[72 + 80];
        BinaryPrimitives.WriteUInt32LittleEndian(seg, LcSegment64);
        BinaryPrimitives.WriteUInt32LittleEndian(seg.AsSpan(4), (uint)seg.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(seg.AsSpan(64), 1);              // nsects
        BinaryPrimitives.WriteUInt32LittleEndian(seg.AsSpan(72 + 48), (uint)textOffset);

        var cmds = new[] { seg, DylibCommand(LcIdDylib, id), DylibCommand(LcLoadDylib, dependency) };
        var data = new byte[textOffset + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 6);              // MH_DYLIB
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)cmds.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), (uint)cmds.Sum(c => c.Length));
        int at = 32;
        foreach (var c in cmds)
        {
            c.CopyTo(data, at);
            at += c.Length;
        }
        data.AsSpan(textOffset).Fill(0xAB);                                         // section contents
        return data;
    }

    static List<string> DylibNames(byte[] data)
    {
        var names = new List<string>();
        uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        int off = 32;
        for (int i = 0; i < ncmds; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));
            if (cmd is LcIdDylib or LcLoadDylib)
            {
                int nameOff = off + 24;
                int end = Array.IndexOf(data, (byte)0, nameOff);
                names.Add(Encoding.UTF8.GetString(data, nameOff, end - nameOff));
            }
            off += size;
        }
        return names;
    }

    [Test]
    public async Task IdGrowsIntoHeaderPaddingAndLeavesContentsAlone()
    {
        var input = MakeDylib("@rpath/libX.dylib", "/usr/lib/libSystem.B.dylib", 0x400);

        var output = MachoInstallName.Edit(input, "@executable_path/libX.dylib", []);

        await Assert.That(DylibNames(output)).IsEquivalentTo(new[] { "@executable_path/libX.dylib", "/usr/lib/libSystem.B.dylib" });
        await Assert.That(output.Length).IsEqualTo(input.Length);
        await Assert.That(output.AsSpan(0x400).ToArray().All(b => b == 0xAB)).IsTrue();
    }

    [Test]
    public async Task ChangeRewritesOnlyTheMatchingDependency()
    {
        var input = MakeDylib("@rpath/libX.dylib", "@rpath/libY.dylib", 0x400);

        var output = MachoInstallName.Edit(input, null, [("@rpath/libY.dylib", "@executable_path/libY.dylib"), ("@rpath/libZ.dylib", "nope")]);

        await Assert.That(DylibNames(output)).IsEquivalentTo(new[] { "@rpath/libX.dylib", "@executable_path/libY.dylib" });
    }

    [Test]
    public async Task InsufficientHeaderPaddingIsAnError()
    {
        // Load commands end right before the section: no room to grow.
        var tight = MakeDylib("@rpath/libX.dylib", "/usr/lib/libSystem.B.dylib", 32 + 152 + 48 + 56);

        await Assert.That(() => MachoInstallName.Edit(tight, "@executable_path/a/much/longer/install/name/libX.dylib", []))
            .Throws<AppleToolException>();
    }

    [Test]
    public async Task NonMachoInputIsRejected()
    {
        await Assert.That(() => MachoInstallName.Edit(new byte[64], "x", [])).Throws<AppleToolException>();
    }
}
