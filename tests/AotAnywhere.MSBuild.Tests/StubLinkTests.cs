using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// The stub link reproduces ld64's library choice against the SDK. Validated end to end on MAUI
// (device and simulator) and Avalonia (simulator): the load commands - libraries, versions,
// weak flags - are identical to Apple's own link, and every import shared by both binaries
// binds to the same library. These pin the individual rules.
public class StubLinkTests
{
    static readonly string[] Device = ["--target=aarch64-ios.15.0", "main.o", "-o", "app"];
    static readonly string[] Simulator17 = ["--target=aarch64-ios.17.0-simulator", "main.o", "-o", "app"];

    sealed class Sysroot
    {
        public readonly string Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aotanywhere-sysroot-" + Guid.NewGuid().ToString("N"))).FullName;
        public readonly string LinkRoot = Path.Combine(Path.GetTempPath(), "aotanywhere-link-" + Guid.NewGuid().ToString("N"));

        public void Lib(string name, string installName, string[] own, string[]? reexported = null, string? currentVersion = null)
        {
            var path = Path.Combine(Root, "usr", "lib", name + ".tbd");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Tbd.Write(installName, StubLink.Targets,
                new Dictionary<string, IReadOnlyCollection<string>> { ["symbols"] = own }, currentVersion,
                reexported: reexported is null ? null : new Dictionary<string, IReadOnlyCollection<string>> { ["symbols"] = reexported }));
        }

        public List<string> Prepare(string[] target, string[] libs, int autolinkStart = int.MaxValue, string[]? referenced = null, string[]? weakOnly = null) =>
            StubLink.Prepare([.. target, $"-L{Root}/usr/lib", .. libs], Root, LinkRoot, autolinkStart + target.Length + 1,
                referenced?.ToHashSet(), weakOnly?.ToHashSet());

        // What the per-link stub for a library exports.
        public HashSet<string> Exports(string name) =>
            Tbd.Parse(File.ReadAllText(Path.Combine(LinkRoot, "usr", "lib", name + ".tbd")))[0].Exports.Keys.ToHashSet();

        public TbdLibrary Stub(string name) => Tbd.Parse(File.ReadAllText(Path.Combine(LinkRoot, "usr", "lib", name + ".tbd")))[0];
    }

    [Test]
    public async Task TheImplementingLibraryWinsOverAnEarlierReexporter()
    {
        var s = new Sysroot();
        s.Lib("libCoreFoundation", "/System/CoreFoundation", ["_CFRetain"], reexported: ["_objc_alloc"]);
        s.Lib("libobjc", "/usr/lib/libobjc.A.dylib", ["_objc_alloc"]);

        s.Prepare(Device, ["-lCoreFoundation", "-lobjc"]);

        await Assert.That(s.Exports("libobjc")).Contains("_objc_alloc");
        await Assert.That(s.Exports("libCoreFoundation")).DoesNotContain("_objc_alloc");
    }

    [Test]
    public async Task AReexporterProvidesWhatNoLinkedLibraryImplements()
    {
        var s = new Sysroot();
        s.Lib("libswiftDarwin", "/usr/lib/swift/libswiftDarwin.dylib", [], reexported: ["_floatThing"]);

        s.Prepare(Device, ["-lswiftDarwin"]);

        await Assert.That(s.Exports("libswiftDarwin")).Contains("_floatThing");
    }

    [Test]
    public async Task LdPreviousMovesASymbolToItsOlderLibraryForOlderDeploymentTargets()
    {
        // On iOS 12.2-16.0, NSKeyValueObservation lived in libswiftFoundation.
        const string sym = "_$s10Foundation21NSKeyValueObservationCMn";
        var s = new Sysroot();
        s.Lib("libFoundation", "/System/Foundation", [sym, $"$ld$previous$/usr/lib/swift/libswiftFoundation.dylib$1.0.0$2$12.2$16.0${sym}$"]);
        s.Lib("libswiftFoundation", "/usr/lib/swift/libswiftFoundation.dylib", [], reexported: [sym]);

        s.Prepare(Device, ["-lFoundation", "-lswiftFoundation"]);
        await Assert.That(s.Exports("libswiftFoundation")).Contains(sym);
        await Assert.That(s.Exports("libFoundation")).DoesNotContain(sym);

        // Not for an iOS 17 deployment target - nor for a simulator target (a different platform).
        var s17 = new Sysroot();
        s17.Lib("libFoundation", "/System/Foundation", [sym, $"$ld$previous$/usr/lib/swift/libswiftFoundation.dylib$1.0.0$2$12.2$16.0${sym}$"]);
        s17.Lib("libswiftFoundation", "/usr/lib/swift/libswiftFoundation.dylib", [], reexported: [sym]);
        s17.Prepare(Simulator17, ["-lFoundation", "-lswiftFoundation"]);
        await Assert.That(s17.Exports("libFoundation")).Contains(sym);
    }

    [Test]
    public async Task LdPreviousToAnUnlinkedLibraryAddsIt()
    {
        const string sym = "_$s12CoreGraphics7CGFloatVMn";
        var s = new Sysroot();
        s.Lib("libswiftCoreFoundation", "/usr/lib/swift/libswiftCoreFoundation.dylib",
            [sym, $"$ld$previous$/usr/lib/swift/libswiftCoreGraphics.dylib$$2$12.2$16.0${sym}$"], currentVersion: "120.100");

        var args = s.Prepare(Device, ["-lswiftCoreFoundation"], referenced: [sym]);

        var added = args.Last();
        await Assert.That(added).EndsWith(".tbd");
        var stub = Tbd.Parse(File.ReadAllText(added))[0];
        await Assert.That(stub.InstallName).IsEqualTo("/usr/lib/swift/libswiftCoreGraphics.dylib");
        await Assert.That(stub.Exports.Keys).Contains(sym);
        // ld64 gives it the version of the library the directive is in (written in three parts:
        // zig misreads two-part versions).
        await Assert.That(stub.CurrentVersion).IsEqualTo("120.100.0");
    }

    [Test]
    public async Task LdHideAndLdAddApplyToTheirExactOsVersion()
    {
        var s = new Sysroot();
        s.Lib("libSystem", "/usr/lib/libSystem.B.dylib", ["_kept", "_gone", "$ld$hide$os15.0$_gone", "$ld$add$os15.0$_extra", "$ld$add$os16.0$_later"]);

        s.Prepare(Device, ["-lSystem"]);

        await Assert.That(s.Exports("libSystem")).IsEquivalentTo(new[] { "_kept", "_extra" });
    }

    [Test]
    public async Task AliasesOfOneLibraryShareItsSymbolsAndAreLinkedOnce()
    {
        // libm.tbd and libdl.tbd are libSystem; zig takes libc from libSystem.tbd only.
        var s = new Sysroot();
        s.Lib("libm", "/usr/lib/libSystem.B.dylib", ["_cos"]);
        s.Lib("libSystem", "/usr/lib/libSystem.B.dylib", ["_cos"]);

        var args = s.Prepare(Device, ["-lm", "-lSystem"]);

        await Assert.That(s.Exports("libSystem")).Contains("_cos");
        await Assert.That(args.Count(a => a is "-lm" or "-lSystem")).IsEqualTo(1);
    }

    [Test]
    public async Task UnusedAutolinkedLibrariesAreDroppedAndWeakOnlyOnesLinkedWeakly()
    {
        var s = new Sysroot();
        s.Lib("libUIKit", "/System/UIKit", ["_UIApplicationMain"]);
        s.Lib("libswiftMetal", "/usr/lib/swift/libswiftMetal.dylib", ["__swift_FORCE_LOAD_$_swiftMetal"]);
        s.Lib("libswiftObservation", "/usr/lib/swift/libswiftObservation.dylib", ["_observe"]);

        var args = s.Prepare(Device, ["-lUIKit", "-lswiftMetal", "-lswiftObservation"], autolinkStart: 1,
            referenced: ["_UIApplicationMain", "__swift_FORCE_LOAD_$_swiftMetal"], weakOnly: ["__swift_FORCE_LOAD_$_swiftMetal"]);

        await Assert.That(args).Contains("-lUIKit");
        await Assert.That(args).Contains("-weak-lswiftMetal");
        await Assert.That(args.Any(a => a.Contains("Observation"))).IsFalse();
    }

    [Test]
    public async Task SysrootPathsPointAtThePerLinkStubs()
    {
        var s = new Sysroot();
        s.Lib("libSystem", "/usr/lib/libSystem.B.dylib", ["_malloc"]);

        var args = s.Prepare(Device, ["-lSystem"]);

        await Assert.That(args).Contains($"-L{s.LinkRoot}/usr/lib");
        await Assert.That(args.Any(a => a.Contains(s.Root))).IsFalse();
    }

    [Test]
    public async Task TbdRoundTripsExportsReexportsAndObjCClasses()
    {
        string text = Tbd.Write("/System/Library/Frameworks/UIKit.framework/UIKit", StubLink.Targets,
            new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["symbols"] = ["_UIApplicationMain", "_$sweird'name"],
                ["objc-classes"] = ["_OBJC_CLASS_$_UIView", "_OBJC_METACLASS_$_UIView"],
            },
            currentVersion: "9127.0.84",
            reexported: new Dictionary<string, IReadOnlyCollection<string>> { ["symbols"] = ["_objc_alloc"] });

        var lib = Tbd.Parse(text).Single();

        await Assert.That(lib.InstallName).IsEqualTo("/System/Library/Frameworks/UIKit.framework/UIKit");
        await Assert.That(lib.CurrentVersion).IsEqualTo("9127.0.84");
        await Assert.That(lib.Exports.Keys).IsEquivalentTo(new[]
            { "_UIApplicationMain", "_$sweird'name", "_OBJC_CLASS_$_UIView", "_OBJC_METACLASS_$_UIView", "_objc_alloc" });
        await Assert.That(lib.ReexportedSymbols).IsEquivalentTo(new[] { "_objc_alloc" });
    }

    [Test]
    public async Task TbdParseIgnoresUndefinedsAndReadsInlinedDocumentsAndReexportedLibraries()
    {
        const string text = """
            --- !tapi-tbd
            tbd-version:     4
            targets:         [ arm64-ios ]
            install-name:    '/System/Library/Frameworks/UIKit.framework/UIKit'
            reexported-libraries:
              - targets:         [ arm64-ios ]
                libraries:       [ '/System/Library/PrivateFrameworks/UIKitCore.framework/UIKitCore',
                                   '/usr/lib/libobjc.A.dylib' ]
            exports:
              - targets:         [ arm64-ios ]
                symbols:         [ _UIKitOwn ]
            undefineds:
              - targets:         [ arm64-ios ]
                symbols:         [ _malloc ]
            --- !tapi-tbd
            tbd-version:     4
            targets:         [ arm64-ios ]
            install-name:    '/System/Library/PrivateFrameworks/UIKitCore.framework/UIKitCore'
            exports:
              - targets:         [ arm64-ios ]
                objc-classes:    [ UIView ]
            ...
            """;

        var docs = Tbd.Parse(text);

        await Assert.That(docs.Count).IsEqualTo(2);
        await Assert.That(docs[0].Exports.Keys).IsEquivalentTo(new[] { "_UIKitOwn" });
        await Assert.That(docs[0].ReexportedLibraries).IsEquivalentTo(new[]
            { "/System/Library/PrivateFrameworks/UIKitCore.framework/UIKitCore", "/usr/lib/libobjc.A.dylib" });
        await Assert.That(docs[1].Exports.Keys).IsEquivalentTo(new[] { "_OBJC_CLASS_$_UIView", "_OBJC_METACLASS_$_UIView" });
    }

    [Test]
    public async Task LdDirectiveTargetsNameTheSymbolTheyApplyTo()
    {
        await Assert.That(StubLink.LdDirectiveTarget("$ld$previous$/usr/lib/swift/libswiftFoundation.dylib$1.0.0$2$12.2$16.0$_$s10FoundationX$")).IsEqualTo("_$s10FoundationX");
        await Assert.That(StubLink.LdDirectiveTarget("$ld$previous$/usr/lib/swift/libswiftDarwin.dylib$$2$12.2$17.0$$")).IsEqualTo("");
        await Assert.That(StubLink.LdDirectiveTarget("$ld$hide$os15.0$_foo")).IsEqualTo("_foo");
        await Assert.That(StubLink.LdDirectiveTarget("$ld$install_name$os4.0$/usr/lib/old.dylib")).IsEqualTo("");
        await Assert.That(StubLink.LdDirectiveTarget("_plain")).IsNull();
    }

    [Test]
    public async Task FlatClassesStayOutOfThePerLinkStubsButKeepTheirFrameworkLinked()
    {
        // The class is recorded under its namespace's framework only as a hint: the autolinked
        // framework is kept (the class is used), but the class itself is left for flat lookup.
        var s = new Sysroot();
        s.Lib("libUIKit", "/System/UIKit", ["_OBJC_CLASS_$_NSLayoutConstraint"]);
        s.Lib("libobjc", "/usr/lib/libobjc.A.dylib", ["_OBJC_CLASS_$_NSObject"]);

        var args = StubLink.Prepare([.. Device, $"-L{s.Root}/usr/lib", "-lobjc", "-lUIKit"], s.Root, s.LinkRoot,
            autolinkStart: Device.Length + 2, referenced: new HashSet<string> { "_OBJC_CLASS_$_NSLayoutConstraint", "_OBJC_CLASS_$_NSObject" },
            flatObjCClasses: true);

        await Assert.That(args).Contains("-lUIKit");
        await Assert.That(s.Exports("libUIKit")).DoesNotContain("_OBJC_CLASS_$_NSLayoutConstraint");
        await Assert.That(s.Exports("libobjc")).Contains("_OBJC_CLASS_$_NSObject");
    }
}
