#!/usr/bin/env dotnet
#:project ../src/Apple/tools/AotAnywhere.AppleTools/AotAnywhere.AppleTools.csproj
// Generate the iOS linker stub sysroot shipped with StuDev.AotAnywhere.Apple (src/Apple/sysroot).
//
// The iOS counterpart of generate-apple-sysroot.cs: off-Mac iOS links resolve the app's
// imports against these self-authored .tbd stubs instead of Xcode's SDK. Each stub lists
// only symbols that MIT-licensed inputs can make an app import:
//
//   - the undefined symbols of the iOS NativeAOT runtime packs' and Microsoft.iOS runtime
//     packs' native static libraries (the runtime, libxamarin, System.*.Native);
//   - Microsoft.iOS.dll's P/Invoke entry points, [Field] globals and wrapper classes (the
//     ILC object and the generated registrar import these);
//   - the undefined symbols of well-known third-party static libraries (MAUI's Swift interop
//     framework);
//   - zig's libSystem symbol list (C code compiled into the app, e.g. libm calls from ILC).
//
// For those symbols (and their libraries) the stubs also keep the SDK's back-deployment linker
// directives ($ld$previous$, $ld$hide$, $ld$add$, $ld$install_name$): which library a symbol
// lived in on older OS versions. The link step applies them for the app's deployment target,
// as ld64 does.
//
// The local iOS SDKs (iPhoneOS + iPhoneSimulator) are only used as the lookup table that says
// which library exports each of those symbols - the same approach as the macOS stubs and
// zig's own libSystem stub. Must run on macOS with Xcode:
//
//   dotnet run eng/generate-apple-ios-sysroot.cs [-- --zig /path/to/zig]
//
// Update the pinned versions below when new packs ship, re-run, and commit the result.

using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AotAnywhere.AppleTools;

await Generator.RunAsync(args);

static partial class Generator
{
    static string ScriptPath([CallerFilePath] string path = "") => path;
    static readonly string RepoRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(ScriptPath())))!;
    static readonly string CacheDir = Path.Combine(RepoRoot, "eng", ".cache", "apple-ios-sysroot-packs");
    static readonly string OutputDir = Path.Combine(RepoRoot, "src", "Apple", "sysroot");

    static readonly string[] Rids = ["ios-arm64", "iossimulator-arm64", "iossimulator-x64"];

    // (package id format, version). {rid} is substituted.
    static readonly (string Id, string Version)[] RuntimePacks =
    [
        ("microsoft.netcore.app.runtime.nativeaot.{rid}", "10.0.13"),
        ("microsoft.ios.runtime.{rid}.net10.0_27.0", "27.0.10722"),
    ];
    // Microsoft.iOS.dll itself (RID-agnostic runtime pack).
    static readonly (string Id, string Version) PlatformAssemblyPack = ("microsoft.ios.runtime.ios.net10.0_27.0", "27.0.10722");
    // Third-party static libraries apps commonly link: (package, version, path inside the package, path inside that zip).
    static readonly (string Id, string Version, string ZipEntry, string Member)[] ThirdParty =
    [
        ("microsoft.maui.core", "10.0.110", "lib/net10.0-ios26.0/Microsoft.Maui.resources.zip",
         "MauiPlatformInteropiOS.xcframework/ios-arm64/MauiPlatformInterop.framework/MauiPlatformInterop"),
        ("microsoft.maui.core", "10.0.110", "lib/net10.0-ios26.0/Microsoft.Maui.resources.zip",
         "MauiPlatformInteropiOS.xcframework/ios-arm64_x86_64-simulator/MauiPlatformInterop.framework/MauiPlatformInterop"),
    ];

    // The C++ runtime entry points compiled C++ (the workload's generated registrar.mm) calls
    // implicitly: operator new/delete in all their standard forms and the Itanium C++ ABI's
    // exception, guard and RTTI support - names fixed by the C++ standard and ABI.
    static readonly string[] CxxRuntime =
    [
        "__Znwm", "__Znam", "__ZnwmRKSt9nothrow_t", "__ZnamRKSt9nothrow_t",
        "__ZnwmSt11align_val_t", "__ZnamSt11align_val_t", "__ZnwmSt11align_val_tRKSt9nothrow_t", "__ZnamSt11align_val_tRKSt9nothrow_t",
        "__ZdlPv", "__ZdaPv", "__ZdlPvm", "__ZdaPvm", "__ZdlPvRKSt9nothrow_t", "__ZdaPvRKSt9nothrow_t",
        "__ZdlPvSt11align_val_t", "__ZdaPvSt11align_val_t", "__ZdlPvmSt11align_val_t", "__ZdaPvmSt11align_val_t",
        "__ZdlPvSt11align_val_tRKSt9nothrow_t", "__ZdaPvSt11align_val_tRKSt9nothrow_t",
        "__ZSt7nothrow", "__ZSt9terminatev", "__ZSt15get_new_handlerv", "__ZSt15set_new_handlerPFvvE",
        "___cxa_allocate_exception", "___cxa_free_exception", "___cxa_throw", "___cxa_rethrow",
        "___cxa_begin_catch", "___cxa_end_catch", "___cxa_get_exception_ptr", "___cxa_current_exception_type",
        "___cxa_call_unexpected", "___cxa_pure_virtual", "___cxa_deleted_virtual", "___cxa_bad_cast", "___cxa_bad_typeid",
        "___cxa_guard_acquire", "___cxa_guard_release", "___cxa_guard_abort", "___cxa_atexit", "___cxa_thread_atexit",
        "___cxa_demangle", "___gxx_personality_v0",
        "__ZTVN10__cxxabiv117__class_type_infoE", "__ZTVN10__cxxabiv120__si_class_type_infoE", "__ZTVN10__cxxabiv121__vmi_class_type_infoE",
        "__ZTVN10__cxxabiv119__pointer_type_infoE", "__ZTVN10__cxxabiv123__fundamental_type_infoE",
        "__ZTISt9exception", "__ZTISt9bad_alloc", "__ZTISt20bad_array_new_length", "__ZTVSt9bad_alloc",
        "__ZNSt9bad_allocC1Ev", "__ZNSt9bad_allocD1Ev", "__ZNSt9exceptionD2Ev", "__ZNKSt9exception4whatEv",
        "__ZNSt20bad_array_new_lengthC1Ev", "__ZNSt20bad_array_new_lengthD1Ev", "__ZTVSt20bad_array_new_length",
        "__ZTIv", "__ZTIPv", "__ZTIi", "__ZTIc", "__ZTIPKc",
    ];

    // Targets the stubs declare: device (arm64, arm64e) and simulator.
    static readonly string[] Targets = StubLink.Targets;

    static readonly HttpClient Http = new();

    public static async Task RunAsync(string[] args)
    {
        string zig = ArgValue(args, "--zig") ?? "zig";
        var sdks = new[] { "iphoneos", "iphonesimulator" }.Select(s => Run("xcrun", "--sdk", s, "--show-sdk-path").Trim()).ToList();
        Console.WriteLine("SDKs:\n  " + string.Join("\n  ", sdks));

        // --- what MIT inputs can make an app import ------------------------------------
        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal); // symbol -> library the input names for it (informational)
        void Want(string symbol, string? library)
        {
            if (!wanted.TryGetValue(symbol, out var existing) || existing is null)
                wanted[symbol] = library;
        }

        foreach (var rid in Rids)
        {
            var files = new List<string>();
            foreach (var (id, version) in RuntimePacks)
            {
                string pack = id.Replace("{rid}", rid);
                string dir = await FetchAsync(pack, version, e => Regex.IsMatch(e, @"/native/[^/]+\.(a|o)$"));
                files.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
            }
            var external = External(files);
            Console.WriteLine($"{rid} runtime packs: {external.Count} external references");
            foreach (var s in external)
                Want(s, null);
        }

        string platformDir = await FetchAsync(PlatformAssemblyPack.Id, PlatformAssemblyPack.Version, e => e.EndsWith("/Microsoft.iOS.dll", StringComparison.Ordinal));
        var bindings = BindingSymbols.Read(Directory.EnumerateFiles(platformDir, "Microsoft.iOS.dll", SearchOption.AllDirectories).First());
        Console.WriteLine($"Microsoft.iOS.dll: {bindings.Count} binding symbols");
        foreach (var b in bindings)
            Want(b.Symbol, b.Library);

        foreach (var (id, version, zipEntry, member) in ThirdParty)
        {
            string path = await FetchZipMemberAsync(id, version, zipEntry, member);
            var external = External([path]);
            Console.WriteLine($"{id} {Path.GetFileName(member)} ({member.Split('/')[1]}): {external.Count} external references");
            foreach (var s in external)
                Want(s, null);
        }

        foreach (var s in CxxRuntime)
            Want(s, "/usr/lib/libc++.1.dylib");

        string zigLibSystem = Path.Combine(ZigLibDir(zig), "libc", "darwin", "libSystem.tbd");
        int zigCount = 0;
        foreach (var lib in Tbd.Parse(File.ReadAllText(zigLibSystem)))
            foreach (var s in lib.Exports.Keys.Where(s => !s.StartsWith("$ld$", StringComparison.Ordinal)))
            {
                Want(s, "/usr/lib/libSystem.B.dylib");
                zigCount++;
            }
        Console.WriteLine($"zig libSystem: {zigCount} symbols");

        // --- who exports them, according to the SDKs -------------------------------------
        // Per public library: what it exports itself (including the documents Apple inlines for
        // re-exported private libraries) and which other libraries it re-exports by reference.
        var own = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var reexports = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var files2 = new Dictionary<string, SdkFile>(StringComparer.Ordinal); // install name -> file info
        var aliases = new List<(string Rel, string InstallName)>();
        foreach (var sdk in sdks)
        {
            foreach (var rel in SdkTbds(sdk))
            {
                var docs = Tbd.Parse(File.ReadAllText(Path.Combine(sdk, rel)));
                if (docs.Count == 0)
                    continue;
                var self = docs[0];
                aliases.Add((rel, self.InstallName));
                files2.TryAdd(self.InstallName, new SdkFile(rel, self.CurrentVersion, self.CompatibilityVersion));
                if (!own.TryGetValue(self.InstallName, out var exports))
                    own[self.InstallName] = exports = new(StringComparer.Ordinal);
                if (!reexports.TryGetValue(self.InstallName, out var re))
                    reexports[self.InstallName] = re = new(StringComparer.Ordinal);
                var inlined = docs.Select(d => d.InstallName).ToHashSet(StringComparer.Ordinal);
                foreach (var doc in docs)
                {
                    foreach (var (symbol, kind) in doc.Exports)
                        if (wanted.ContainsKey(symbol) || (StubLink.LdDirectiveTarget(symbol) is { } target && (target.Length == 0 || wanted.ContainsKey(target))))
                            exports.TryAdd(symbol, kind);
                    re.UnionWith(doc.ReexportedLibraries.Where(r => !inlined.Contains(r)));
                }
            }
        }

        // --- attribute ---------------------------------------------------------------------
        // A symbol goes into every public library that exports it, directly or through a
        // re-export (Foundation's Swift overlay symbols are in both Foundation and
        // libswiftFoundation). The linker then binds to the first library on the link line that
        // has it - the same choice ld64 makes against the SDK, which matters for back-deployment:
        // an older OS may have the symbol only behind the re-exporting library.
        var effective = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string> Effective(string lib, HashSet<string> visiting)
        {
            if (effective.TryGetValue(lib, out var done))
                return done;
            var result = new Dictionary<string, string>(own[lib], StringComparer.Ordinal);
            visiting.Add(lib);
            foreach (var r in reexports[lib])
                if (own.ContainsKey(r) && !visiting.Contains(r))
                    foreach (var (symbol, kind) in Effective(r, visiting))
                        result.TryAdd(symbol, kind);
            visiting.Remove(lib);
            return effective[lib] = result;
        }

        // Own symbols go under exports:, ones reached through a re-exported library under
        // reexports: - the link step uses the difference to bind like ld64 (to the implementing
        // library when it is linked, else to the re-exporting one).
        var assigned = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal); // install name -> kind -> symbols
        var assignedReexports = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        int placements = 0;
        foreach (var lib in own.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var (symbol, kind) in Effective(lib, new(StringComparer.Ordinal)))
            {
                var target = own[lib].ContainsKey(symbol) ? assigned : assignedReexports;
                if (!target.TryGetValue(lib, out var kinds))
                    target[lib] = kinds = new(StringComparer.Ordinal);
                if (!kinds.TryGetValue(kind, out var set))
                    kinds[kind] = set = new(StringComparer.Ordinal);
                set.Add(symbol);
                placed.Add(symbol);
                placements++;
            }
        }
        var unresolved = wanted.Keys.Where(s => !placed.Contains(s)).Order(StringComparer.Ordinal).ToList();

        // Every public library gets a stub, even without symbols: the workload names libraries
        // and frameworks directly (-liconv, -lswiftFoundation, -framework X for each binding
        // namespace an app uses), and a missing one fails the link even when nothing from it is
        // imported. An empty stub is only a name, an install name and versions.
        foreach (var lib in files2.Keys)
            assigned.TryAdd(lib, new(StringComparer.Ordinal));
        foreach (var lib in assignedReexports.Keys)
            assigned.TryAdd(lib, new(StringComparer.Ordinal));

        // --- write -------------------------------------------------------------------------
        if (Directory.Exists(OutputDir))
            foreach (var d in new[] { "System", "usr" })
                if (Directory.Exists(Path.Combine(OutputDir, d)))
                    Directory.Delete(Path.Combine(OutputDir, d), recursive: true);

        int written = 0;
        foreach (var (rel, installName) in aliases.Distinct())
        {
            if (!assigned.TryGetValue(installName, out var kinds))
                continue;
            var file = files2[installName];
            string text = Tbd.Write(installName, Targets,
                kinds.ToDictionary(k => k.Key, k => (IReadOnlyCollection<string>)k.Value),
                file.CurrentVersion, file.CompatibilityVersion,
                assignedReexports.TryGetValue(installName, out var re) ? re.ToDictionary(k => k.Key, k => (IReadOnlyCollection<string>)k.Value) : null);
            string outPath = Path.Combine(OutputDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, text);
            written++;
        }

        // zig reads the SDK version (for LC_BUILD_VERSION) from the sysroot it links against.
        string sdkVersion = Run("xcrun", "--sdk", "iphoneos", "--show-sdk-version").Trim();
        File.WriteAllText(Path.Combine(OutputDir, "SDKSettings.json"),
            $"{{\n  \"Version\": \"{sdkVersion}\",\n  \"MinimalDisplayName\": \"{sdkVersion}\"\n}}\n");

        Console.WriteLine($"\n{assigned.Count} libraries ({assigned.Count(a => a.Value.Count > 0)} with symbols), {wanted.Count - unresolved.Count} symbols in {placements} placements, {written} stub files -> {Path.GetRelativePath(RepoRoot, OutputDir)}");
        foreach (var (lib, kinds) in assigned.OrderByDescending(a => a.Value.Values.Sum(v => v.Count)).Take(15))
            Console.WriteLine($"  {kinds.Values.Sum(v => v.Count),6}  {lib}");
        var notBinding = unresolved.Where(s => !bindings.Any(b => b.Symbol == s)).ToList();
        Console.WriteLine($"\n{unresolved.Count} symbols no SDK library exports ({notBinding.Count} not from Microsoft.iOS metadata;"
            + " expected: symbols the app's own objects define, toolchain-only Swift compatibility libraries, unavailable-on-iOS bindings):");
        foreach (var s in notBinding.Take(60))
            Console.WriteLine($"  {s}");
    }

    sealed record SdkFile(string Rel, string? CurrentVersion, string? CompatibilityVersion);

    // Public, linkable stubs: frameworks and usr/lib (+ swift). Not PrivateFrameworks/SubFrameworks:
    // what an app can import from those is re-exported (and inlined) by a public umbrella.
    static IEnumerable<string> SdkTbds(string sdk)
    {
        string fw = Path.Combine(sdk, "System", "Library", "Frameworks");
        foreach (var dir in Directory.EnumerateDirectories(fw, "*.framework"))
        {
            string name = Path.GetFileNameWithoutExtension(dir);
            string tbd = Path.Combine(dir, name + ".tbd");
            if (File.Exists(tbd))
                yield return Path.GetRelativePath(sdk, tbd);
        }
        foreach (var sub in new[] { "usr/lib", "usr/lib/swift" })
        {
            string d = Path.Combine(sdk, sub);
            if (Directory.Exists(d))
                foreach (var tbd in Directory.EnumerateFiles(d, "*.tbd"))
                    yield return Path.GetRelativePath(sdk, tbd);
        }
    }

    // Undefined symbols of a set of objects/archives, minus what the set itself defines.
    static HashSet<string> External(IEnumerable<string> files)
    {
        var undefined = new HashSet<string>(StringComparer.Ordinal);
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files.Where(f => Regex.IsMatch(f, @"(\.a|\.o|/[^./]+)$")))
        {
            undefined.UnionWith(NmNames(Capture("xcrun", "nm", "-u", "-j", "-arch", "all", f).Stdout));
            defined.UnionWith(NmNames(Capture("xcrun", "nm", "-U", "-j", "-arch", "all", f).Stdout));
        }
        undefined.ExceptWith(defined);
        return undefined;
    }

    static IEnumerable<string> NmNames(string output) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.EndsWith(':') && !l.Contains(' '));

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
        string env = Run(zig, "env");
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

    static string Run(params string[] argv)
    {
        var (code, stdout, stderr) = Capture(argv);
        return code == 0 ? stdout : throw new Exception($"command failed ({code}): {string.Join(' ', argv)}\n{stderr}");
    }

    static (int Code, string Stdout, string Stderr) Capture(params string[] argv)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in argv.Skip(1))
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, stderr.Result);
    }
}
