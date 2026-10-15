namespace AotAnywhere.AppleTools;

/// <summary>
/// A zig driver invocation: <c>zig cc ...</c> or <c>zig c++ ...</c>. <paramref name="Sections"/> are
/// <c>-sectcreate</c> requests, which zig's linker doesn't support: the caller assembles each file
/// into an object (<see cref="ClangToZig.SectionAssembly"/>) and appends it to <paramref name="Args"/>.
/// </summary>
public sealed record ZigInvocation(string Driver, IReadOnlyList<string> Args, IReadOnlyList<SectionInput>? Sections = null);

/// <summary>A <c>-sectcreate &lt;segment&gt; &lt;section&gt; &lt;file&gt;</c> request.</summary>
public sealed record SectionInput(string Segment, string Section, string File);

/// <summary>
/// Compile against generated declarations (<see cref="ObjCShims"/>) instead of the SDK's headers:
/// <paramref name="ShimDir"/> holds the framework headers, <paramref name="DarwinLibcInclude"/> is
/// zig's open-source Darwin libc/objc4 headers (<c>lib/libc/include/any-darwin-any</c>).
/// </summary>
public sealed record HeaderShims(string ShimDir, string DarwinLibcInclude);

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
    // zig ignores LC_LINKER_OPTION, so the overlays libswiftCore pulls in through
    // autolinking have to be named explicitly - the same list the macOS link
    // takeover uses (src/DirectLink.targets, _AotAnywhereSwiftOverlayLib).
    static readonly string[] SwiftOverlays =
    [
        "swiftCoreFoundation", "swiftDarwin", "swiftDispatch", "swiftFoundation", "swiftObjectiveC",
        "swiftXPC", "swift_Builtin_float", "swift_errno", "swift_math", "swift_signal", "swift_stdio",
        "swift_time", "swiftsys_time", "swiftunistd",
    ];

    static readonly string[] CxxSourceExtensions = [".mm", ".cpp", ".cc", ".cxx"];

    public static ZigInvocation Translate(string tool, IReadOnlyList<string> args, HeaderShims? shims = null)
    {
        if (tool is not ("clang" or "clang++"))
            throw new AppleToolException($"AotAnywhere: unexpected compiler driver '{tool}'.");

        string? sdk = null, arch = null, minOS = null;
        bool simulator = false, compile = false, cxxSource = false, hasLibCxx = false, hasSwift = false;
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
                case "-lswiftCore":
                    hasSwift = true;
                    break;
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
        zigArgs.AddRange(rest);
        if (hasLibCxx || tool == "clang++")
            zigArgs.Add($"{sdk}/usr/lib/libc++.tbd");
        zigArgs.Add($"-L{sdk}/usr/lib");
        zigArgs.Add("-lSystem");
        if (hasSwift)
        {
            zigArgs.Add("-Wl,-dead_strip_dylibs");
            zigArgs.AddRange(SwiftOverlays.Select(l => "-l" + l));
        }
        return new ZigInvocation("cc", zigArgs, sections);
    }

    /// <summary>
    /// Assembly source for an object carrying <paramref name="input"/>'s file as its own section,
    /// kept by <c>-dead_strip</c> - the equivalent of ld64's <c>-sectcreate</c>.
    /// </summary>
    public static string SectionAssembly(SectionInput input) =>
        $".section {input.Segment},{input.Section},regular,no_dead_strip\n.incbin \"{input.File.Replace("\\", "/").Replace("\"", "\\\"")}\"\n";

    /// <summary>The <c>--target=</c> argument of a translated invocation, for compiling side objects.</summary>
    public static string TargetOf(ZigInvocation inv) => inv.Args.First(a => a.StartsWith("--target=", StringComparison.Ordinal));

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
