using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// The ibtool stand-in. Validated end to end against Apple's ibtool output and on screen: MAUI's
// generated splash storyboard and Avalonia's launch XIB render identically to Apple's compiled
// versions in the simulator. These pin the encoding rules.
public class InterfaceBuilderTests
{
    static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aotanywhere-ib-" + Guid.NewGuid().ToString("N"))).FullName;

    const string Storyboard = """
        <?xml version="1.0" encoding="UTF-8"?>
        <document type="com.apple.InterfaceBuilder3.CocoaTouch.Storyboard.XIB" version="3.0" launchScreen="YES" useAutolayout="YES" initialViewController="VC1">
            <scenes>
                <scene sceneID="S1">
                    <objects>
                        <viewController id="VC1" sceneMemberID="viewController">
                            <view key="view" contentMode="scaleToFill" id="V1">
                                <rect key="frame" x="0.0" y="0.0" width="414" height="896"/>
                                <autoresizingMask key="autoresizingMask" widthSizable="YES" heightSizable="YES"/>
                                <subviews>
                                    <imageView contentMode="center" fixedFrame="YES" translatesAutoresizingMaskIntoConstraints="NO" image="splash.png" id="I1">
                                        <rect key="frame" x="0.0" y="0.0" width="414" height="896"/>
                                        <autoresizingMask key="autoresizingMask" widthSizable="YES" heightSizable="YES"/>
                                    </imageView>
                                </subviews>
                                <color key="backgroundColor" red="0.5" green="0.25" blue="1" alpha="1" colorSpace="custom" customColorSpace="sRGB"/>
                            </view>
                        </viewController>
                        <placeholder placeholderIdentifier="IBFirstResponder" id="FR" sceneMemberID="firstResponder"/>
                    </objects>
                </scene>
            </scenes>
        </document>
        """;

    const string Xib = """
        <?xml version="1.0" encoding="UTF-8"?>
        <document type="com.apple.InterfaceBuilder3.CocoaTouch.XIB" version="3.0" launchScreen="YES" useAutolayout="YES">
            <objects>
                <placeholder placeholderIdentifier="IBFilesOwner" id="-1"/>
                <placeholder placeholderIdentifier="IBFirstResponder" id="-2"/>
                <view contentMode="scaleToFill" id="root">
                    <rect key="frame" x="0" y="0" width="480" height="480"/>
                    <autoresizingMask key="autoresizingMask" widthSizable="YES" heightSizable="YES"/>
                    <subviews>
                        <label text="Hello" textAlignment="center" lineBreakMode="middleTruncation" minimumFontSize="18" translatesAutoresizingMaskIntoConstraints="NO" id="L1">
                            <rect key="frame" x="20" y="140" width="441" height="43"/>
                            <fontDescription key="fontDescription" type="boldSystem" pointSize="36"/>
                            <color key="textColor" cocoaTouchSystemColor="darkTextColor"/>
                        </label>
                    </subviews>
                    <color key="backgroundColor" white="1" alpha="1" colorSpace="custom" customColorSpace="calibratedWhite"/>
                    <constraints>
                        <constraint firstItem="L1" firstAttribute="centerY" secondItem="root" secondAttribute="bottom" multiplier="1/3" constant="1" id="c1"/>
                        <constraint firstAttribute="centerX" secondItem="L1" secondAttribute="centerX" id="c2"/>
                        <constraint firstItem="L1" firstAttribute="leading" secondItem="root" secondAttribute="leading" constant="20" symbolic="YES" id="c3"/>
                    </constraints>
                </view>
            </objects>
        </document>
        """;

    static string Write(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public async Task StoryboardCompilesToAControllerNibAViewNibAndAnInfoPlist()
    {
        var dir = Temp();
        var outDir = Path.Combine(dir, "out");
        InterfaceBuilder.Compile(Write(dir, "Splash.storyboard", Storyboard), outDir);

        var sb = Path.Combine(outDir, "Splash.storyboardc");
        var info = Plist.ReadDictionary(File.ReadAllBytes(Path.Combine(sb, "Info.plist")));
        await Assert.That(info["UIStoryboardDesignatedEntryPointIdentifier"]).IsEqualTo("UIViewController-VC1");

        var controller = NibArchive.Read(File.ReadAllBytes(Path.Combine(sb, "UIViewController-VC1.nib")));
        var swapper = controller.Objects.Single(o => o.ClassName == "UIClassSwapper");
        await Assert.That(System.Text.Encoding.UTF8.GetString((byte[])((NibArchive.Obj)swapper["UINibName"]!)["NS.bytes"]!)).IsEqualTo("VC1-view-V1");

        var view = NibArchive.Read(File.ReadAllBytes(Path.Combine(sb, "VC1-view-V1.nib")));
        var image = view.Objects.Single(o => o.ClassName == "UIImageView");
        await Assert.That(image["UIContentMode"]).IsEqualTo(4);
        await Assert.That(image["UIAutoresizingMask"]).IsEqualTo(18);
        // fixedFrame keeps the frame even though the view doesn't translate its autoresizing mask.
        await Assert.That(image.Items.Any(i => i.Key == "UIViewDoesNotTranslateAutoresizingMaskIntoConstraints")).IsFalse();
        var placeholder = (NibArchive.Obj)image["UIImage"]!;
        await Assert.That(placeholder.ClassName).IsEqualTo("UIImageNibPlaceholder");
    }

    [Test]
    public async Task GeometryIsEncodedAsDoubles()
    {
        var dir = Temp();
        InterfaceBuilder.Compile(Write(dir, "Splash.storyboard", Storyboard), Path.Combine(dir, "out"));
        var view = NibArchive.Read(File.ReadAllBytes(Path.Combine(dir, "out", "Splash.storyboardc", "VC1-view-V1.nib")));
        var root = view.Objects.First(o => o.ClassName == "UIView");

        var center = (byte[])root["UICenter"]!;
        await Assert.That(center[0]).IsEqualTo((byte)7);
        await Assert.That(BitConverter.ToDouble(center, 1)).IsEqualTo(207.0);
        await Assert.That(BitConverter.ToDouble(center, 9)).IsEqualTo(448.0);
    }

    [Test]
    public async Task XibCompilesLabelsFontsColorsAndConstraints()
    {
        var dir = Temp();
        InterfaceBuilder.Compile(Write(dir, "LaunchScreen.xib", Xib), dir);
        var nib = NibArchive.Read(File.ReadAllBytes(Path.Combine(dir, "LaunchScreen.nib")));

        var label = nib.Objects.Single(o => o.ClassName == "UILabel");
        await Assert.That(label["UITextAlignment"]).IsEqualTo(1);
        await Assert.That(label["UILineBreakMode"]).IsEqualTo(5);
        var font = (NibArchive.Obj)label["UIFont"]!;
        await Assert.That(font["UIFontPointSize"]).IsEqualTo(new NibArchive.Double(36));
        await Assert.That(font["UISystemFont"]).IsEqualTo(true);
        var textColor = (NibArchive.Obj)label["UITextColor"]!;
        await Assert.That(System.Text.Encoding.UTF8.GetString((byte[])((NibArchive.Obj)textColor["UISystemColorName"]!)["NS.bytes"]!)).IsEqualTo("darkTextColor");

        var constraints = nib.Objects.Where(o => o.ClassName == "NSLayoutConstraint").ToList();
        await Assert.That(constraints.Count).IsEqualTo(3);
        var third = constraints.Single(c => c["NSMultiplier"] is not null);
        await Assert.That(third["NSMultiplier"]).IsEqualTo(new NibArchive.Double((float)(1.0 / 3)));
        await Assert.That(third["NSFirstAttribute"]).IsEqualTo(10);
        await Assert.That(constraints.Count(c => c["NSSymbolicConstant"] is not null)).IsEqualTo(1);
    }

    [Test]
    public async Task UnsupportedElementsAreErrors()
    {
        var dir = Temp();
        var xib = Xib.Replace("<label ", "<button ").Replace("</label>", "</button>");
        await Assert.That(() => InterfaceBuilder.Compile(Write(dir, "Bad.xib", xib), dir)).Throws<InterfaceBuilder.CompileError>();
    }

    [Test]
    public async Task NibArchiveRoundTrips()
    {
        var root = new NibArchive.Obj("NSObject").Add("A", 1).Add("B", 70000).Add("C", true).Add("D", 1.5f)
            .Add("E", new NibArchive.Double(2.5)).Add("F", new byte[] { 1, 2 }).Add("G", null);
        var child = new NibArchive.Obj("NSString").Add("NS.bytes", "x"u8.ToArray());
        root.Add("H", child).Add("I", child);

        var read = NibArchive.Read(NibArchive.Write(root));

        await Assert.That(read.Root["A"]).IsEqualTo(1);
        await Assert.That(read.Root["B"]).IsEqualTo(70000);
        await Assert.That(read.Root["E"]).IsEqualTo(new NibArchive.Double(2.5));
        await Assert.That(read.Root["H"]).IsSameReferenceAs(read.Root["I"]);
        await Assert.That(read.Objects.Count).IsEqualTo(2);
    }
}
