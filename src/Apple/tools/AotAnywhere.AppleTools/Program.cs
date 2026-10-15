using System.Diagnostics;
using AotAnywhere.AppleTools;
using AotAnywhere.AppleTools.Xip;

// Usage: AotAnywhere.AppleTools <tool> [args...]
// The generated PATH wrappers (xcrun, clang, clang++, codesign, ...) pass their own name as <tool>.
try
{
    if (args.Length == 0)
        throw new AppleToolException("usage: AotAnywhere.AppleTools <tool> [args...]");
    return Tools.Run(args[0], args.Skip(1).ToList());
}
catch (AppleToolException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static class Tools
{
    public static int Run(string tool, List<string> args) => tool switch
    {
        "xcrun" => Xcrun(args),
        "clang" or "clang++" => Clang(tool, args),
        "install_name_tool" => InstallNameTool(args),
        "codesign" => Codesign(args),
        "strip" => Strip(args),
        "dsymutil" => DSymUtil(args),
        "import-xcode" => ImportXcode(args),
        "objc-shims" => ObjCShimsTool(args),
        "synthetic-xcode" => SyntheticXcodeTool(args),
        // Spotlight indexing of the build output; meaningless off-Mac.
        "mdimport" => 0,
        // On a Mac with Xcode (AotAnywhereAppleForce), defer anything not reimplemented here.
        _ when OperatingSystem.IsMacOS() && File.Exists(AppleXcrun) => Exec(AppleXcrun, [tool, .. args], AppleToolEnvironment()),
        _ => throw new AppleToolException($"AotAnywhere: the Xcode tool '{tool}' is not available off-Mac. If the project uses a feature that needs it (storyboards, asset catalogs, Metal shaders, Core ML models, ...), it can only be built on a Mac with Xcode."),
    };

    const string AppleXcrun = "/usr/bin/xcrun";

    // Apple's tools need the real Xcode: when DEVELOPER_DIR points at a synthetic one, unset it
    // so xcrun falls back to xcode-select's choice.
    static Dictionary<string, string?>? AppleToolEnvironment()
    {
        string? dev = Environment.GetEnvironmentVariable("DEVELOPER_DIR");
        if (dev is null || !File.Exists(Path.Combine(dev, "..", "..", SyntheticXcode.Marker)))
            return null;
        return new() { ["DEVELOPER_DIR"] = null };
    }

    // xcrun [-sdk|--sdk <sdk>] (--show-sdk-path | --show-sdk-version | --show-sdk-platform-path | -f <tool> | <tool> args...)
    static int Xcrun(List<string> args)
    {
        string sdk = "iphoneos";
        int i = 0;
        for (; i < args.Count; i++)
        {
            string a = args[i];
            if (a is "-sdk" or "--sdk")
            {
                sdk = NextArg(args, ref i, a);
                continue;
            }
            if (a is "-v" or "--verbose" or "-l" or "--log" or "-n" or "--no-cache" or "-k" or "--kill-cache")
                continue;
            if (a == "--show-sdk-path")
                return Print(AppleSdk.SdkPath(DeveloperDir(), sdk));
            if (a == "--show-sdk-platform-path")
                return Print(AppleSdk.PlatformPath(DeveloperDir(), sdk));
            if (a == "--show-sdk-version")
                return Print(SdkVersion(AppleSdk.SdkPath(DeveloperDir(), sdk), AppleSdk.PlatformName(sdk)));
            if (a is "-f" or "--find")
                return Print(Path.Combine(ToolsBin(), NextArg(args, ref i, a)));
            break;
        }
        if (i >= args.Count)
            throw new AppleToolException("AotAnywhere: xcrun was called without a tool.");
        return Run(args[i], args.Skip(i + 1).ToList());
    }

    static int Clang(string tool, List<string> args)
    {
        // The ILCompiler targets probe `clang --version` to pick linker workarounds for specific
        // Xcode releases; report a version none of them apply to.
        if (args is ["--version"] or ["-v"])
            return Print($"Apple clang version 17.0.0 (AotAnywhere: zig cc)\nTarget: arm64-apple-darwin\nThread model: posix");

        // Set by the Apple package's targets when compiling against generated declarations
        // (AotAnywhereAppleGeneratedHeaders) instead of the SDK's headers.
        HeaderShims? shims = null;
        if (Environment.GetEnvironmentVariable("AOTANYWHERE_OBJC_SHIMS") is { Length: > 0 } shimDir)
        {
            shims = new HeaderShims(shimDir, Path.Combine(Path.GetDirectoryName(Zig())!, "lib", "libc", "include", "any-darwin-any"));
            // Non-umbrella framework headers (<CoreImage/CIFilterBuiltins.h>) are only known
            // once the generated sources exist.
            foreach (var source in args.Where(a => !a.StartsWith('-') && File.Exists(a) && Path.GetExtension(a) is ".m" or ".mm" or ".c" or ".cpp"))
                foreach (var header in ObjCShims.FrameworkImports(source))
                    ObjCShims.EnsureHeader(shimDir, header);
        }

        // Set when linking against AotAnywhere's stubs (AotAnywhereAppleLinkStubs) instead of the SDK.
        LinkStubs? stubs = Environment.GetEnvironmentVariable("AOTANYWHERE_APPLE_SYSROOT") is { Length: > 0 } sysroot
            ? new LinkStubs(sysroot, Environment.GetEnvironmentVariable("DEVELOPER_DIR"), Path.Combine(Path.GetDirectoryName(Zig())!, "lib", "libc", "include", "any-darwin-any"))
            : null;

        var inv = ClangToZig.Translate(tool, args, shims, stubs);
        int autolinkStart = inv.Args.Count;
        IReadOnlySet<string>? referenced = null, weakOnly = null;
        if (!args.Contains("-c"))
        {
            // Stub links decide which autolinked libraries are used in StubLink, after applying
            // the stubs' back-deployment directives; SDK links decide here.
            var added = Autolink(args, inv.Args, stubs is null, out var missing, out referenced, out weakOnly);
            inv = inv with
            {
                Args = [.. inv.Args, .. added],
                ExtraAssembly = missing.Where(l => l.StartsWith("swiftCompatibility", StringComparison.Ordinal)).Select(ClangToZig.ForceLoadAssembly).ToList(),
            };
        }
        // Stub links bind through a per-link copy of the stubs (see StubLink).
        string? linkSysroot = null;
        if (stubs is not null && !args.Contains("-c"))
        {
            linkSysroot = Directory.CreateTempSubdirectory("aotanywhere-stubs-").FullName;
            inv = inv with
            {
                Args = StubLink.Prepare(inv.Args, stubs.Sysroot, linkSysroot, autolinkStart, referenced, weakOnly, flatObjCClasses: true),
                FlatObjCClasses = true,
                ZigLibc = ClangToZig.ZigLibcFile(stubs with { Sysroot = linkSysroot }),
            };
        }
        int exit;
        try
        {
            exit = RunZig(inv);
        }
        finally
        {
            if (linkSysroot is not null && !Debug)
                Directory.Delete(linkSysroot, recursive: true);
        }
        // Like clang, leave no output behind on failure: zig can leave an empty object, which
        // the workload's incremental build would then take as up to date.
        if (exit != 0 && args.IndexOf("-o") is int o and >= 0 && o + 1 < args.Count && File.Exists(args[o + 1]))
            File.Delete(args[o + 1]);
        return exit;
    }

    // The libraries and frameworks the link's objects ask for through LC_LINKER_OPTION.
    static List<string> Autolink(List<string> clangArgs, IReadOnlyList<string> zigArgs, bool onlyNeeded, out List<string> missing,
        out IReadOnlySet<string>? referenced, out IReadOnlySet<string>? weakOnly)
    {
        missing = [];
        referenced = weakOnly = null;
        int a = clangArgs.IndexOf("-arch");
        if (a < 0 || a + 1 >= clangArgs.Count)
            return [];
        var inputs = clangArgs.Where(x => !x.StartsWith('-') && (x.EndsWith(".a", StringComparison.Ordinal) || x.EndsWith(".o", StringComparison.Ordinal)) && File.Exists(x)).ToList();
        // Static libraries linked by name (libxamarin, the runtime's), and static frameworks
        // (xcframework slices, e.g. MAUI's Swift interop library).
        var libraryDirs = SearchDirs(clangArgs, "-L");
        foreach (var l in clangArgs.Where(x => x.StartsWith("-l", StringComparison.Ordinal) && x.Length > 2))
            inputs.AddRange(libraryDirs.Select(d => Path.Combine(d, "lib" + l[2..] + ".a")).Where(File.Exists).Take(1));
        var frameworkDirs = SearchDirs(clangArgs, "-F");
        for (int i = 0; i + 1 < clangArgs.Count; i++)
            if (clangArgs[i] is "-framework" or "-weak_framework")
                inputs.AddRange(frameworkDirs.Select(d => Path.Combine(d, clangArgs[i + 1] + ".framework", clangArgs[i + 1])).Where(File.Exists).Take(1));
        var analysis = MachoAutolink.Analyze(inputs, clangArgs[a + 1]);
        referenced = analysis.Undefined;
        weakOnly = analysis.WeakOnly;
        return MachoAutolink.Resolve(analysis.Options, SearchDirs(zigArgs, "-L"), SearchDirs(zigArgs, "-F"), zigArgs.ToList(), out missing, onlyNeeded ? analysis.Undefined : null);
    }

    static List<string> SearchDirs(IReadOnlyList<string> args, string flag)
    {
        var dirs = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == flag && i + 1 < args.Count)
                dirs.Add(args[++i]);
            else if (args[i].StartsWith(flag, StringComparison.Ordinal) && args[i].Length > flag.Length)
                dirs.Add(args[i][flag.Length..]);
        }
        return dirs;
    }

    static int RunZig(ZigInvocation inv)
    {
        var sources = (inv.Sections ?? []).Select(ClangToZig.SectionAssembly).Concat(inv.ExtraAssembly).ToList();
        if (sources.Count == 0 && inv.ZigLibc is null)
            return Exec(Zig(), [inv.Driver, .. inv.Args]);

        // Side objects for what zig's linker can't do itself (-sectcreate, toolchain-only
        // libraries), and the libc installation file for stub links.
        var linkArgs = inv.Args.ToList();
        var dir = Directory.CreateTempSubdirectory("aotanywhere-link-").FullName;
        try
        {
            for (int i = 0; i < sources.Count; i++)
            {
                string asm = Path.Combine(dir, $"extra{i}.s"), obj = Path.Combine(dir, $"extra{i}.o");
                File.WriteAllText(asm, sources[i]);
                int rc = Exec(Zig(), ["cc", ClangToZig.TargetOf(inv), "-c", asm, "-o", obj]);
                if (rc != 0)
                    return rc;
                linkArgs.Add(obj);
            }
            Dictionary<string, string?>? env = null;
            if (inv.ZigLibc is not null)
            {
                string libc = Path.Combine(dir, "libc.txt");
                File.WriteAllText(libc, inv.ZigLibc);
                env = new() { ["ZIG_LIBC"] = libc };
            }
            if (!inv.FlatObjCClasses)
                return Exec(Zig(), [inv.Driver, .. linkArgs], env);

            // Stub links leave Objective-C classes undefined for dyld's flat lookup. zig can only
            // allow that for all undefined symbols at once (no -U), so link normally first and
            // retry with -undefined dynamic_lookup only if classes are all that is missing - any
            // other undefined symbol still fails the link.
            var errors = new System.Text.StringBuilder();
            int first = Exec(Zig(), [inv.Driver, .. linkArgs], env, errors);
            if (first == 0)
                return 0;
            if (!ClangToZig.OnlyObjCClassesUndefined(errors.ToString()))
            {
                Console.Error.Write(errors);
                return first;
            }
            return Exec(Zig(), [inv.Driver, .. linkArgs, "-Wl,-undefined,dynamic_lookup"], env);
        }
        finally
        {
            if (!Debug)
                Directory.Delete(dir, recursive: true);
        }
    }

    // install_name_tool [-id <name>] [-change <old> <new>]... <file>
    static int InstallNameTool(List<string> args)
    {
        string? id = null;
        var changes = new List<(string, string)>();
        string? file = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-id":
                    id = NextArg(args, ref i, "-id");
                    break;
                case "-change":
                    changes.Add((NextArg(args, ref i, "-change"), NextArg(args, ref i, "-change")));
                    break;
                default:
                    if (args[i].StartsWith('-'))
                        throw new AppleToolException($"AotAnywhere: install_name_tool option '{args[i]}' is not supported.");
                    file = args[i];
                    break;
            }
        }
        if (file is null)
            throw new AppleToolException("AotAnywhere: install_name_tool was called without a file.");
        File.WriteAllBytes(file, MachoInstallName.Edit(File.ReadAllBytes(file), id, changes));
        return 0;
    }

    // codesign [-v] [--force] [--timestamp[=none]] --sign <identity> [--entitlements <file>]
    //          [--generate-entitlement-der] [--keychain <kc>] [--allocate <path>] <path>...
    // "-" signs ad-hoc (simulator builds). Any other identity is the certificate in the .p12
    // the Apple package's targets point AOTANYWHERE_SIGN_P12 at (there is no keychain off-Mac).
    static int Codesign(List<string> args)
    {
        string? identity = null, entitlementsFile = null;
        var paths = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-s" or "--sign":
                    identity = NextArg(args, ref i, a);
                    break;
                case "--entitlements":
                    entitlementsFile = NextArg(args, ref i, a);
                    break;
                // Paths to keychain / codesign_allocate: meaningless here.
                case "--keychain" or "--allocate" or "--options" or "-o":
                    NextArg(args, ref i, a);
                    break;
                // The DER entitlements are always generated (iOS 15+ requires them).
                case "-v" or "-f" or "--force" or "--generate-entitlement-der" or "--timestamp" or "--timestamp=none" or "--preserve-metadata=entitlements":
                    break;
                default:
                    if (a.StartsWith('-'))
                        throw new AppleToolException($"AotAnywhere: codesign option '{a}' is not supported.");
                    paths.Add(a);
                    break;
            }
        }
        if (identity is null)
            throw new AppleToolException("AotAnywhere: codesign was called without --sign.");

        SigningIdentity? signer = identity == "-" ? null : SigningIdentities.Load(identity);
        byte[]? entitlements = entitlementsFile is null ? null : File.ReadAllBytes(entitlementsFile);

        foreach (var path in paths)
        {
            string full = Path.TrimEndingDirectorySeparator(path);
            if (Directory.Exists(full))
            {
                BundleSigner.Sign(full, signer, entitlements);
            }
            else
            {
                var signed = MachoSigner.Sign(File.ReadAllBytes(full), new SignOptions(Path.GetFileNameWithoutExtension(full), signer, entitlements));
                File.WriteAllBytes(full, signed);
            }
            Console.Error.WriteLine($"{path}: signed {(signer is null ? "ad-hoc" : "with " + signer.CommonName)}");
        }
        return 0;
    }

    // import-xcode (--xip <Xcode.xip> | --from <Xcode.app>) --out <dir>
    static int ImportXcode(List<string> args)
    {
        string? xip = null, from = null, output = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--xip": xip = NextArg(args, ref i, "--xip"); break;
                case "--from": from = NextArg(args, ref i, "--from"); break;
                case "--out": output = NextArg(args, ref i, "--out"); break;
                default: throw new AppleToolException($"AotAnywhere: unknown import-xcode option '{args[i]}'.");
            }
        }
        if (output is null || (xip is null) == (from is null))
            throw new AppleToolException("usage: import-xcode (--xip <Xcode.xip> | --from <Xcode.app>) --out <dir>");

        var sw = Stopwatch.StartNew();
        if (xip is not null)
        {
            Console.WriteLine($"AotAnywhere: importing the iOS SDKs from {xip} into {output} (this decompresses the whole archive and takes a few minutes)...");
            XcodeImporter.ImportXip(xip, output, Console.WriteLine);
        }
        else
        {
            Console.WriteLine($"AotAnywhere: importing the iOS SDKs from {from} into {output}...");
            XcodeImporter.ImportDirectory(from!, output, Console.WriteLine);
        }
        Console.WriteLine($"AotAnywhere: done in {sw.Elapsed:m\\:ss}.");
        return 0;
    }

    // synthetic-xcode --out <Xcode.app> --xcode-version <x.y> --sdk-version <x.y>
    static int SyntheticXcodeTool(List<string> args)
    {
        string? output = null, xcode = null, sdk = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--out": output = NextArg(args, ref i, "--out"); break;
                case "--xcode-version": xcode = NextArg(args, ref i, "--xcode-version"); break;
                case "--sdk-version": sdk = NextArg(args, ref i, "--sdk-version"); break;
                default: throw new AppleToolException($"AotAnywhere: unknown synthetic-xcode option '{args[i]}'.");
            }
        }
        if (output is null || xcode is null || sdk is null)
            throw new AppleToolException("usage: synthetic-xcode --out <Xcode.app> --xcode-version <x.y> --sdk-version <x.y>");
        SyntheticXcode.Write(output, xcode, sdk);
        return 0;
    }

    // objc-shims --out <dir> (<assembly> | @<file listing assemblies>)...
    static int ObjCShimsTool(List<string> args)
    {
        string? output = null;
        var assemblies = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--out")
                output = NextArg(args, ref i, "--out");
            else if (args[i].StartsWith('@'))
                assemblies.AddRange(File.ReadAllLines(args[i][1..]).Where(l => l.Trim().Length > 0));
            else
                assemblies.Add(args[i]);
        }
        if (output is null || assemblies.Count == 0)
            throw new AppleToolException("usage: objc-shims --out <dir> (<assembly> | @<file>)...");
        var r = ObjCShims.Generate(assemblies, output);
        Console.WriteLine($"AotAnywhere: Objective-C declarations for {r.Classes} classes, {r.Protocols} protocols and {r.Structs} structs written to {output}.");
        return 0;
    }

    // Symbols are stripped at link time instead (Apple's strip rejects zig-linked
    // binaries), so the post-link strip has nothing left to do.
    static int Strip(List<string> args) => 0;

    // dSYM bundles need Apple's dsymutil; off-Mac builds don't produce them (the
    // Apple package sets NoDSymUtil=true). `--help` is the ILCompiler targets' probe.
    static int DSymUtil(List<string> args)
    {
        if (args is ["--help"])
            return Print("OVERVIEW: AotAnywhere dsymutil stand-in (no dSYM generation off-Mac)");
        Console.Error.WriteLine("AotAnywhere: dSYM generation is not supported off-Mac; no .dSYM was produced.");
        return 0;
    }

    static string DeveloperDir() =>
        Environment.GetEnvironmentVariable("DEVELOPER_DIR") is { Length: > 0 } d
            ? d
            : throw new AppleToolException("AotAnywhere: DEVELOPER_DIR is not set, so the Xcode location is unknown.");

    // The directory of generated PATH wrappers (one per tool name).
    static string ToolsBin() =>
        Environment.GetEnvironmentVariable("AOTANYWHERE_XCODE_BIN") is { Length: > 0 } b
            ? b
            : throw new AppleToolException("AotAnywhere: AOTANYWHERE_XCODE_BIN is not set.");

    static string Zig() =>
        Environment.GetEnvironmentVariable("AOTANYWHERE_ZIG") is { Length: > 0 } z
            ? z
            : throw new AppleToolException("AotAnywhere: AOTANYWHERE_ZIG is not set, so zig cannot be found.");

    // "<sdks>/iPhoneOS26.5.sdk" -> "26.5"
    static string SdkVersion(string sdkPath, string platform) =>
        Path.GetFileNameWithoutExtension(sdkPath).Substring(platform.Length);

    static string NextArg(List<string> args, ref int i, string flag) =>
        i + 1 < args.Count ? args[++i] : throw new AppleToolException($"AotAnywhere: '{flag}' is missing its value.");

    static int Print(string s)
    {
        Console.WriteLine(s);
        return 0;
    }

    // AOTANYWHERE_APPLE_DEBUG=1: log the commands run and keep per-link temporary files.
    static readonly bool Debug = Environment.GetEnvironmentVariable("AOTANYWHERE_APPLE_DEBUG") is "1" or "true";

    static int Exec(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?>? environment = null, System.Text.StringBuilder? stderr = null)
    {
        if (Debug)
            Console.Error.WriteLine("AotAnywhere: " + string.Join(' ', (environment ?? new Dictionary<string, string?>()).Select(e => $"{e.Key}={e.Value}").Append(fileName).Concat(arguments).Select(a => a.Contains(' ') ? $"'{a}'" : a)));
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardError = stderr is not null };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in environment ?? new Dictionary<string, string?>())
        {
            if (v is null)
                psi.Environment.Remove(k);
            else
                psi.Environment[k] = v;
        }
        using var p = Process.Start(psi) ?? throw new AppleToolException($"AotAnywhere: failed to start '{fileName}'.");
        if (stderr is not null)
            stderr.Append(p.StandardError.ReadToEnd());
        p.WaitForExit();
        return p.ExitCode;
    }
}
