using System.Runtime.InteropServices;
using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests
{
    // ObjCShims reads binding metadata by attribute and type *name*, so this test assembly
    // doubles as a tiny binding assembly: the Foundation/UIKit/CoreGraphics namespaces at the
    // bottom of this file mimic Microsoft.iOS's shapes. The generator's output for the real
    // Microsoft.iOS.dll was validated separately by compiling the MAUI and Avalonia template
    // registrars against it and comparing with an Xcode-header compile: identical symbols,
    // code and method type encodings.
    public class ObjCShimsTests
    {
        static readonly Lazy<string> Header = new(() => Generate(out _));

        static string Generate(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "aotanywhere-objcshims-" + Guid.NewGuid().ToString("N"));
            ObjCShims.Generate([typeof(Foundation.NSObject).Assembly.Location], dir);
            return File.ReadAllText(Path.Combine(dir, ObjCShims.DeclarationsFile));
        }

        [Test]
        public async Task WrapperClassesGetTheirSuperclassAndDirectProtocols()
        {
            // INSSecureCoding extends INSCoding, so C# lists INSCoding on NSString too; ObjC
            // states only the direct protocols.
            await Assert.That(Header.Value).Contains("@interface NSString : NSObject <NSCopying, NSSecureCoding> @end");
            await Assert.That(Header.Value).Contains("@interface UIView : UIResponder @end");
        }

        [Test]
        public async Task SuperclassSkipsUnregisteredManagedClasses()
        {
            await Assert.That(Header.Value).Contains("@interface UIButtonLike : UIView @end");
        }

        [Test]
        public async Task SuperclassesAreDeclaredBeforeSubclasses()
        {
            int responder = Header.Value.IndexOf("@interface UIResponder ", StringComparison.Ordinal);
            int view = Header.Value.IndexOf("@interface UIView ", StringComparison.Ordinal);
            int button = Header.Value.IndexOf("@interface UIButtonLike ", StringComparison.Ordinal);
            await Assert.That(responder).IsGreaterThanOrEqualTo(0);
            await Assert.That(responder).IsLessThan(view);
            await Assert.That(view).IsLessThan(button);
        }

        [Test]
        public async Task ModelsAndNSObjectAreNotRedeclared()
        {
            // [Register("...", false)] models are managed-only; NSObject comes from objc4's headers.
            await Assert.That(Header.Value).DoesNotContain("TableDelegateModel");
            await Assert.That(Header.Value).DoesNotContain("@interface NSObject");
        }

        [Test]
        public async Task ProtocolsExtendNSObjectUnlessFoundationRoots()
        {
            await Assert.That(Header.Value).Contains("@protocol UIScrollViewDelegate <NSObject> @end");
            await Assert.That(Header.Value).Contains("@protocol UITableViewDelegate <UIScrollViewDelegate> @end");
            await Assert.That(Header.Value).Contains("@protocol NSCoding @end");
            await Assert.That(Header.Value).Contains("@protocol NSSecureCoding <NSCoding> @end");
        }

        [Test]
        public async Task ProtocolsAreDefinedBeforeTheProtocolsExtendingThem()
        {
            int scroll = Header.Value.IndexOf("@protocol UIScrollViewDelegate <", StringComparison.Ordinal);
            int table = Header.Value.IndexOf("@protocol UITableViewDelegate <", StringComparison.Ordinal);
            await Assert.That(scroll).IsGreaterThanOrEqualTo(0);
            await Assert.That(scroll).IsLessThan(table);
        }

        [Test]
        public async Task InformalProtocolsAreLeftToTheRegistrar()
        {
            await Assert.That(Header.Value).DoesNotContain("UIInformalThing");
        }

        [Test]
        public async Task GenericCollectionsGetLightweightGenerics()
        {
            await Assert.That(Header.Value).Contains("@interface NSSet<ObjectType> : NSObject @end");
            await Assert.That(Header.Value).Contains("@interface NSDictionary<KeyType, ObjectType> : NSObject @end");
        }

        [Test]
        public async Task WellKnownStructsUseTheFrameworkLayoutNotTheBindingOne()
        {
            // The type encoding of a method taking a CGRect must be {CGRect={CGPoint=dd}{CGSize=dd}},
            // and NSRange {_NSRange=QQ}; the bindings' flattened, signed layouts would change both.
            await Assert.That(Header.Value).Contains("struct CGRect { CGPoint origin; CGSize size; };");
            await Assert.That(Header.Value).Contains("typedef struct _NSRange { NSUInteger location; NSUInteger length; } NSRange;");
            await Assert.That(Header.Value).DoesNotContain("typedef struct CGRect {");
            await Assert.That(Header.Value).DoesNotContain("typedef struct NSRange {");
        }

        [Test]
        public async Task OtherStructsFollowTheBindingLayout()
        {
            // NFloat is CGFloat, enums are their underlying type, struct fields come first.
            await Assert.That(Header.Value).Contains("typedef struct UIStyled { int64_t Style; int32_t Count; } UIStyled;");
            int point = Header.Value.IndexOf("typedef struct CGPointLike {", StringComparison.Ordinal);
            int insets = Header.Value.IndexOf("typedef struct UIFancyInsets { CGFloat Top; CGPointLike Corner; } UIFancyInsets;", StringComparison.Ordinal);
            await Assert.That(point).IsGreaterThanOrEqualTo(0);
            await Assert.That(point).IsLessThan(insets);
        }

        [Test]
        public async Task StructsWithoutACLayoutOrThatMirrorTheRuntimeAreSkipped()
        {
            await Assert.That(Header.Value).DoesNotContain("UIManagedOnly");
            await Assert.That(Header.Value).DoesNotContain("objc_super");
        }

        [Test]
        public async Task EachBindingNamespaceGetsAnUmbrellaStub()
        {
            Generate(out var dir);
            var uikit = Path.Combine(dir, "UIKit", "UIKit.h");
            await Assert.That(File.Exists(uikit)).IsTrue();
            await Assert.That(File.ReadAllText(uikit)).Contains($"#import <{ObjCShims.DeclarationsFile}>");
            await Assert.That(File.Exists(Path.Combine(dir, "CoreGraphics", "CoreGraphics.h"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "QuartzCore", "QuartzCore.h"))).IsTrue();
        }

        [Test]
        public async Task FrameworkImportsFollowQuotedIncludes()
        {
            var dir = Path.Combine(Path.GetTempPath(), "aotanywhere-objcimports-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "registrar.mm"), "#include <stdarg.h>\n#import \"registrar.h\"\n");
            File.WriteAllText(Path.Combine(dir, "registrar.h"), "#include <objc/runtime.h>\n#import <UIKit/UIKit.h>\n  #import <CoreImage/CIFilterBuiltins.h>\n#import <xamarin/xamarin.h>\n");

            var imports = ObjCShims.FrameworkImports(Path.Combine(dir, "registrar.mm")).ToList();

            await Assert.That(imports).IsEquivalentTo(new[] { "CoreImage/CIFilterBuiltins.h", "UIKit/UIKit.h" });
        }

        [Test]
        public async Task ClangCompileWithShimsUsesNoSdkHeaders()
        {
            const string sdk = "/X/iPhoneSimulator.sdk";
            var inv = ClangToZig.Translate("clang",
                ["-miphonesimulator-version-min=15.0", "-arch", "arm64", "-isysroot", sdk, "-std=c++14", "-c", "registrar.mm", "-o", "registrar.o"],
                new HeaderShims("/obj/aotanywhere-objc", "/zig/lib/libc/include/any-darwin-any"));

            await Assert.That(inv.Driver).IsEqualTo("c++");
            await Assert.That(inv.Args).Contains("-nostdinc++");
            await Assert.That(inv.Args).Contains("/zig/lib/libc/include/any-darwin-any");
            await Assert.That(inv.Args).Contains("/obj/aotanywhere-objc");
            await Assert.That(inv.Args.Any(a => a.Contains(sdk))).IsFalse();
        }
    }
}

// --- A miniature binding assembly (see ObjCShimsTests) ---

namespace Foundation
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
    public sealed class RegisterAttribute : Attribute
    {
        public RegisterAttribute(string name, bool isWrapper) { }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
    public sealed class ProtocolAttribute : Attribute
    {
        public string? Name { get; set; }
        public bool IsInformal { get; set; }
    }

    [Register("NSObject", true)] public class NSObject { }
    [Protocol(Name = "NSCopying")] public interface INSCopying { }
    [Protocol(Name = "NSCoding")] public interface INSCoding { }
    [Protocol(Name = "NSSecureCoding")] public interface INSSecureCoding : INSCoding { }
    [Register("NSString", true)] public class NSString : NSObject, INSCopying, INSSecureCoding { }
    [Register("NSSet", true)] public class NSSet : NSObject { }
    public class NSSet<TKey> : NSSet { }
    [Register("NSDictionary", true)] public class NSDictionary : NSObject { }
    public class NSDictionary<TKey, TValue> : NSDictionary { }
    public struct NSRange { public nint Location; public nint Length; }
    internal struct objc_super { public IntPtr Handle; public IntPtr ClassHandle; }
}

namespace UIKit
{
    using Foundation;

    [Register("UIResponder", true)] public class UIResponder : NSObject { }
    [Register("UIView", true)] public class UIView : UIResponder { }
    public class ManagedView : UIView { }
    [Register("UIButtonLike", true)] public class UIButtonLike : ManagedView { }
    [Protocol(Name = "UIScrollViewDelegate")] public interface IUIScrollViewDelegate { }
    [Protocol(Name = "UITableViewDelegate")] public interface IUITableViewDelegate : IUIScrollViewDelegate { }
    [Register("TableDelegateModel", false)] public class UITableViewDelegate : NSObject, IUITableViewDelegate { }
    [Protocol(Name = "UIInformalThing", IsInformal = true)] public interface IUIInformalThing { }
    public enum UIStyle : long { Plain }
    public struct UIStyled { public UIStyle Style; public int Count; }
    public struct UIFancyInsets { public NFloat Top; public CoreGraphics.CGPointLike Corner; }
    public struct UIManagedOnly { public string Text; }
}

namespace CoreGraphics
{
    public struct CGPointLike { public NFloat X; public NFloat Y; }
    public struct CGRect { NFloat x, y, width, height; }
}
