using System.Diagnostics;
using AotAnywhere.AppleTools;

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
        "signing-identity" => SigningIdentityTool(args),
        "strip" => Strip(args),
        "dsymutil" => DSymUtil(args),
        "objc-shims" => ObjCShimsTool(args),
        "synthetic-xcode" => SyntheticXcodeTool(args),
        "ditto" => RunDitto(args),
        "ibtool" => IbTool(args),
        "actool" => AcTool(args),
        "car-dump" => Print(CarWriter.Dump(File.ReadAllBytes(args[0]))),
        "nib-dump" => Print(NibArchive.Read(File.ReadAllBytes(args[0])).Dump()),
        // Spotlight indexing of the build output; meaningless off-Mac.
        "mdimport" => 0,
        // On Windows, MSBuild's Exec runs cmd.exe, which has no `command`; the ILCompiler
        // targets probe for xcrun with `command -v xcrun`, so the wrapper dir provides one.
        "command" => CommandV(args),
        // The .ipa: `zip -r -y` (macOS/Linux), or a mode fix-up of the Windows-made archive.
        "zip" => Done(() => ZipTool.Run(args)),
        "ipa-modes" => Done(() => ZipTool.FixModes(args.Single())),
        // On a Mac with Xcode (AotAnywhereAppleForce), defer anything not reimplemented here.
        _ when OperatingSystem.IsMacOS() && File.Exists(AppleXcrun) => Exec(AppleXcrun, [tool, .. args], AppleToolEnvironment()),
        _ => throw new AppleToolException($"AotAnywhere: the Xcode tool '{tool}' is not available off-Mac. If the project uses a feature that needs it (Metal shaders, Core ML models, SceneKit assets, ...), it can only be built on a Mac with Xcode."),
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
            shims = new HeaderShims(shimDir, Path.Combine(ZigLibDir(), "libc", "include", "any-darwin-any"));
            // Non-umbrella framework headers (<CoreImage/CIFilterBuiltins.h>) are only known
            // once the generated sources exist.
            foreach (var source in args.Where(a => !a.StartsWith('-') && File.Exists(a) && Path.GetExtension(a) is ".m" or ".mm" or ".c" or ".cpp"))
                foreach (var header in ObjCShims.FrameworkImports(source))
                    ObjCShims.EnsureHeader(shimDir, header);
        }

        // Set when linking against AotAnywhere's stubs (AotAnywhereAppleLinkStubs) instead of the SDK.
        LinkStubs? stubs = Environment.GetEnvironmentVariable("AOTANYWHERE_APPLE_SYSROOT") is { Length: > 0 } sysroot
            ? new LinkStubs(sysroot, Environment.GetEnvironmentVariable("DEVELOPER_DIR"), Path.Combine(ZigLibDir(), "libc", "include", "any-darwin-any"))
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

    // signing-identity --p12 <file> [--password-file <file>] --profile <file.mobileprovision> --bundle-id <id> --out <dir>
    // Stands in for the workload's DetectSigningIdentity (Keychain + ~/Library profile lookup):
    // checks that the profile covers the bundle ID and allows the .p12's certificate, copies it to
    // <dir>/<UUID>.mobileprovision, and prints "thumbprint|profile path without extension|app ID|
    // distribution type|certificate name|profile name" for the targets. (The workload looks a profile up as
    // Path.Combine(profileDir, name + ".mobileprovision"), so a rooted path works as the name.)
    static int SigningIdentityTool(List<string> args)
    {
        string? p12 = null, passwordFile = null, profilePath = null, bundleId = null, outDir = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--p12": p12 = NextArg(args, ref i, args[i]); break;
                case "--password-file": passwordFile = NextArg(args, ref i, args[i]); break;
                case "--profile": profilePath = NextArg(args, ref i, args[i]); break;
                case "--bundle-id": bundleId = NextArg(args, ref i, args[i]); break;
                case "--out": outDir = NextArg(args, ref i, args[i]); break;
                default: throw new AppleToolException($"AotAnywhere: signing-identity: unknown argument '{args[i]}'.");
            }
        }
        if (p12 is null || profilePath is null || bundleId is null || outDir is null)
            throw new AppleToolException("usage: signing-identity --p12 <file> [--password-file <file>] --profile <file> --bundle-id <id> --out <dir>");

        var identity = SigningIdentities.LoadP12(p12, passwordFile);
        var profile = Provisioning.Load(File.ReadAllBytes(profilePath));
        var now = DateTime.UtcNow;
        if (identity.Certificate.NotAfter.ToUniversalTime() < now)
            throw new AppleToolException($"AotAnywhere: the signing certificate '{identity.CommonName}' expired on {identity.Certificate.NotAfter:yyyy-MM-dd}.");
        if (profile.Expiration < now)
            throw new AppleToolException($"AotAnywhere: the provisioning profile '{profile.Name}' expired on {profile.Expiration:yyyy-MM-dd}.");
        if (profile.Platforms.Count > 0 && !profile.Platforms.Contains("iOS"))
            throw new AppleToolException($"AotAnywhere: the provisioning profile '{profile.Name}' is for {string.Join(", ", profile.Platforms)}, not iOS.");
        if (!profile.Allows(identity.Certificate))
            throw new AppleToolException($"AotAnywhere: the provisioning profile '{profile.Name}' does not include the certificate '{identity.CommonName}' from {p12}. Download a profile that lists it, or export the matching certificate.");
        string appId = profile.AppIdFor(bundleId)
            ?? throw new AppleToolException($"AotAnywhere: the provisioning profile '{profile.Name}' (app ID {profile.ApplicationIdentifier}) does not cover the bundle identifier '{bundleId}'.");

        Directory.CreateDirectory(outDir);
        string copy = Path.Combine(Path.GetFullPath(outDir), profile.Uuid + ".mobileprovision");
        File.Copy(profilePath, copy, overwrite: true);
        // One line on stdout only (the targets parse it); nothing on stderr.
        return Print(string.Join('|', identity.Certificate.Thumbprint, Path.ChangeExtension(copy, null), appId, profile.DistributionType, identity.CommonName, profile.Name));
    }

    // ibtool [--errors --warnings --notices --output-format xml1 ...] (--compilation-directory <dir> | --compile <path>) <input>...
    // Prints the result plist the workload's IBTool task parses.
    static int IbTool(List<string> args)
    {
        string? directory = null, compileTo = null, link = null;
        var inputs = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--compilation-directory": directory = NextArg(args, ref i, args[i]); break;
                case "--compile": compileTo = NextArg(args, ref i, args[i]); break;
                case "--output-format" or "--minimum-deployment-target" or "--target-device" or "--sdk" or "--module"
                    or "--output-partial-info-plist" or "--target-device-family" or "--platform":
                    NextArg(args, ref i, args[i]); break;
                case "--link": link = NextArg(args, ref i, args[i]); break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        break; // --errors, --warnings, --notices, --auto-activate-custom-fonts, ...
                    inputs.Add(args[i]);
                    break;
            }
        }

        var errors = new List<object>();
        // --link: the workload compiles each storyboard on its own, then links them into one folder.
        // Linking merges storyboards that reference each other (storyboard references), which isn't
        // supported, so it is a copy of each compiled storyboard.
        if (link is not null)
        {
            foreach (var input in inputs)
            {
                if (!Directory.Exists(input) || !input.TrimEnd('/', '\\').EndsWith(".storyboardc", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new Dictionary<string, object>(StringComparer.Ordinal) { ["description"] = $"{input}: ibtool --link expects compiled storyboards" });
                    continue;
                }
                string target = Path.Combine(link, Path.GetFileName(input.TrimEnd('/', '\\')));
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                Ditto.Run([input.TrimEnd('/', '\\') + "/", target]);
            }
        }
        else
        foreach (var input in inputs)
        {
            try
            {
                string outDir = directory ?? Path.GetDirectoryName(Path.GetFullPath(compileTo ?? input))!;
                var written = InterfaceBuilder.Compile(input, outDir);
                // --compile names the output itself.
                if (compileTo is not null && directory is null && written.Count == 1 && !string.Equals(written[0], Path.GetFullPath(compileTo), StringComparison.Ordinal))
                    File.Move(written[0], compileTo, overwrite: true);
            }
            catch (Exception e) when (e is InterfaceBuilder.CompileError or System.Xml.XmlException)
            {
                errors.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["description"] = $"{input}: {e.Message} (see docs/ios-targets.md for what AotAnywhere's ibtool supports; build on a Mac with Xcode for anything else)",
                });
            }
        }

        var result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["com.apple.ibtool.document.errors"] = new Dictionary<string, object>(StringComparer.Ordinal),
            ["com.apple.ibtool.document.notices"] = new Dictionary<string, object>(StringComparer.Ordinal),
            ["com.apple.ibtool.document.warnings"] = new Dictionary<string, object>(StringComparer.Ordinal),
        };
        if (errors.Count > 0)
            result["com.apple.ibtool.errors"] = errors;
        Console.Out.Write(System.Text.Encoding.UTF8.GetString(Plist.WriteXml(result)));
        return errors.Count > 0 ? 1 : 0;
    }

    // actool [--errors --warnings --notices --output-format xml1 ...] --compile <dir> [--app-icon <name>]
    //        [--target-device <idiom>]... [--output-partial-info-plist <path>] <catalog.xcassets>...
    // Prints the result plist the workload's ACTool task parses.
    static int AcTool(List<string> args)
    {
        string? compile = null, appIcon = null, partialInfo = null;
        var devices = new List<string>();
        var catalogs = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--compile": compile = NextArg(args, ref i, args[i]); break;
                case "--app-icon": appIcon = NextArg(args, ref i, args[i]); break;
                case "--target-device": devices.Add(NextArg(args, ref i, args[i])); break;
                case "--output-partial-info-plist": partialInfo = NextArg(args, ref i, args[i]); break;
                case "--output-format" or "--minimum-deployment-target" or "--platform" or "--product-type" or "--launch-image"
                    or "--filter-for-device-model" or "--filter-for-device-os-version" or "--asset-pack-output-specifications"
                    or "--development-region" or "--optimization":
                    NextArg(args, ref i, args[i]); break;
                case "--enable-on-demand-resources":
                    if (i + 1 < args.Count && args[i + 1] is "YES" or "NO")
                        i++;
                    break;
                case "--accent-color" or "--alternate-app-icon" or "--complication" or "--sticker-pack-identifier-prefix" or "--widget-background-color":
                    return AcToolResult([], [$"AotAnywhere's actool does not support {args[i]}; build on a Mac with Xcode for that."]);
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        break; // --errors, --warnings, --notices, --compress-pngs, --include-all-app-icons, ...
                    catalogs.Add(args[i]);
                    break;
            }
        }
        if (compile is null)
            throw new AppleToolException("AotAnywhere: actool needs --compile <directory>.");

        try
        {
            return AcToolResult(AssetCatalog.Compile(catalogs, new AssetCatalog.Options(compile, appIcon, devices, partialInfo)), []);
        }
        catch (AssetCatalog.CompileError e)
        {
            return AcToolResult([], [$"{e.Message} (AotAnywhere's actool compiles app icons and PNG image sets only; build on a Mac with Xcode for anything else)"]);
        }
    }

    static int AcToolResult(List<string> outputs, List<string> errors)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["com.apple.actool.compilation-results"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["output-files"] = outputs.Select(Path.GetFullPath).Cast<object>().ToList(),
            },
        };
        if (errors.Count > 0)
            result["com.apple.actool.errors"] = errors.Select(e => (object)new Dictionary<string, object>(StringComparer.Ordinal) { ["description"] = e }).ToList();
        Console.Out.Write(System.Text.Encoding.UTF8.GetString(Plist.WriteXml(result)));
        return errors.Count > 0 ? 1 : 0;
    }

    static int RunDitto(List<string> args)
    {
        Ditto.Run(args);
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

    // zig's lib directory, as zig reports it: a zig from PATH (UseExternalZig) has no directory in
    // AOTANYWHERE_ZIG, and packaged zigs (Homebrew, distributions) keep lib somewhere else.
    static string ZigLibDir()
    {
        string zig = Zig();
        try
        {
            using var p = Process.Start(new ProcessStartInfo(zig, "env") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var m = System.Text.RegularExpressions.Regex.Match(output, "\\.lib_dir = \"((?:[^\"\\\\]|\\\\.)*)\"");
            if (p.ExitCode == 0 && m.Success)
                return m.Groups[1].Value.Replace("\\\\", "\\");
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        return Path.Combine(Path.GetDirectoryName(zig)!, "lib");
    }

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

    static int Done(Action action)
    {
        action();
        return 0;
    }

    // command -v <name>: print where <name> resolves on PATH (with PATHEXT on Windows); exit 1 if nowhere.
    static int CommandV(List<string> args)
    {
        if (args is not ["-v", var name])
            throw new AppleToolException("AotAnywhere: only `command -v <name>` is supported.");
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend("")
            : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in extensions)
            {
                string candidate = Path.Combine(dir.Trim('"'), name + ext);
                if (File.Exists(candidate))
                    return Print(candidate);
            }
        return 1;
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
