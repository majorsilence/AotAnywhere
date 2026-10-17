#!/usr/bin/env dotnet
#:project ../src/Apple/tools/AotAnywhere.AppleTools/AotAnywhere.AppleTools.csproj
// Generate the iOS linker stub sysroot shipped with StuDev.AotAnywhere.Apple (src/Apple/sysroot).
//
// No Apple SDK file is read: every symbol, and the library it is stubbed in, comes from
// open-source inputs - the MIT-licensed .NET and Microsoft.iOS packs, MAUI's interop library,
// zig's libSystem list, and the naming rules of open-source projects (see IosStubs in
// AotAnywhere.AppleTools for the rules). Runs on any host with zig:
//
//   dotnet run eng/generate-apple-ios-sysroot.cs [-- --zig /path/to/zig]
//
// Update the pinned versions below when new packs ship, re-run, and commit the result.

using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AotAnywhere.AppleTools;

await Generator.RunAsync(args);

static class Generator
{
    static string ScriptPath([CallerFilePath] string path = "") => path;
    static readonly string RepoRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(ScriptPath())))!;
    static readonly string CacheDir = Path.Combine(RepoRoot, "eng", ".cache", "apple-ios-sysroot-packs");
    static readonly string OutputDir = Path.Combine(RepoRoot, "src", "Apple", "sysroot");

    static readonly string[] Rids = ["ios-arm64", "iossimulator-arm64", "iossimulator-x64"];

    // The iOS workload versions the stubs serve, newest first (CI builds with each:
    // .github/workflows/ios-validation.yml). Each brings its own runtime pack (whose native code
    // imports different symbols) and Microsoft.iOS.dll; the stubs carry the union. The SDK version
    // zig records is the newest's.
    static readonly (string SdkVersion, string PackVersion)[] Workloads =
    [
        ("27.0", "27.0.10722"),
        ("26.5", "26.5.10318"),
        ("26.0", "26.0.11017"),
    ];
    static string IosSdkVersion => Workloads[0].SdkVersion;
    static readonly (string Id, string Version)[] RuntimePacks =
    [
        ("microsoft.netcore.app.runtime.nativeaot.{rid}", "10.0.13"),
        .. Workloads.Select(w => ("microsoft.ios.runtime.{rid}.net10.0_" + w.SdkVersion, w.PackVersion)),
    ];

    // Third-party static libraries apps commonly link: (package, version, zip inside the package, member of that zip).
    static readonly (string Id, string Version, string ZipEntry, string Member)[] ThirdParty =
    [
        ("microsoft.maui.core", "10.0.110", "lib/net10.0-ios26.0/Microsoft.Maui.resources.zip",
         "MauiPlatformInteropiOS.xcframework/ios-arm64/MauiPlatformInterop.framework/MauiPlatformInterop"),
        ("microsoft.maui.core", "10.0.110", "lib/net10.0-ios26.0/Microsoft.Maui.resources.zip",
         "MauiPlatformInteropiOS.xcframework/ios-arm64_x86_64-simulator/MauiPlatformInterop.framework/MauiPlatformInterop"),
    ];

    static readonly HttpClient Http = new();

    public static async Task RunAsync(string[] args)
    {
        string zig = ArgValue(args, "--zig") ?? "zig";

        var native = new List<string>();
        foreach (var rid in Rids)
            foreach (var (id, version) in RuntimePacks)
            {
                string dir = await FetchAsync(id.Replace("{rid}", rid), version, e => Regex.IsMatch(e, @"/native/[^/]+\.(a|o)$"));
                native.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => Regex.IsMatch(f, @"\.(a|o)$")));
            }
        foreach (var (id, version, zipEntry, member) in ThirdParty)
            native.Add(await FetchZipMemberAsync(id, version, zipEntry, member));

        var platformAssemblies = new List<string>();
        foreach (var (sdk, version) in Workloads)
        {
            string platformDir = await FetchAsync("microsoft.ios.runtime.ios.net10.0_" + sdk, version, e => e.EndsWith("/Microsoft.iOS.dll", StringComparison.Ordinal));
            platformAssemblies.Add(Directory.EnumerateFiles(platformDir, "Microsoft.iOS.dll", SearchOption.AllDirectories).First());
        }
        string zigLibSystem = Path.Combine(ZigLibDir(zig), "libc", "darwin", "libSystem.tbd");

        var result = IosStubs.Generate(new IosStubs.Inputs(native, platformAssemblies, zigLibSystem, IosSdkVersion), OutputDir);

        Console.WriteLine($"{result.Wanted} symbols, {result.Attributed} stubbed in {result.Libraries} libraries ({result.Files} stub files) -> {Path.GetRelativePath(RepoRoot, OutputDir)}");
        Console.WriteLine($"{result.Unattributed.Count} not stubbed (expected: the app's own objects define them, the linker synthesizes them, or they are toolchain-only):");
        foreach (var s in result.Unattributed)
            Console.WriteLine($"  {s}");
    }

    static async Task<string> FetchAsync(string pack, string version, Func<string, bool> wantEntry)
    {
        string dest = Path.Combine(CacheDir, $"{pack}.{version}");
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            return dest;
        Console.WriteLine($"  downloading {pack} {version} ...");
        byte[] data = await Http.GetByteArrayAsync($"https://api.nuget.org/v3-flatcontainer/{pack}/{version}/{pack}.{version}.nupkg");
        Directory.CreateDirectory(dest);
        using var zip = new ZipArchive(new MemoryStream(data));
        foreach (var entry in zip.Entries.Where(e => wantEntry("/" + e.FullName)))
        {
            string target = Path.Combine(dest, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        return dest;
    }

    static async Task<string> FetchZipMemberAsync(string pack, string version, string zipEntry, string member)
    {
        string dir = await FetchAsync(pack, version, e => e == "/" + zipEntry);
        string target = Path.Combine(dir, "members", member.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(target))
        {
            using var inner = ZipFile.OpenRead(Path.Combine(dir, zipEntry.Replace('/', Path.DirectorySeparatorChar)));
            var entry = inner.GetEntry(member) ?? throw new Exception($"{member} not found in {pack} {zipEntry}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
        return target;
    }

    static string ZigLibDir(string zig)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(zig, "env") { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi)!;
        string env = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var m = Regex.Match(env, "\\.lib_dir = \"([^\"]+)\"");
        if (!m.Success)
            m = Regex.Match(env, "\"lib_dir\":\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : throw new Exception("could not find zig's lib_dir in `zig env`");
    }

    static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
