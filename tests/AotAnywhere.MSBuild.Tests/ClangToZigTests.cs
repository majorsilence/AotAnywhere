using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// The command lines below are trimmed copies of what the net10.0-ios workload's
// CompileNativeCode / LinkNativeCode tasks pass to `xcrun clang`. The full
// translation was validated end-to-end by running a zig-linked app in the iOS
// simulator; these pin the individual rewrites.
public class ClangToZigTests
{
    const string Sdk = "/X/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/SDKs/iPhoneSimulator26.5.sdk";

    static readonly string[] SimCompileMm =
    [
        "-g", "-miphonesimulator-version-min=15.0", "-arch", "arm64", "-isysroot", Sdk,
        "-I/packs/runtimes/iossimulator-arm64/native/", "-std=c++14",
        "-o", "obj/nativelibraries/registrar.o", "-c", "obj/linker-cache/registrar.mm",
    ];

    [Test]
    public async Task SimulatorCompileOfObjCxxUsesTheSdkLibcxxHeaders()
    {
        var inv = ClangToZig.Translate("clang", SimCompileMm);

        await Assert.That(inv.Driver).IsEqualTo("c++");
        await Assert.That(inv.Args[0]).IsEqualTo("--target=aarch64-ios.15.0-simulator");
        await Assert.That(inv.Args).Contains("-fno-sanitize=undefined");
        await Assert.That(inv.Args).Contains($"-F{Sdk}/System/Library/SubFrameworks");
        await Assert.That(inv.Args).DoesNotContain("-isysroot");
        await Assert.That(inv.Args).DoesNotContain("-arch");

        // libc++'s headers must come before the C headers.
        int cxx = inv.Args.ToList().IndexOf($"-I{Sdk}/usr/include/c++/v1");
        int c = inv.Args.ToList().IndexOf($"{Sdk}/usr/include");
        await Assert.That(inv.Args).Contains("-nostdinc++");
        await Assert.That(cxx).IsGreaterThanOrEqualTo(0);
        await Assert.That(cxx).IsLessThan(c);

        // Everything else passes through untouched, in order.
        await Assert.That(inv.Args.TakeLast(6)).IsEquivalentTo(
            new[] { "-I/packs/runtimes/iossimulator-arm64/native/", "-std=c++14", "-o", "obj/nativelibraries/registrar.o", "-c", "obj/linker-cache/registrar.mm" });
    }

    [Test]
    public async Task DeviceCompileOfCSourceUsesZigCc()
    {
        var inv = ClangToZig.Translate("clang", ["-miphoneos-version-min=17.0", "-arch", "arm64", "-isysroot", Sdk, "-c", "a.m", "-o", "a.o"]);

        await Assert.That(inv.Driver).IsEqualTo("cc");
        await Assert.That(inv.Args[0]).IsEqualTo("--target=aarch64-ios.17.0");
        await Assert.That(inv.Args).DoesNotContain("-nostdinc++");
    }

    [Test]
    public async Task LinkMapsLibcxxToTheSdkAndLinkerFlagsToWl()
    {
        var inv = ClangToZig.Translate("clang++",
        [
            "-Lobj/nativelibraries", "-lSystem.Native", "-miphonesimulator-version-min=15.0", "-isysroot", Sdk, "-arch", "arm64",
            "-lxamarin-dotnet-nativeaot", "-rpath", "@executable_path", "-framework", "UIKit", "obj/native/App.o",
            "-o", "obj/nativelibraries/App", "-dead_strip", "-exported_symbols_list", "obj/mtouch-symbols.list",
            "-lstdc++", "-lobjc", "-lswiftCore", "-Wl,-framework,CryptoKit",
        ]);

        await Assert.That(inv.Driver).IsEqualTo("cc");
        await Assert.That(inv.Args).Contains("-Wl,-rpath,@executable_path");
        await Assert.That(inv.Args).Contains("-Wl,-dead_strip");
        await Assert.That(inv.Args).DoesNotContain("-rpath");
        await Assert.That(inv.Args).DoesNotContain("-exported_symbols_list");
        await Assert.That(inv.Args).DoesNotContain("obj/mtouch-symbols.list");
        await Assert.That(inv.Args).DoesNotContain("-lstdc++");
        await Assert.That(inv.Args.Count(a => a == $"{Sdk}/usr/lib/libc++.tbd")).IsEqualTo(1);
        await Assert.That(inv.Args).Contains($"-L{Sdk}/usr/lib");
        await Assert.That(inv.Args).Contains("-lSystem");
        // Swift overlays come from the autolink pass, and every named library stays linked (as with ld64).
        await Assert.That(inv.Args).Contains($"-L{Sdk}/usr/lib/swift");
        await Assert.That(inv.Args).DoesNotContain("-Wl,-dead_strip_dylibs");
        // Named both ways by the workload; linked once (dyld rejects duplicate load commands).
        await Assert.That(inv.Args).DoesNotContain("-Wl,-framework,CryptoKit");
        await Assert.That(inv.Args.Count(a => a == "CryptoKit")).IsEqualTo(1);
    }

    [Test]
    public async Task LinkNamesEachLibraryOnce()
    {
        var inv = ClangToZig.Translate("clang",
        [
            "-miphoneos-version-min=15.0", "-isysroot", Sdk, "-arch", "arm64", "a.o",
            "-framework", "Security", "-lz", "-framework", "Security", "-Wl,-framework,Security", "-lz", "-o", "a",
        ]);

        await Assert.That(inv.Args.Count(a => a == "Security")).IsEqualTo(1);
        await Assert.That(inv.Args.Count(a => a == "-lz")).IsEqualTo(1);
    }

    [Test]
    public async Task StubLinkUsesTheSysrootAndDropsXcodeSearchPaths()
    {
        const string xcode = "/X/Xcode.app/Contents/Developer";
        var inv = ClangToZig.Translate("clang++",
        [
            "-miphoneos-version-min=15.0", "-isysroot", Sdk, "-arch", "arm64", "a.o", "-lc++",
            $"-L{xcode}/Toolchains/XcodeDefault.xctoolchain/usr/lib/swift/iphoneos/", "-L", $"{xcode}/Platforms/x", "-Lobj/native",
            "-F", "obj/xcframework", "-o", "a",
        ], stubs: new LinkStubs("/pkg/sysroot", xcode, "/zig/include"));

        await Assert.That(inv.Args.Any(a => a.Contains(xcode))).IsFalse();
        await Assert.That(inv.Args.Any(a => a.Contains(Sdk))).IsFalse();
        await Assert.That(inv.Args).Contains("-Lobj/native");
        await Assert.That(inv.Args).Contains("obj/xcframework");
        await Assert.That(inv.Args).Contains("/pkg/sysroot/usr/lib/libc++.tbd");
        await Assert.That(inv.Args).Contains("-F/pkg/sysroot/System/Library/Frameworks");
        await Assert.That(inv.ZigLibc!).Contains("crt_dir=/pkg/sysroot/usr/lib");
        await Assert.That(inv.ZigLibc!).Contains("darwin_sdk_dir=/pkg/sysroot");
    }

    [Test]
    public async Task LinkWithoutSwiftAddsNoOverlays()
    {
        var inv = ClangToZig.Translate("clang", ["-miphoneos-version-min=15.0", "-isysroot", Sdk, "-arch", "arm64", "a.o", "-o", "a"]);

        await Assert.That(inv.Args).DoesNotContain("-lswiftFoundation");
        await Assert.That(inv.Args).DoesNotContain($"{Sdk}/usr/lib/libc++.tbd");
    }

    [Test]
    [Arguments("-isysroot")]
    [Arguments("-arch")]
    [Arguments("-miphoneos-version-min=15.0")]
    public async Task MissingRequiredFlagIsAnError(string omitted)
    {
        var args = new List<string> { "-miphoneos-version-min=15.0", "-arch", "arm64", "-isysroot", Sdk, "-c", "a.m" };
        int at = args.IndexOf(omitted);
        args.RemoveRange(at, omitted.StartsWith("-m") ? 1 : 2);

        await Assert.That(() => ClangToZig.Translate("clang", args)).Throws<AppleToolException>();
    }

    [Test]
    public async Task NonIosDeploymentTargetIsAnError()
    {
        await Assert.That(() => ClangToZig.Translate("clang", ["-mtvos-version-min=15.0", "-arch", "arm64", "-isysroot", Sdk, "-c", "a.m"]))
            .Throws<AppleToolException>();
    }

    [Test]
    public async Task SectcreateBecomesASectionInputInsteadOfALinkerFlag()
    {
        var inv = ClangToZig.Translate("clang++",
        [
            "-miphonesimulator-version-min=15.0", "-arch", "arm64", "-isysroot", Sdk, "main.o",
            "-Xlinker", "-sectcreate", "-Xlinker", "__TEXT", "-Xlinker", "__entitlements", "-Xlinker", "/obj/App.xcent",
            "-o", "App",
        ]);

        await Assert.That(inv.Args).DoesNotContain("-sectcreate");
        await Assert.That(inv.Args).DoesNotContain("-Xlinker");
        await Assert.That(inv.Sections!).IsEquivalentTo(new[] { new SectionInput("__TEXT", "__entitlements", "/obj/App.xcent") });
        // Kept by -dead_strip, like ld64's -sectcreate sections.
        await Assert.That(ClangToZig.SectionAssembly(inv.Sections![0]))
            .IsEqualTo(".section __TEXT,__entitlements,regular,no_dead_strip\n.incbin \"/obj/App.xcent\"\n");
        await Assert.That(ClangToZig.TargetOf(inv)).IsEqualTo("--target=aarch64-ios.15.0-simulator");
    }

    [Test]
    public async Task MissingSwiftBackDeploymentLibrariesWarnBelowTheirCutoff()
    {
        var below = ClangToZig.BackDeploymentWarnings(["swiftCompatibility56", "swiftCompatibilityConcurrency"], "--target=aarch64-ios.15.0-simulator").ToList();
        await Assert.That(below.Count).IsEqualTo(1);
        await Assert.That(below[0]).Contains("swiftCompatibility56");
        await Assert.That(ClangToZig.BackDeploymentWarnings(["swiftCompatibility56"], "--target=aarch64-ios.16.0").Any()).IsFalse();
        // A library without a known cutoff always warns.
        await Assert.That(ClangToZig.BackDeploymentWarnings(["swiftCompatibility50"], "--target=aarch64-ios.18").Count()).IsEqualTo(1);
    }
}
