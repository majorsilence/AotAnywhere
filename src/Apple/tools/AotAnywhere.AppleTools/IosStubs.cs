using System.Text.RegularExpressions;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Builds the iOS linker stub sysroot (src/Apple/sysroot) from open-source inputs only - no Apple
/// SDK file is read.
///
/// <b>Which symbols.</b> Those MIT-licensed inputs can make an app import: the undefined symbols
/// of the iOS runtime packs' native libraries and of well-known third-party static libraries
/// (MAUI's Swift interop), Microsoft.iOS.dll's P/Invoke entry points and <c>[Field]</c> globals,
/// zig's libSystem list, and the C++ ABI's runtime entry points.
///
/// <b>Which library.</b> Decided the way Microsoft.iOS itself does, from open sources:
/// <list type="bullet">
/// <item>Functions and globals: the library the bindings name. Those paths are exercised at run
///   time - Mono resolves each P/Invoke and field with <c>dlopen(path)</c> + <c>dlsym</c>.</item>
/// <item>libSystem: zig's list (MIT). libobjc, libc++, zlib, ICU: their open-source projects' API
///   names and install names.</item>
/// <item>Swift: the module encoded in each mangled name - its overlay library if it is one of
///   Swift's historical Darwin overlays (<see cref="HistoricalOverlays"/>), else the overlay or framework
///   the inputs' own objects autolink (<c>-lswiftX</c> / <c>-framework X</c>); and the library splits in
///   <see cref="LibraryMoves"/>.</item>
/// <item>Objective-C classes: listed under the framework their binding namespace names, as a hint
///   only - which framework an app using the class must load. Microsoft.iOS never names a class's
///   library (it finds classes with <c>objc_getClass</c>), and classes do move between frameworks
///   (NSLayoutConstraint: UIKit's namespace, Foundation's binary), so the link doesn't bind them to
///   it: <see cref="StubLink"/> leaves them to dyld's flat lookup, which finds a class wherever it
///   lives on the running OS version.</item>
/// </list>
/// Compatibility versions are 0.0.0 (dyld only requires the installed library's to be at least
/// the recorded one); current versions are left out.
/// </summary>
public static class IosStubs
{
    public const string LibSystem = "/usr/lib/libSystem.B.dylib";  // Libsystem (APSL)
    public const string LibObjc = "/usr/lib/libobjc.A.dylib";      // objc4 (APSL)
    public const string LibCxx = "/usr/lib/libc++.1.dylib";        // LLVM libc++ (Apache-2.0)

    /// <summary>
    /// Linker names of <c>/usr/lib</c> libraries apps link (the workload names <c>-lz -liconv
    /// -lcompression -licucore -lobjc -lc++ -ldl -lm</c>), to their install names as each open-source
    /// project builds them (zlib, GNU libiconv, ICU, objc4, Libsystem, LLVM libc++).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> UsrLib = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["libSystem.dylib"] = LibSystem, ["libSystem.B.dylib"] = LibSystem, ["libc.dylib"] = LibSystem,
        ["libdispatch.dylib"] = LibSystem,
        ["libobjc.dylib"] = LibObjc, ["libobjc.A.dylib"] = LibObjc,
        ["libc++.dylib"] = LibCxx, ["libc++.1.dylib"] = LibCxx,
        ["libz.dylib"] = "/usr/lib/libz.1.dylib",
        ["libiconv.dylib"] = "/usr/lib/libiconv.2.dylib",
        ["libicucore.dylib"] = "/usr/lib/libicucore.A.dylib",
        ["libcompression.dylib"] = "/usr/lib/libcompression.dylib",
        ["libsqlite3.dylib"] = "/usr/lib/libsqlite3.dylib",
        ["libxml2.dylib"] = "/usr/lib/libxml2.2.dylib",
    };

    // The linker names Libsystem's own build installs as aliases of libSystem.
    static readonly string[] LibSystemAliases = ["libc", "libm", "libdl", "libpthread", "libinfo", "libdbm", "libpoll", "libproc", "librpcsvc"];

    /// <summary>
    /// The Darwin overlay modules of Swift 5.1 - the first release whose Swift libraries ship in
    /// iOS (12.2) - as listed in Swift's open-source tree (swift-5.1-RELEASE stdlib/public/Darwin,
    /// plus Darwin from stdlib/public/Platform). Each is <c>/usr/lib/swift/libswift&lt;Module&gt;.dylib</c>
    /// on every iOS version since, so a symbol of one of these modules binds to it: where an
    /// overlay's code later moved (CGFloat into the CoreFoundation overlay, much of Foundation's
    /// into Foundation itself), the old library still provides it for the apps built against it.
    /// </summary>
    public static readonly IReadOnlySet<string> HistoricalOverlays = new HashSet<string>(StringComparer.Ordinal)
    {
        "ARKit", "AVFoundation", "Accelerate", "AppKit", "AssetsLibrary", "CallKit", "CloudKit", "Compression", "Contacts",
        "CoreAudio", "CoreData", "CoreFoundation", "CoreGraphics", "CoreImage", "CoreLocation", "CoreMedia", "CryptoTokenKit",
        "Dispatch", "Foundation", "GLKit", "GameplayKit", "HomeKit", "IOKit", "Intents", "MapKit", "MediaPlayer", "Metal",
        "MetalKit", "ModelIO", "NaturalLanguage", "Network", "ObjectiveC", "OpenCL", "Photos", "QuartzCore", "SafariServices",
        "SceneKit", "SpriteKit", "UIKit", "Vision", "WatchKit", "XCTest", "XPC", "os", "simd", "Darwin",
    };

    /// <summary>
    /// Libraries split out of another one, with the OS range in which apps must bind to the old
    /// library instead - written as the library-wide <c>$ld$previous$</c> directives Swift's own
    /// build adds (swift stdlib/public/ClangOverlays/linker-support/magic-symbols-for-install-name.c:
    /// _Builtin_float was part of libswiftDarwin before iOS 18).
    /// </summary>
    public static readonly (string Library, string PreviousLibrary, string Start, string End)[] LibraryMoves =
    [
        ("/usr/lib/swift/libswift_Builtin_float.dylib", "/usr/lib/swift/libswiftDarwin.dylib", "12.2", "18.0"),
    ];

    // Documented C API prefixes of the frameworks the runtime packs' native code calls directly.
    static readonly (string Prefix, string Framework)[] CPrefixes =
    [
        ("_CFHTTP", "CFNetwork"), ("_kCFHTTP", "CFNetwork"), ("_CFReadStreamCreateForHTTPRequest", "CFNetwork"), ("_CFNetwork", "CFNetwork"),
        ("_CF", "CoreFoundation"), ("_kCF", "CoreFoundation"), ("___CF", "CoreFoundation"),
        ("_Sec", "Security"), ("_kSec", "Security"), ("_SSL", "Security"), ("_sec_", "Security"),
        ("_nw_", "Network"), ("__nw_", "Network"),
        ("_gss_", "GSS"), ("_GSS", "GSS"), ("___gss", "GSS"),
        ("_NS", "Foundation"), ("_UI", "UIKit"),
    ];

    /// <summary>What to generate from.</summary>
    /// <param name="NativeInputs">Static libraries and objects whose undefined symbols apps import (runtime packs, third-party libraries).</param>
    /// <param name="PlatformAssembly">Microsoft.iOS.dll.</param>
    /// <param name="ZigLibSystem">zig's <c>lib/libc/darwin/libSystem.tbd</c>.</param>
    /// <param name="SdkVersion">The iOS SDK version the workload targets (zig records it in LC_BUILD_VERSION).</param>
    public sealed record Inputs(IReadOnlyList<string> NativeInputs, string PlatformAssembly, string ZigLibSystem, string SdkVersion);

    public sealed record Result(int Wanted, int Attributed, int Libraries, int Files, IReadOnlyList<string> Unattributed);

    /// <summary>The C++ runtime entry points compiled C++ (the generated registrar) calls implicitly.</summary>
    public static readonly string[] CxxRuntime =
    [
        "__Znwm", "__Znam", "__ZnwmRKSt9nothrow_t", "__ZnamRKSt9nothrow_t",
        "__ZnwmSt11align_val_t", "__ZnamSt11align_val_t", "__ZnwmSt11align_val_tRKSt9nothrow_t", "__ZnamSt11align_val_tRKSt9nothrow_t",
        "__ZdlPv", "__ZdaPv", "__ZdlPvm", "__ZdaPvm", "__ZdlPvRKSt9nothrow_t", "__ZdaPvRKSt9nothrow_t",
        "__ZdlPvSt11align_val_t", "__ZdaPvSt11align_val_t", "__ZdlPvmSt11align_val_t", "__ZdaPvmSt11align_val_t",
        "__ZSt7nothrow", "__ZSt9terminatev", "__ZSt15get_new_handlerv", "__ZSt15set_new_handlerPFvvE",
        "___cxa_allocate_exception", "___cxa_free_exception", "___cxa_throw", "___cxa_rethrow",
        "___cxa_begin_catch", "___cxa_end_catch", "___cxa_get_exception_ptr", "___cxa_current_exception_type",
        "___cxa_call_unexpected", "___cxa_pure_virtual", "___cxa_deleted_virtual", "___cxa_bad_cast", "___cxa_bad_typeid",
        "___cxa_guard_acquire", "___cxa_guard_release", "___cxa_guard_abort", "___cxa_demangle", "___gxx_personality_v0",
        "__ZTVN10__cxxabiv117__class_type_infoE", "__ZTVN10__cxxabiv120__si_class_type_infoE", "__ZTVN10__cxxabiv121__vmi_class_type_infoE",
        "__ZTVN10__cxxabiv119__pointer_type_infoE", "__ZTVN10__cxxabiv123__fundamental_type_infoE",
        "__ZTISt9exception", "__ZTISt9bad_alloc", "__ZTISt20bad_array_new_length", "__ZTVSt9bad_alloc",
        "__ZNSt9bad_allocC1Ev", "__ZNSt9bad_allocD1Ev", "__ZNSt9exceptionD2Ev", "__ZNKSt9exception4whatEv",
        "__ZNSt20bad_array_new_lengthC1Ev", "__ZNSt20bad_array_new_lengthD1Ev", "__ZTVSt20bad_array_new_length",
    ];

    /// <summary>Writes the sysroot into <paramref name="output"/> (replacing its System/ and usr/).</summary>
    public static Result Generate(Inputs inputs, string output)
    {
        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal); // symbol -> library the binding names
        void Want(string symbol, string? library)
        {
            if (!wanted.TryGetValue(symbol, out var existing) || existing is null)
                wanted[symbol] = library;
        }

        var objects = inputs.NativeInputs.SelectMany(MachoAutolink.ReadObjects).ToList();
        foreach (var s in External(objects))
            Want(s, null);
        foreach (var b in BindingSymbols.Read(inputs.PlatformAssembly))
            Want(b.Symbol, b.Library);
        foreach (var s in CxxRuntime)
            Want(s, LibCxx);
        var zigLibSystem = Tbd.Parse(File.ReadAllText(inputs.ZigLibSystem))
            .SelectMany(l => l.Exports.Keys).Where(s => !s.StartsWith("$ld$", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        foreach (var s in zigLibSystem)
            Want(s, LibSystem);

        var context = new Context(
            zigLibSystem,
            Autolinked(objects, "-l", "swift"),
            Autolinked(objects, "-framework", ""),
            BindingSymbols.FrameworkConstants(inputs.PlatformAssembly),
            BindingSymbols.NamespaceLibraries(inputs.PlatformAssembly));

        var assigned = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal); // install name -> list key -> symbols
        var unattributed = new List<string>();
        foreach (var (symbol, named) in wanted.OrderBy(w => w.Key, StringComparer.Ordinal))
        {
            if (Attribute(symbol, named, context) is not { } library)
            {
                unattributed.Add(symbol);
                continue;
            }
            Add(assigned, library, ListKey(symbol), symbol);
        }

        foreach (var (library, previous, start, end) in LibraryMoves)
            foreach (int platform in new[] { 2 /* iOS */, 7 /* iOS simulator */ })
                Add(assigned, library, "symbols", $"$ld$previous${previous}$${platform}${start}${end}$$");

        // Every library a link line may name gets a stub, even without symbols: the workload
        // passes -framework for each binding namespace an app uses, and -l for /usr/lib ones.
        foreach (var library in context.Frameworks.Concat(UsrLib.Values)
                     .Concat(context.SwiftOverlays.Select(o => $"/usr/lib/swift/lib{o}.dylib"))
                     .Concat(HistoricalOverlays.Select(m => $"/usr/lib/swift/libswift{m}.dylib"))
                     .Concat(context.AutolinkFrameworks.Select(FrameworkPath)))
            assigned.TryAdd(library, new(StringComparer.Ordinal));

        foreach (var dir in new[] { "System", "usr" })
            if (Directory.Exists(Path.Combine(output, dir)))
                Directory.Delete(Path.Combine(output, dir), recursive: true);
        int files = 0;
        foreach (var (library, kinds) in assigned)
        {
            string text = Tbd.Write(library, StubLink.Targets, kinds.ToDictionary(k => k.Key, k => (IReadOnlyCollection<string>)k.Value), compatibilityVersion: "0.0.0");
            foreach (var rel in StubPaths(library))
            {
                string path = Path.Combine(output, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                files++;
            }
        }
        File.WriteAllText(Path.Combine(output, "SDKSettings.json"),
            $"{{\n  \"Version\": \"{inputs.SdkVersion}\",\n  \"MinimalDisplayName\": \"{inputs.SdkVersion}\"\n}}\n");

        int attributed = assigned.Values.Sum(k => k.Values.Sum(v => v.Count(s => !s.StartsWith("$ld$", StringComparison.Ordinal))));
        return new Result(wanted.Count, attributed, assigned.Count(a => a.Value.Count > 0), files, unattributed);
    }

    /// <summary>Everything the attribution rules look at besides the symbol.</summary>
    public sealed record Context(
        IReadOnlySet<string> ZigLibSystem,
        IReadOnlySet<string> SwiftOverlays,       // "swiftFoundation" for every -lswiftFoundation the inputs autolink
        IReadOnlySet<string> AutolinkFrameworks,  // "StoreKit" for every -framework StoreKit they autolink
        IReadOnlySet<string> Frameworks,          // Microsoft.iOS ObjCRuntime.Constants framework paths
        IReadOnlyDictionary<string, string> NamespaceLibraries);

    /// <summary>The library a symbol is attributed to, or null when no rule applies (symbols the app defines itself).</summary>
    public static string? Attribute(string symbol, string? namedLibrary, Context context)
    {
        string s = symbol;
        // Linker-synthesized: selector stubs, section bounds.
        if (s.StartsWith("_objc_msgSend$", StringComparison.Ordinal) || s.StartsWith("section$", StringComparison.Ordinal) || s.StartsWith("segment$", StringComparison.Ordinal))
            return null;
        if (s.StartsWith("__ZN9libunwind", StringComparison.Ordinal))
            return LibSystem;
        // C++ runtime: Itanium-mangled names and the __cxa ABI (libc++ / libc++abi). __cxa_atexit is libSystem's.
        if (s.StartsWith("__Z", StringComparison.Ordinal) || s.StartsWith("___cxa_", StringComparison.Ordinal) || s == "___gxx_personality_v0")
            return s is "___cxa_atexit" or "___cxa_thread_atexit" or "___cxa_finalize" ? LibSystem : LibCxx;
        if (SwiftLibrary(s, context) is { } swift)
            return swift;
        // objc4 defines NSObject; zig's libSystem list beats a binding's approximate library name
        // for C functions (the bindings P/Invoke _Block_copy "from" libobjc).
        if (s is "_OBJC_CLASS_$_NSObject" or "_OBJC_METACLASS_$_NSObject")
            return LibObjc;
        if (context.ZigLibSystem.Contains(s))
            return LibSystem;
        if (namedLibrary is not null)
            return Normalize(namedLibrary, context);
        // objc4's public runtime API.
        if (Regex.IsMatch(s, @"^_(objc_|class_|object_|sel_|method_|ivar_|property_|protocol_|imp_)") || s is "__objc_empty_cache" or "___objc_personality_v0")
            return LibObjc;
        // zlib's and ICU's public C APIs.
        if (Regex.IsMatch(s, @"^_(crc32|adler32|deflate|inflate|compress|uncompress|gz|zlibVersion|zError|get_crc_table)"))
            return "/usr/lib/libz.1.dylib";
        if (Regex.IsMatch(s, @"^_u[a-z0-9]*_[a-zA-Z]"))
            return "/usr/lib/libicucore.A.dylib";
        foreach (var (prefix, framework) in CPrefixes)
            if (s.StartsWith(prefix, StringComparison.Ordinal))
                return FrameworkPath(framework);
        if (s == "_OBJC_EHTYPE_$_NSException")
            return FrameworkPath("CoreFoundation");
        return null;
    }

    /// <summary>The library for a Swift symbol, from the module its mangled name encodes; null if not Swift.</summary>
    static string? SwiftLibrary(string s, Context context)
    {
        const string forceLoad = "__swift_FORCE_LOAD_$_";
        if (s.StartsWith(forceLoad, StringComparison.Ordinal))
        {
            string lib = s[forceLoad.Length..];
            // Toolchain-only static libraries; the link step supplies placeholders.
            return lib.StartsWith("swiftCompatibility", StringComparison.Ordinal) ? null : $"/usr/lib/swift/lib{lib}.dylib";
        }
        if (s.StartsWith("_swift_", StringComparison.Ordinal) || s.StartsWith("__swift_", StringComparison.Ordinal))
            return "/usr/lib/swift/libswiftCore.dylib";
        if (!s.StartsWith("_$s", StringComparison.Ordinal))
            return null;
        // $s<len><Module>..., or $sSo<len><ObjCType><kind><len><Module>... for extensions of imported types.
        var m = Regex.Match(s, @"^_\$s(?:So\d+[A-Za-z_][A-Za-z0-9_]*?[CVO](?=\d))?(\d+)([A-Za-z_][A-Za-z0-9_]*)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n <= m.Groups[2].Value.Length)
        {
            string module = m.Groups[2].Value[..n];
            if (module == "main")
                return null; // the app's own Swift code
            if (HistoricalOverlays.Contains(module))
                return $"/usr/lib/swift/libswift{module}.dylib";
            if (module == "_Concurrency")
                return "/usr/lib/swift/libswift_Concurrency.dylib";
            if (context.SwiftOverlays.Contains("swift" + module))
                return $"/usr/lib/swift/libswift{module}.dylib";
            if (context.AutolinkFrameworks.Contains(module))
                return FrameworkPath(module);
            // Not a module any input links: a standard-library entity (associated types such as RawValue).
        }
        return "/usr/lib/swift/libswiftCore.dylib";
    }

    // A binding's library name -> the install name to stub it in.
    static string Normalize(string named, Context context)
    {
        if (UsrLib.TryGetValue(Path.GetFileName(named), out var usrLib))
            return usrLib;
        // Sub-frameworks bind through their umbrella (vImage -> Accelerate), as -framework Accelerate does.
        var sub = Regex.Match(named, @"^(/System/Library/Frameworks/([^/]+)\.framework)/Frameworks/");
        if (sub.Success)
            named = $"{sub.Groups[1].Value}/{sub.Groups[2].Value}";
        var fw = Regex.Match(named, @"^/System/Library/Frameworks/([^/]+)\.framework/");
        bool known = context.Frameworks.Any(f => string.Equals(f, named, StringComparison.Ordinal));
        // A path derived from a namespace: the namespace's own library (AudioUnit -> AudioToolbox).
        if (fw.Success && !known && context.NamespaceLibraries.TryGetValue(fw.Groups[1].Value, out var nsLib))
            named = nsLib;
        // Microsoft's casing (CoreMidi -> CoreMIDI).
        return context.Frameworks.FirstOrDefault(f => string.Equals(f, named, StringComparison.OrdinalIgnoreCase)) ?? named;
    }

    /// <summary>The stub files for an install name: <c>X.framework/X.tbd</c>, or a dylib's linker names.</summary>
    public static IEnumerable<string> StubPaths(string installName)
    {
        if (installName.StartsWith("/System/Library/Frameworks/", StringComparison.Ordinal))
        {
            yield return Path.ChangeExtension(installName.TrimStart('/'), ".tbd");
            yield break;
        }
        // Install names are POSIX paths on every host, so split on '/' rather than with Path.
        int slash = installName.LastIndexOf('/');
        string dir = installName[..slash].TrimStart('/');
        string file = installName[(slash + 1)..];                              // libz.1.dylib
        yield return $"{dir}/{Path.ChangeExtension(file, ".tbd")}";            // libz.1.tbd
        string stem = Regex.Replace(file, @"(\.[A-Z0-9]+)*\.dylib$", "");      // libz
        if (stem + ".dylib" != file)
            yield return $"{dir}/{stem}.tbd";                                  // libz.tbd, for -lz
        if (installName == LibSystem)
            foreach (var alias in LibSystemAliases)
                yield return $"{dir}/{alias}.tbd";
    }

    static string FrameworkPath(string name) => $"/System/Library/Frameworks/{name}.framework/{name}";

    static bool IsObjCClass(string symbol) =>
        symbol.StartsWith("_OBJC_CLASS_$_", StringComparison.Ordinal) || symbol.StartsWith("_OBJC_METACLASS_$_", StringComparison.Ordinal);

    static string ListKey(string symbol) =>
        IsObjCClass(symbol) ? "objc-classes" : symbol.StartsWith("_OBJC_EHTYPE_$_", StringComparison.Ordinal) ? "objc-eh-types" : "symbols";

    static void Add(Dictionary<string, Dictionary<string, HashSet<string>>> assigned, string library, string key, string symbol)
    {
        if (!assigned.TryGetValue(library, out var kinds))
            assigned[library] = kinds = new(StringComparer.Ordinal);
        if (!kinds.TryGetValue(key, out var set))
            kinds[key] = set = new(StringComparer.Ordinal);
        set.Add(symbol);
    }

    // Undefined symbols of a set of objects that the set doesn't define itself.
    static HashSet<string> External(List<MachoAutolink.ObjectSymbols> objects)
    {
        var undefined = objects.SelectMany(o => o.Undefined).ToHashSet(StringComparer.Ordinal);
        undefined.ExceptWith(objects.SelectMany(o => o.Defined));
        return undefined;
    }

    // "-lswiftFoundation" -> "swiftFoundation"; "-framework StoreKit" -> "StoreKit".
    static HashSet<string> Autolinked(List<MachoAutolink.ObjectSymbols> objects, string flag, string prefix)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in objects.SelectMany(o => o.LinkerOptions))
        {
            if (flag == "-l" && option is [var l] && l.StartsWith("-l" + prefix, StringComparison.Ordinal) && !l.StartsWith("-lswiftCompatibility", StringComparison.Ordinal))
                result.Add(l[2..]);
            else if (flag == "-framework" && option is ["-framework" or "-weak_framework", var fw])
                result.Add(fw);
        }
        return result;
    }
}
