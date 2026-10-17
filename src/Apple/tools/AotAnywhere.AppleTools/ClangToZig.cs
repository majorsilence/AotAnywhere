namespace AotAnywhere.AppleTools;

/// <summary>
/// A zig driver invocation: <c>zig cc ...</c> or <c>zig c++ ...</c>. <paramref name="Sections"/> are
/// <c>-sectcreate</c> requests, which zig's linker doesn't support: the caller assembles each file
/// into an object (<see cref="ClangToZig.SectionAssembly"/>) and appends it to <paramref name="Args"/>.
/// </summary>
public sealed record ZigInvocation(string Driver, IReadOnlyList<string> Args, IReadOnlyList<SectionInput>? Sections = null)
{
    /// <summary>Extra assembly sources to assemble and link alongside (see <see cref="ClangToZig.ForceLoadAssembly"/>).</summary>
    public IReadOnlyList<string> ExtraAssembly { get; init; } = [];

    /// <summary>
    /// A zig libc installation file (passed as <c>ZIG_LIBC</c>) to link with, or null. See
    /// <see cref="ClangToZig.ZigLibcFile"/>.
    /// </summary>
    public string? ZigLibc { get; init; }

    /// <summary>Objective-C classes are left for dyld's flat lookup (see <see cref="StubLink"/>).</summary>
    public bool FlatObjCClasses { get; init; }
}

/// <summary>A <c>-sectcreate &lt;segment&gt; &lt;section&gt; &lt;file&gt;</c> request.</summary>
public sealed record SectionInput(string Segment, string Section, string File);

/// <summary>
/// Compile against generated declarations (<see cref="ObjCShims"/>) instead of the SDK's headers:
/// <paramref name="ShimDir"/> holds the framework headers, <paramref name="DarwinLibcInclude"/> is
/// zig's open-source Darwin libc/objc4 headers (<c>lib/libc/include/any-darwin-any</c>).
/// </summary>
public sealed record HeaderShims(string ShimDir, string DarwinLibcInclude);

/// <summary>
/// Link against AotAnywhere's own .tbd stubs (src/Apple/sysroot) instead of the SDK's. Search
/// paths the workload passes into <paramref name="XcodeDir"/> (the SDK, the toolchain's Swift
/// libraries) are dropped, so nothing of Xcode's is read even when it exists.
/// </summary>
public sealed record LinkStubs(string Sysroot, string? XcodeDir, string DarwinLibcInclude = "");

/// <summary>
/// Translates the Apple clang command lines the net*-ios workload builds (CompileNativeCode for
/// main.mm / registrar.mm, LinkNativeCode for the app executable) into zig cc.
///
/// Apple clang finds everything through <c>-isysroot</c>; zig does not, and needs:
/// <list type="bullet">
/// <item>an explicit <c>--target=&lt;arch&gt;-ios.&lt;min&gt;[-simulator]</c> instead of <c>-arch</c> + <c>-m*-version-min</c>;</item>
/// <item><c>-F</c> for the SDK's Frameworks and SubFrameworks (UIKit's headers include UIUtilities, a subframework);</item>
/// <item>the SDK's own libc++: zig otherwise builds its bundled libc++ for iOS, which fails, so C++ compiles get
///   <c>-nostdinc++ -I&lt;sdk&gt;/usr/include/c++/v1</c> (ahead of the C headers, as libc++ requires) and links get
///   <c>&lt;sdk&gt;/usr/lib/libc++.tbd</c> in place of <c>-lc++</c>/<c>-lstdc++</c>;</item>
/// <item><c>-L&lt;sdk&gt;/usr/lib -lSystem</c> (zig has no iOS libSystem of its own);</item>
/// <item><c>-fno-sanitize=undefined</c>: zig's default UBSan runtime does not build for iOS.</item>
/// </list>
/// Linker-only flags clang accepts bare (<c>-dead_strip</c>, <c>-rpath X</c>) are passed through <c>-Wl,</c>.
/// </summary>
public static class ClangToZig
{
    static readonly string[] CxxSourceExtensions = [".mm", ".cpp", ".cc", ".cxx"];

    public static ZigInvocation Translate(string tool, IReadOnlyList<string> args, HeaderShims? shims = null, LinkStubs? stubs = null)
    {
        if (tool is not ("clang" or "clang++"))
            throw new AppleToolException($"AotAnywhere: unexpected compiler driver '{tool}'.");

        string? sdk = null, arch = null, minOS = null;
        bool simulator = false, compile = false, cxxSource = false, hasLibCxx = false;
        var rest = new List<string>();
        var sections = new List<SectionInput>();

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-isysroot":
                    sdk = Next(args, ref i, a);
                    continue;
                case "-arch":
                    arch = Next(args, ref i, a);
                    continue;
                // zig's linker does not read exported-symbol lists. The workload only
                // passes one for the executable, whose exports nothing loads by name.
                case "-exported_symbols_list":
                    Next(args, ref i, a);
                    continue;
                case "-dead_strip":
                    rest.Add("-Wl,-dead_strip");
                    continue;
                case "-rpath":
                    rest.Add("-Wl,-rpath," + Next(args, ref i, a));
                    continue;
                case "-lc++":
                case "-lstdc++":
                    hasLibCxx = true;
                    continue;
                case "-c":
                    compile = true;
                    break;
                // -Xlinker -sectcreate -Xlinker <segment> -Xlinker <section> -Xlinker <file>:
                // the simulator build embeds its entitlements this way.
                case "-Xlinker" when i + 1 < args.Count && args[i + 1] == "-sectcreate":
                    i++;
                    sections.Add(new SectionInput(XlinkerValue(args, ref i), XlinkerValue(args, ref i), XlinkerValue(args, ref i)));
                    continue;
                case "-sectcreate":
                    sections.Add(new SectionInput(Next(args, ref i, a), Next(args, ref i, a), Next(args, ref i, a)));
                    continue;
            }

            if (TryParseMinVersion(a, out var platform, out var version))
            {
                minOS = version;
                simulator = platform.Contains("simulator");
                continue;
            }

            if (!a.StartsWith('-') && CxxSourceExtensions.Any(e => a.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                cxxSource = true;

            rest.Add(a);
        }

        if (sdk is null)
            throw new AppleToolException("AotAnywhere: the clang command line has no -isysroot, so the iOS SDK is unknown.");
        if (arch is null)
            throw new AppleToolException("AotAnywhere: the clang command line has no -arch.");
        if (minOS is null)
            throw new AppleToolException("AotAnywhere: the clang command line has no -m<platform>-version-min=, so the deployment target is unknown.");

        var zigArgs = new List<string>
        {
            $"--target={ZigArch(arch)}-ios.{minOS}{(simulator ? "-simulator" : "")}",
            "-fno-sanitize=undefined",
        };

        if (compile && shims is not null)
        {
            // No SDK headers at all: the generated code only needs the C/objc runtime headers
            // and declarations of the framework types it names, and uses no C++ library.
            zigArgs.Add("-nostdinc++");
            zigArgs.Add("-isystem");
            zigArgs.Add(shims.DarwinLibcInclude);
            zigArgs.Add("-isystem");
            zigArgs.Add(shims.ShimDir);
            zigArgs.AddRange(rest);
            return new ZigInvocation(tool == "clang++" || cxxSource ? "c++" : "cc", zigArgs);
        }

        zigArgs.Add($"-F{sdk}/System/Library/Frameworks");
        zigArgs.Add($"-F{sdk}/System/Library/SubFrameworks");

        if (compile)
        {
            bool cxx = tool == "clang++" || cxxSource;
            if (cxx)
            {
                zigArgs.Add("-nostdinc++");
                zigArgs.Add($"-I{sdk}/usr/include/c++/v1");
            }
            zigArgs.Add("-isystem");
            zigArgs.Add($"{sdk}/usr/include");
            zigArgs.AddRange(rest);
            return new ZigInvocation(cxx ? "c++" : "cc", zigArgs);
        }

        // Link with `zig cc` even for clang++: zig's c++ driver would link its own
        // libc++, and the SDK's is the one the app must load at run time.
        string libRoot = sdk;
        if (stubs is not null)
        {
            libRoot = stubs.Sysroot;
            zigArgs.RemoveAll(a => a.StartsWith("-F" + sdk, StringComparison.Ordinal));
            zigArgs.Add($"-F{libRoot}/System/Library/Frameworks");
            rest = WithoutSearchPathsUnder(rest, stubs.XcodeDir);
        }
        zigArgs.AddRange(WithoutDuplicateLibraries(rest));
        if (hasLibCxx || tool == "clang++")
            zigArgs.Add($"{libRoot}/usr/lib/libc++.tbd");
        zigArgs.Add($"-L{libRoot}/usr/lib");
        // Swift overlays and other LC_LINKER_OPTION libraries are added by the caller's autolink
        // pass (MachoAutolink), from here. Not -dead_strip_dylibs: like ld64, keep every library
        // the workload names, since managed code can look classes up by name at run time.
        zigArgs.Add($"-L{libRoot}/usr/lib/swift");
        zigArgs.Add("-lSystem");
        return new ZigInvocation("cc", zigArgs, sections) { ZigLibc = stubs is null ? null : ZigLibcFile(stubs) };
    }

    /// <summary>
    /// For device targets zig links its own bundled libSystem stub (macOS-only, so nothing
    /// resolves) in addition to any -lSystem it is given, which leaves the app with two
    /// libSystem load commands that dyld refuses. Naming the stub sysroot as zig's libc
    /// installation makes zig take libSystem from it instead (and the SDK version for
    /// LC_BUILD_VERSION from its SDKSettings.json).
    /// </summary>
    public static string ZigLibcFile(LinkStubs stubs) =>
        $"include_dir={stubs.DarwinLibcInclude}\nsys_include_dir={stubs.DarwinLibcInclude}\ncrt_dir={stubs.Sysroot}/usr/lib\n" +
        $"msvc_lib_dir=\nkernel32_lib_dir=\ngcc_dir=\ndarwin_sdk_dir={stubs.Sysroot}\n";

    /// <summary>
    /// Assembly source for an object carrying <paramref name="input"/>'s file as its own section,
    /// kept by <c>-dead_strip</c> - the equivalent of ld64's <c>-sectcreate</c>.
    /// </summary>
    public static string SectionAssembly(SectionInput input) =>
        $".section {input.Segment},{input.Section},regular,no_dead_strip\n.incbin \"{input.File.Replace("\\", "/").Replace("\"", "\\\"")}\"\n";

    /// <summary>
    /// Defines <c>__swift_FORCE_LOAD_$_&lt;library&gt;</c> for a Swift library that only ships with
    /// Xcode's toolchain (<c>swiftCompatibility56</c>, <c>swiftCompatibilityConcurrency</c>): Swift
    /// objects reference that marker to make ld64 pull the static library in. Those libraries
    /// back-deploy runtime fixes to older OS versions; without them the app links and runs, minus
    /// the back-deployed fixes on OS versions that predate them.
    /// </summary>
    public static string ForceLoadAssembly(string library) =>
        $".section __DATA,__data\n.private_extern __swift_FORCE_LOAD_$_{library}\n.globl __swift_FORCE_LOAD_$_{library}\n__swift_FORCE_LOAD_$_{library}:\n.byte 0\n";

    // The iOS release whose Swift runtime includes each back-deployment library's fixes.
    static readonly Dictionary<string, Version> BackDeployedUntil = new(StringComparer.Ordinal)
    {
        ["swiftCompatibility56"] = new(15, 4),
        ["swiftCompatibilityConcurrency"] = new(15, 0),
    };

    /// <summary>
    /// Warnings for the Swift back-deployment libraries a link stands in for with
    /// <see cref="ForceLoadAssembly"/>, when the app's deployment target (from
    /// <paramref name="target"/>, a <c>--target=</c> argument) is older than the iOS release that
    /// has their fixes built in.
    /// </summary>
    public static IEnumerable<string> BackDeploymentWarnings(IEnumerable<string> libraries, string target)
    {
        string os = target[(target.IndexOf("-ios.", StringComparison.Ordinal) + 5)..];
        if (os.IndexOf('-') is int dash and >= 0)
            os = os[..dash];
        Version.TryParse(os.Contains('.') ? os : os + ".0", out var deployment);
        foreach (var library in libraries)
        {
            bool known = BackDeployedUntil.TryGetValue(library, out var until);
            if (known && deployment is not null && deployment >= until)
                continue;
            yield return $"Swift code in this app asks for Swift's {library} back-deployment library, which only ships with " +
                $"Xcode's toolchain; it is left out, so the Swift runtime fixes it carries are missing on iOS versions " +
                (known ? $"before {until!.Major}.{until.Minor} (the app's deployment target is {os}). Set SupportedOSPlatformVersion to {until.Major}.{until.Minor} or later, or build on a Mac with Xcode, if the app must run there."
                       : $"that predate them (the app's deployment target is {os}). Build on a Mac with Xcode if the app must run there.");
        }
    }

    /// <summary>
    /// Whether a failed zig link's output shows nothing but undefined Objective-C class symbols
    /// (and the notes that go with them) - the case a stub link resolves by flat lookup.
    /// </summary>
    public static bool OnlyObjCClassesUndefined(string zigOutput)
    {
        bool any = false;
        foreach (var raw in zigOutput.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("note:", StringComparison.Ordinal) || !line.Contains("error", StringComparison.Ordinal))
                continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^error: undefined symbol: (\S+)$");
            if (!m.Success || !(m.Groups[1].Value.StartsWith("_OBJC_CLASS_$_", StringComparison.Ordinal) || m.Groups[1].Value.StartsWith("_OBJC_METACLASS_$_", StringComparison.Ordinal)))
                return false;
            any = true;
        }
        return any;
    }

    /// <summary>The <c>--target=</c> argument of a translated invocation, for compiling side objects.</summary>
    public static string TargetOf(ZigInvocation inv) => inv.Args.First(a => a.StartsWith("--target=", StringComparison.Ordinal));

    // ld64 links a library once however often it is named; zig would write a load command per
    // mention, and dyld rejects a binary with duplicate ones. The workload names some frameworks
    // both as "-framework X" and "-Wl,-framework,X".
    static List<string> WithoutDuplicateLibraries(List<string> args)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a is "-framework" or "-weak_framework" && i + 1 < args.Count)
            {
                if (seen.Add("framework:" + args[i + 1]))
                {
                    result.Add(a);
                    result.Add(args[i + 1]);
                }
                i++;
                continue;
            }
            if (a.StartsWith("-Wl,-framework,", StringComparison.Ordinal) || a.StartsWith("-Wl,-weak_framework,", StringComparison.Ordinal))
            {
                var parts = a.Split(',');
                if (parts.Length == 3 && seen.Add("framework:" + parts[2]))
                {
                    result.Add(parts[1]);
                    result.Add(parts[2]);
                }
                continue;
            }
            if (a.StartsWith("-l", StringComparison.Ordinal) && a.Length > 2 && !seen.Add("lib:" + a[2..]))
                continue;
            result.Add(a);
        }
        return result;
    }

    // Drops -L/-F search paths (either "-Ldir" or "-L dir") inside an Xcode installation.
    static List<string> WithoutSearchPathsUnder(List<string> args, string? xcodeDir)
    {
        if (string.IsNullOrEmpty(xcodeDir))
            return args;
        string prefix = xcodeDir.TrimEnd('/', '\\');
        bool Inside(string path) => path.StartsWith(prefix + "/", StringComparison.Ordinal) || path == prefix;
        var result = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a is "-L" or "-F" && i + 1 < args.Count && Inside(args[i + 1]))
            {
                i++;
                continue;
            }
            if ((a.StartsWith("-L", StringComparison.Ordinal) || a.StartsWith("-F", StringComparison.Ordinal)) && a.Length > 2 && Inside(a[2..]))
                continue;
            result.Add(a);
        }
        return result;
    }

    static string XlinkerValue(IReadOnlyList<string> args, ref int i)
    {
        if (i + 2 >= args.Count || args[i + 1] != "-Xlinker")
            throw new AppleToolException("AotAnywhere: incomplete '-Xlinker -sectcreate' arguments.");
        i += 2;
        return args[i];
    }

    // -miphoneos-version-min=, -mios-version-min=, -miphonesimulator-version-min=, -mios-simulator-version-min=
    static bool TryParseMinVersion(string arg, out string platform, out string version)
    {
        platform = version = "";
        const string suffix = "-version-min=";
        if (!arg.StartsWith("-m", StringComparison.Ordinal))
            return false;
        int idx = arg.IndexOf(suffix, StringComparison.Ordinal);
        if (idx < 0)
            return false;
        platform = arg.Substring(2, idx - 2);
        if (platform is not ("iphoneos" or "ios" or "iphonesimulator" or "ios-simulator"))
            throw new AppleToolException($"AotAnywhere: unsupported deployment target flag '{arg}' (only iOS is supported).");
        version = arg.Substring(idx + suffix.Length);
        return true;
    }

    static string ZigArch(string arch) => arch switch
    {
        "arm64" => "aarch64",
        "x86_64" => "x86_64",
        _ => throw new AppleToolException($"AotAnywhere: unsupported -arch '{arch}'."),
    };

    static string Next(IReadOnlyList<string> args, ref int i, string flag)
    {
        if (i + 1 >= args.Count)
            throw new AppleToolException($"AotAnywhere: '{flag}' is missing its value.");
        return args[++i];
    }
}
