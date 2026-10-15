using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// The iOS stub sysroot is generated from open-source inputs only (IosStubs). Validated end to
// end on MAUI (device and simulator) and Avalonia (simulator): the same dylibs as Apple's own
// link, every binding either identical or through a library that re-exports the same symbol,
// and both apps run. These pin the attribution rules.
public class IosStubsTests
{
    static readonly IosStubs.Context Context = new(
        ZigLibSystem: new HashSet<string> { "_malloc", "__Block_copy", "_cos" },
        SwiftOverlays: new HashSet<string> { "swiftFoundation", "swiftDispatch" },
        AutolinkFrameworks: new HashSet<string> { "StoreKit", "UIKit" },
        Frameworks: new HashSet<string>
        {
            "/System/Library/Frameworks/UIKit.framework/UIKit",
            "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI",
            "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox",
            "/System/Library/Frameworks/Accelerate.framework/Accelerate",
        },
        NamespaceLibraries: new Dictionary<string, string> { ["AudioUnit"] = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox" });

    static string? Attribute(string symbol, string? named = null) => IosStubs.Attribute(symbol, named, Context);

    [Test]
    public async Task FunctionsGoToTheLibraryTheBindingNames()
    {
        await Assert.That(Attribute("_UIApplicationMain", "/System/Library/Frameworks/UIKit.framework/UIKit"))
            .IsEqualTo("/System/Library/Frameworks/UIKit.framework/UIKit");
    }

    [Test]
    public async Task ZigsLibSystemListBeatsABindingsApproximateLibrary()
    {
        // The bindings P/Invoke _Block_copy "from" libobjc; it is libSystem's.
        await Assert.That(Attribute("__Block_copy", "/usr/lib/libobjc.dylib")).IsEqualTo(IosStubs.LibSystem);
        await Assert.That(Attribute("_cos")).IsEqualTo(IosStubs.LibSystem);
    }

    [Test]
    public async Task BindingLibraryNamesAreNormalized()
    {
        // Install names, Microsoft's casing, umbrellas for sub-frameworks, a namespace's own library.
        await Assert.That(Attribute("_objc_foo", "/usr/lib/libobjc.dylib")).IsEqualTo(IosStubs.LibObjc);
        await Assert.That(Attribute("_MIDIThing", "/System/Library/Frameworks/CoreMidi.framework/CoreMidi"))
            .IsEqualTo("/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI");
        await Assert.That(Attribute("_vImageBox", "/System/Library/Frameworks/Accelerate.framework/Frameworks/vImage.framework/vImage"))
            .IsEqualTo("/System/Library/Frameworks/Accelerate.framework/Accelerate");
        await Assert.That(Attribute("_OBJC_CLASS_$_AUAudioUnit", "/System/Library/Frameworks/AudioUnit.framework/AudioUnit"))
            .IsEqualTo("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox");
    }

    [Test]
    public async Task OpenSourceRuntimeLibrariesByTheirApiNames()
    {
        await Assert.That(Attribute("__Znwm")).IsEqualTo(IosStubs.LibCxx);
        await Assert.That(Attribute("___cxa_throw")).IsEqualTo(IosStubs.LibCxx);
        await Assert.That(Attribute("___cxa_atexit")).IsEqualTo(IosStubs.LibSystem);
        await Assert.That(Attribute("__ZN9libunwind25findDynamicUnwindSections")).IsEqualTo(IosStubs.LibSystem);
        await Assert.That(Attribute("_objc_retain")).IsEqualTo(IosStubs.LibObjc);
        await Assert.That(Attribute("_OBJC_CLASS_$_NSObject", "/System/Library/Frameworks/Foundation.framework/Foundation")).IsEqualTo(IosStubs.LibObjc);
        await Assert.That(Attribute("_deflateInit2_")).IsEqualTo("/usr/lib/libz.1.dylib");
        await Assert.That(Attribute("_uidna_openUTS46")).IsEqualTo("/usr/lib/libicucore.A.dylib");
        await Assert.That(Attribute("_SecRandomCopyBytes")).IsEqualTo("/System/Library/Frameworks/Security.framework/Security");
        await Assert.That(Attribute("_CFHTTPMessageCreateRequest")).IsEqualTo("/System/Library/Frameworks/CFNetwork.framework/CFNetwork");
    }

    [Test]
    public async Task SwiftSymbolsGoByTheirModuleAndWhatTheInputsAutolink()
    {
        // An overlay library the inputs autolink...
        await Assert.That(Attribute("_$s10Foundation21NSKeyValueObservationCMn")).IsEqualTo("/usr/lib/swift/libswiftFoundation.dylib");
        // ...an extension of an imported Objective-C type, in the Foundation overlay...
        await Assert.That(Attribute("_$sSo8NSObjectC10Foundation27_KeyValueCodingAndObservingACWP")).IsEqualTo("/usr/lib/swift/libswiftFoundation.dylib");
        // ...Swift API that lives in the framework itself...
        await Assert.That(Attribute("_$s8StoreKit03AppA0O13requestReview")).IsEqualTo("/System/Library/Frameworks/StoreKit.framework/StoreKit");
        // ...a module that moved (and gets a $ld$previous back to its old library)...
        await Assert.That(Attribute("_$s12CoreGraphics7CGFloatVMn")).IsEqualTo("/usr/lib/swift/libswiftCoreFoundation.dylib");
        // ...and the standard library.
        await Assert.That(Attribute("_$sSHMp")).IsEqualTo("/usr/lib/swift/libswiftCore.dylib");
        await Assert.That(Attribute("_$s8RawValueSYTl")).IsEqualTo("/usr/lib/swift/libswiftCore.dylib");
        await Assert.That(Attribute("_swift_retain")).IsEqualTo("/usr/lib/swift/libswiftCore.dylib");
        await Assert.That(Attribute("__swift_FORCE_LOAD_$_swiftDispatch")).IsEqualTo("/usr/lib/swift/libswiftDispatch.dylib");
    }

    [Test]
    public async Task AppDefinedToolchainOnlyAndLinkerSynthesizedSymbolsAreNotStubbed()
    {
        await Assert.That(Attribute("__swift_FORCE_LOAD_$_swiftCompatibility56")).IsNull();
        await Assert.That(Attribute("_$s4main21XamarinSwiftFunctionsCMa")).IsNull();
        await Assert.That(Attribute("_objc_msgSend$copy")).IsNull();
        await Assert.That(Attribute("section$start$__DATA$__modules")).IsNull();
        await Assert.That(Attribute("_RhpNewFast")).IsNull();
    }

    [Test]
    public async Task StubFilesCoverEveryLinkerName()
    {
        await Assert.That(IosStubs.StubPaths("/usr/lib/libz.1.dylib")).IsEquivalentTo(new[] { "usr/lib/libz.1.tbd", "usr/lib/libz.tbd" });
        await Assert.That(IosStubs.StubPaths("/System/Library/Frameworks/UIKit.framework/UIKit"))
            .IsEquivalentTo(new[] { "System/Library/Frameworks/UIKit.framework/UIKit.tbd" });
        // -lm, -ldl ... are libSystem.
        var libSystem = IosStubs.StubPaths(IosStubs.LibSystem).ToList();
        await Assert.That(libSystem).Contains("usr/lib/libSystem.tbd");
        await Assert.That(libSystem).Contains("usr/lib/libm.tbd");
        await Assert.That(libSystem).Contains("usr/lib/libdl.tbd");
    }

    [Test]
    public async Task FlatLookupIsOnlyForUndefinedObjectiveCClasses()
    {
        const string classesOnly = """
            error: undefined symbol: _OBJC_CLASS_$_NSLayoutConstraint
                note: referenced by obj/registrar.o:_main
            error: undefined symbol: _OBJC_METACLASS_$_UIView
            """;
        const string realMissing = """
            error: undefined symbol: _OBJC_CLASS_$_UIView
            error: undefined symbol: _SomeFunction
            """;
        await Assert.That(ClangToZig.OnlyObjCClassesUndefined(classesOnly)).IsTrue();
        await Assert.That(ClangToZig.OnlyObjCClassesUndefined(realMissing)).IsFalse();
        await Assert.That(ClangToZig.OnlyObjCClassesUndefined("error: unable to find framework 'Foo'")).IsFalse();
        await Assert.That(ClangToZig.OnlyObjCClassesUndefined("")).IsFalse();
    }
}
