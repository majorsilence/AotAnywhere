using System.Buffers.Binary;
using System.Text;

namespace AotAnywhere.AppleTools;

/// <summary>
/// The subset of <c>install_name_tool</c> the workload uses (InstallNameTool task): <c>-id</c> to
/// re-identify a dylib and <c>-change old new</c> to rewrite a dependency, on a thin 64-bit
/// little-endian Mach-O.
///
/// Dylib load commands embed their path, so a longer name grows the command. The load commands
/// are rebuilt and written back over the header padding (the gap between the end of the load
/// commands and the first section's file contents), exactly the space Apple's tool uses; if the
/// padding is too small the edit fails, as it does with Apple's tool. The code signature is left
/// stale - the workload re-signs every bundled dylib afterwards.
/// </summary>
public static class MachoInstallName
{
    const uint MhMagic64 = 0xfeedfacf;
    const int HeaderSize = 32;

    const uint LcSegment64 = 0x19;
    const uint LcIdDylib = 0xd;
    const uint LcLoadDylib = 0xc;
    const uint LcLoadWeakDylib = 0x80000018;
    const uint LcReexportDylib = 0x8000001f;
    const uint LcLazyLoadDylib = 0x20;
    const uint LcLoadUpwardDylib = 0x80000023;

    // struct dylib_command: cmd, cmdsize, name.offset, timestamp, current_version, compatibility_version
    const int DylibCommandSize = 24;

    public static byte[] Edit(byte[] data, string? newId, IReadOnlyList<(string Old, string New)> changes)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != MhMagic64)
            throw new AppleToolException("AotAnywhere: install_name_tool only supports thin 64-bit Mach-O files.");

        uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        int sizeofcmds = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
        if (HeaderSize + sizeofcmds > data.Length)
            throw new AppleToolException("AotAnywhere: truncated Mach-O load commands.");

        var commands = new List<byte[]>();
        long firstContent = data.Length;
        bool idSeen = false;
        int off = HeaderSize;
        for (uint i = 0; i < ncmds; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));
            if (size < 8 || off + size > HeaderSize + sizeofcmds)
                throw new AppleToolException("AotAnywhere: malformed Mach-O load command.");
            var bytes = data.AsSpan(off, size).ToArray();

            if (cmd == LcSegment64)
                firstContent = Math.Min(firstContent, FirstSectionOffset(bytes));

            if (IsDylibCommand(cmd))
            {
                string name = ReadName(bytes);
                string? replacement = null;
                if (cmd == LcIdDylib)
                {
                    idSeen = true;
                    replacement = newId;
                }
                else
                {
                    foreach (var (old, @new) in changes)
                        if (name == old)
                            replacement = @new;
                }
                if (replacement is not null && replacement != name)
                    bytes = WithName(bytes, replacement);
            }

            commands.Add(bytes);
            off += size;
        }

        if (newId is not null && !idSeen)
            throw new AppleToolException("AotAnywhere: install_name_tool -id given for a file that is not a dylib (no LC_ID_DYLIB).");

        int newSize = commands.Sum(c => c.Length);
        if (HeaderSize + newSize > firstContent)
            throw new AppleToolException($"AotAnywhere: not enough header padding to grow the load commands ({HeaderSize + newSize} bytes needed, {firstContent} available).");

        var result = (byte[])data.Clone();
        int end = Math.Max(HeaderSize + sizeofcmds, HeaderSize + newSize);
        Array.Clear(result, HeaderSize, end - HeaderSize);
        int w = HeaderSize;
        foreach (var c in commands)
        {
            c.CopyTo(result, w);
            w += c.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), (uint)newSize);
        return result;
    }

    static bool IsDylibCommand(uint cmd) =>
        cmd is LcIdDylib or LcLoadDylib or LcLoadWeakDylib or LcReexportDylib or LcLazyLoadDylib or LcLoadUpwardDylib;

    static string ReadName(byte[] cmd)
    {
        int nameOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(cmd.AsSpan(8));
        int len = Array.IndexOf(cmd, (byte)0, nameOff) is var nul and >= 0 ? nul - nameOff : cmd.Length - nameOff;
        return Encoding.UTF8.GetString(cmd, nameOff, len);
    }

    static byte[] WithName(byte[] cmd, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        int size = (DylibCommandSize + nameBytes.Length + 1 + 7) & ~7;   // NUL-terminated, 8-byte aligned
        var result = new byte[size];
        cmd.AsSpan(0, DylibCommandSize).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), DylibCommandSize);
        nameBytes.CopyTo(result, DylibCommandSize);
        return result;
    }

    // segment_command_64 is 72 bytes, followed by nsects section_64 records of 80 bytes;
    // section_64.offset is at +48. Zero-fill sections (offset 0) occupy no file space.
    static long FirstSectionOffset(byte[] seg)
    {
        uint nsects = BinaryPrimitives.ReadUInt32LittleEndian(seg.AsSpan(64));
        long min = long.MaxValue;
        for (int s = 0; s < nsects; s++)
        {
            int at = 72 + s * 80;
            if (at + 80 > seg.Length)
                break;
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(seg.AsSpan(at + 48));
            if (offset != 0)
                min = Math.Min(min, offset);
        }
        return min;
    }
}
