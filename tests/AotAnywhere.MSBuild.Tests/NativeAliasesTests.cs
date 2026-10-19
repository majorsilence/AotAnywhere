using System.Diagnostics;
using System.Runtime.InteropServices;
using AotAnywhere.AppleTools;
using TUnit.Core.Enums;

namespace AotAnywhere.MSBuild.Tests;

// The workload's tasks P/Invoke macOS's C library by path; off a Mac those loads are redirected
// (NativeAliases), on Linux to the darwin-shim.c stand-in.
public class NativeAliasesTests
{
    [DllImport("/usr/lib/libc.dylib", EntryPoint = "getpid")]
    static extern int DarwinGetpid();

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task MacOSLibcLoadsAreRedirected()
    {
        // Linux: glibc stands in (getpid means the same); macOS: the real library loads first.
        NativeAliases.Install(OperatingSystem.IsLinux() ? "libc.so.6" : "/usr/lib/libSystem.B.dylib");
        await Assert.That(DarwinGetpid()).IsEqualTo(Environment.ProcessId);
        await Assert.That(NativeAliases.IsDarwinLibrary("/usr/lib/libSystem.dylib")).IsTrue();
        await Assert.That(NativeAliases.IsDarwinLibrary("libz")).IsFalse();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int StatFn([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int CopyFileFn([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, IntPtr state, uint flags);

    [Test]
    [RunOn(OS.Linux)]
    public async Task LinuxShimFillsMacOSStatAndCopiesFiles()
    {
        string? zig = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, "zig")).FirstOrDefault(File.Exists);
        if (zig is null)
            return; // the CI hosts that run this have the patched zig on PATH
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aotanywhere-shim-" + Guid.NewGuid().ToString("N"))).FullName;
        string source = Path.Combine(Harness.RepoRoot, "src", "Apple", "tools", "launcher", "darwin-shim.c");
        string shim = Path.Combine(dir, "libaotanywhere-darwin.so");
        using (var p = Process.Start(zig, ["cc", "-O2", "-shared", "-fPIC", source, "-o", shim]))
            await p.WaitForExitAsync();
        string file = Path.Combine(dir, "data.bin");
        File.WriteAllBytes(file, new byte[1234]);

        IntPtr lib = NativeLibrary.Load(shim);
        var buf = new byte[144];
        int result = Marshal.GetDelegateForFunctionPointer<StatFn>(NativeLibrary.GetExport(lib, "stat$INODE64"))(file, buf);
        await Assert.That(result).IsEqualTo(0);
        await Assert.That(BitConverter.ToInt64(buf, 96)).IsEqualTo(1234L);            // st_size
        await Assert.That(BitConverter.ToUInt16(buf, 4) & 0xf000).IsEqualTo(0x8000);   // st_mode: S_IFREG

        string copy = Path.Combine(dir, "copy.bin");
        result = Marshal.GetDelegateForFunctionPointer<CopyFileFn>(NativeLibrary.GetExport(lib, "copyfile"))(file, copy, IntPtr.Zero, 1 << 3 /* COPYFILE_DATA */);
        await Assert.That(result).IsEqualTo(0);
        await Assert.That(new FileInfo(copy).Length).IsEqualTo(1234L);
        // Calls macOS and Linux share come from glibc, through the shim's dependency on it.
        await Assert.That(NativeLibrary.TryGetExport(lib, "realpath", out _)).IsTrue();
    }
}
