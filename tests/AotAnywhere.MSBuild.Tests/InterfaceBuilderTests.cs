using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// The ibtool stand-in. Validated end to end against Apple's ibtool output and on screen: MAUI's
// generated splash storyboard and Avalonia's launch XIB render identically to Apple's compiled
// versions in the simulator, and test/ios/IBTest's storyboards (navigation, controls, segues,
// tab bar) compile to the same files as Apple's, render pixel-identically, and drive their
// outlets, actions and segues at run time. These pin the encoding rules.
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
        var xib = Xib.Replace("<label ", "<pickerView ").Replace("</label>", "</pickerView>");
        await Assert.That(() => InterfaceBuilder.Compile(Write(dir, "Bad.xib", xib), dir)).Throws<InterfaceBuilder.CompileError>();
    }

    const string Navigation = """
        <?xml version="1.0" encoding="UTF-8"?>
        <document type="com.apple.InterfaceBuilder3.CocoaTouch.Storyboard.XIB" version="3.0" useAutolayout="YES" useSafeAreas="YES" initialViewController="NAV">
            <scenes>
                <scene sceneID="S1">
                    <objects>
                        <navigationController id="NAV" sceneMemberID="viewController">
                            <navigationBar key="navigationBar" contentMode="scaleToFill" id="NB">
                                <rect key="frame" x="0.0" y="59" width="393" height="44"/>
                                <autoresizingMask key="autoresizingMask"/>
                            </navigationBar>
                            <connections>
                                <segue destination="MAIN" kind="relationship" relationship="rootViewController" id="R1"/>
                            </connections>
                        </navigationController>
                        <placeholder placeholderIdentifier="IBFirstResponder" id="FR1" sceneMemberID="firstResponder"/>
                    </objects>
                </scene>
                <scene sceneID="S2">
                    <objects>
                        <viewController id="MAIN" customClass="MainViewController" sceneMemberID="viewController">
                            <view key="view" contentMode="scaleToFill" id="V">
                                <rect key="frame" x="0.0" y="0.0" width="393" height="852"/>
                                <autoresizingMask key="autoresizingMask" widthSizable="YES" heightSizable="YES"/>
                                <subviews>
                                    <stackView opaque="NO" contentMode="scaleToFill" axis="vertical" spacing="12" translatesAutoresizingMaskIntoConstraints="NO" id="SV">
                                        <rect key="frame" x="20" y="123" width="353" height="100"/>
                                        <subviews>
                                            <textField opaque="NO" contentMode="scaleToFill" contentHorizontalAlignment="left" contentVerticalAlignment="center" borderStyle="roundedRect" placeholder="Name" minimumFontSize="17" translatesAutoresizingMaskIntoConstraints="NO" id="TF">
                                                <rect key="frame" x="0.0" y="0.0" width="353" height="34"/>
                                                <fontDescription key="fontDescription" type="system" pointSize="14"/>
                                                <textInputTraits key="textInputTraits" autocapitalizationType="words" keyboardType="emailAddress" returnKeyType="done"/>
                                            </textField>
                                            <button opaque="NO" contentMode="scaleToFill" buttonType="system" lineBreakMode="middleTruncation" translatesAutoresizingMaskIntoConstraints="NO" id="B1">
                                                <rect key="frame" x="0.0" y="46" width="353" height="30"/>
                                                <state key="normal" title="Greet"/>
                                                <connections>
                                                    <action selector="greet:" destination="MAIN" eventType="touchUpInside" id="A1"/>
                                                </connections>
                                            </button>
                                            <button opaque="NO" contentMode="scaleToFill" buttonType="system" lineBreakMode="middleTruncation" translatesAutoresizingMaskIntoConstraints="NO" id="B2">
                                                <rect key="frame" x="0.0" y="88" width="353" height="12"/>
                                                <state key="normal" title="Next"/>
                                                <connections>
                                                    <segue destination="SECOND" kind="show" id="SG"/>
                                                </connections>
                                            </button>
                                        </subviews>
                                    </stackView>
                                    <label opaque="NO" text="Loose" translatesAutoresizingMaskIntoConstraints="NO" id="LOOSE">
                                        <rect key="frame" x="20" y="400" width="100" height="20"/>
                                    </label>
                                </subviews>
                                <viewLayoutGuide key="safeArea" id="SA"/>
                                <constraints>
                                    <constraint firstItem="SV" firstAttribute="top" secondItem="SA" secondAttribute="top" constant="20" id="c1"/>
                                </constraints>
                            </view>
                            <navigationItem key="navigationItem" title="Main" id="NI"/>
                            <connections>
                                <outlet property="nameField" destination="TF" id="o1"/>
                            </connections>
                        </viewController>
                        <placeholder placeholderIdentifier="IBFirstResponder" id="FR2" sceneMemberID="firstResponder"/>
                        <exit id="X2" userLabel="Exit" sceneMemberID="exit"/>
                    </objects>
                </scene>
                <scene sceneID="S3">
                    <objects>
                        <viewController id="SECOND" sceneMemberID="viewController">
                            <view key="view" contentMode="scaleToFill" id="V2">
                                <rect key="frame" x="0.0" y="0.0" width="393" height="852"/>
                                <viewLayoutGuide key="safeArea" id="SA2"/>
                            </view>
                        </viewController>
                    </objects>
                </scene>
            </scenes>
        </document>
        """;

    static Dictionary<string, NibArchive> CompileNavigation(out Dictionary<string, object> info)
    {
        var dir = Temp();
        InterfaceBuilder.Compile(Write(dir, "Nav.storyboard", Navigation), Path.Combine(dir, "out"));
        var sb = Path.Combine(dir, "out", "Nav.storyboardc");
        info = Plist.ReadDictionary(File.ReadAllBytes(Path.Combine(sb, "Info.plist")));
        return Directory.GetFiles(sb, "*.nib").ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => NibArchive.Read(File.ReadAllBytes(f)));
    }

    static string Str(object? o) => System.Text.Encoding.UTF8.GetString((byte[])((NibArchive.Obj)o!)["NS.bytes"]!);

    [Test]
    public async Task RelationshipChildrenAreEmbeddedAndSegueDestinationsGetTheirOwnNib()
    {
        var nibs = CompileNavigation(out var info);

        // As Apple's ibtool: the root controller lives inside the navigation controller's nib.
        await Assert.That(nibs.Keys.Order()).IsEquivalentTo(new[] { "MAIN-view-V", "SECOND-view-V2", "UINavigationController-NAV", "UIViewController-SECOND" });
        await Assert.That(info["UIStoryboardDesignatedEntryPointIdentifier"]).IsEqualTo("UINavigationController-NAV");

        var nav = nibs["UINavigationController-NAV"];
        var swappers = nav.Objects.Where(o => o.ClassName == "UIClassSwapper").ToList();
        var navController = swappers.Single(o => Str(o["UIClassName"]) == "UINavigationController");
        var main = swappers.Single(o => Str(o["UIClassName"]) == "MainViewController");
        await Assert.That(main["UIParentViewController"]).IsSameReferenceAs(navController);
        var bar = (NibArchive.Obj)navController["UINavigationBar"]!;
        await Assert.That(((NibArchive.Obj)bar["UIItems"]!)["UINibEncoderEmptyKey"]).IsSameReferenceAs(main["UINavigationItem"]);

        var template = nav.Objects.Single(o => o.ClassName == "UIStoryboardShowSegueTemplate");
        await Assert.That(Str(template["UIDestinationViewControllerIdentifier"])).IsEqualTo("UIViewController-SECOND");
        await Assert.That(Str(template["UIActionName"])).IsEqualTo("showViewController:sender:");
    }

    [Test]
    public async Task OutletsActionsAndSeguesInAViewGoThroughUpstreamPlaceholders()
    {
        var view = CompileNavigation(out _)["MAIN-view-V"];
        var proxies = view.Objects.Where(o => o.ClassName == "UIProxyObject").ToDictionary(o => Str(o["UIProxiedObjectIdentifier"]));
        await Assert.That(proxies.Keys).Contains("UpstreamPlaceholder-1"); // the controller
        await Assert.That(proxies.Keys).Contains("UpstreamPlaceholder-2"); // the segue template

        var outlet = view.Objects.Single(o => o.ClassName == "UIRuntimeOutletConnection" && Str(o["UILabel"]) == "nameField");
        await Assert.That(outlet["UISource"]).IsSameReferenceAs(proxies["UpstreamPlaceholder-1"]);
        await Assert.That(((NibArchive.Obj)outlet["UIDestination"]!).ClassName).IsEqualTo("UITextField");

        var events = view.Objects.Where(o => o.ClassName == "UIRuntimeEventConnection").ToDictionary(o => Str(o["UILabel"]));
        await Assert.That(events["greet:"]["UIDestination"]).IsSameReferenceAs(proxies["UpstreamPlaceholder-1"]);
        await Assert.That(events["greet:"]["UIEventMask"]).IsEqualTo(64);
        await Assert.That(events["perform:"]["UIDestination"]).IsSameReferenceAs(proxies["UpstreamPlaceholder-2"]);

        // The controller's table maps the placeholders back.
        var nav = CompileNavigation(out _)["UINavigationController-NAV"];
        var main = nav.Objects.Single(o => o.ClassName == "UIClassSwapper" && Str(o["UIClassName"]) == "MainViewController");
        var table = ((NibArchive.Obj)main["UIExternalObjectsTableForViewLoading"]!).Items.Where(i => i.Key == "UINibEncoderEmptyKey").Select(i => i.Value).ToList();
        await Assert.That(Str(table[0])).IsEqualTo("UpstreamPlaceholder-1");
        await Assert.That(table[1]).IsSameReferenceAs(main);
        await Assert.That(((NibArchive.Obj)table[3]!).ClassName).IsEqualTo("UIStoryboardShowSegueTemplate");
    }

    [Test]
    public async Task OnlyPlacedViewsStopTranslatingTheirAutoresizingMask()
    {
        var view = CompileNavigation(out _)["MAIN-view-V"];
        bool Translates(string className) => !view.Objects.Single(o => o.ClassName == className).Items.Any(i => i.Key == "UIViewDoesNotTranslateAutoresizingMaskIntoConstraints");
        await Assert.That(Translates("UIStackView")).IsFalse();   // a constraint places it
        await Assert.That(Translates("UITextField")).IsFalse();   // the stack view places it
        await Assert.That(Translates("UILabel")).IsTrue();        // nothing does: Interface Builder keeps its frame
    }

    [Test]
    public async Task TheSafeAreaGuideIsArchivedOnlyWhenConstraintsUseIt()
    {
        var nibs = CompileNavigation(out _);
        var guide = nibs["MAIN-view-V"].Objects.Single(o => o.ClassName == "UILayoutGuide");
        await Assert.That(Str(guide["UILayoutGuideIdentifier"])).IsEqualTo("UIViewSafeAreaLayoutGuide");
        await Assert.That(nibs["SECOND-view-V2"].Objects.Any(o => o.ClassName == "UILayoutGuide")).IsFalse();
    }

    [Test]
    public async Task ControlsCarryTheirKeys()
    {
        var view = CompileNavigation(out _)["MAIN-view-V"];
        var field = view.Objects.Single(o => o.ClassName == "UITextField");
        await Assert.That(field["UIBorderStyle"]).IsEqualTo(3);
        await Assert.That(field["UIAutocapitalizationType"]).IsEqualTo(1);
        await Assert.That(field["UIKeyboardType"]).IsEqualTo(7);
        await Assert.That(field["UIReturnKeyType"]).IsEqualTo(9);
        await Assert.That(Str(field["UIPlaceholder"])).IsEqualTo("Name");

        var stack = view.Objects.Single(o => o.ClassName == "UIStackView");
        await Assert.That(stack["UIStackViewAxis"]).IsEqualTo(1);
        await Assert.That(stack["UIStackViewSpacing"]).IsEqualTo(new NibArchive.Double(12));
        await Assert.That(((NibArchive.Obj)stack["UIStackViewArrangedSubviews"]!).Items.Count(i => i.Key == "UINibEncoderEmptyKey")).IsEqualTo(3);

        var button = view.Objects.First(o => o.ClassName == "UIButton");
        await Assert.That(button["UIButtonType"]).IsEqualTo(1);
        var states = (NibArchive.Obj)button["UIButtonStatefulContent"]!;
        var content = (NibArchive.Obj)states.Items.Where(i => i.Key == "UINibEncoderEmptyKey").ElementAt(1).Value!;
        await Assert.That(Str(content["UITitle"])).IsEqualTo("Greet");
    }

    [Test]
    public async Task XibActionsAndOutletsTargetFilesOwner()
    {
        var dir = Temp();
        var xib = Xib.Replace("<placeholder placeholderIdentifier=\"IBFilesOwner\" id=\"-1\"/>",
                "<placeholder placeholderIdentifier=\"IBFilesOwner\" id=\"-1\"><connections><outlet property=\"view\" destination=\"root\" id=\"o1\"/></connections></placeholder>")
            .Replace("<label text=\"Hello\"", "<switch on=\"YES\" id=\"SW\"><rect key=\"frame\" x=\"1\" y=\"2\" width=\"51\" height=\"31\"/><connections><action selector=\"flip:\" destination=\"-1\" eventType=\"valueChanged\" id=\"a1\"/></connections></switch><label text=\"Hello\"");
        InterfaceBuilder.Compile(Write(dir, "Owner.xib", xib), dir);
        var nib = NibArchive.Read(File.ReadAllBytes(Path.Combine(dir, "Owner.nib")));
        var owner = nib.Objects.Single(o => o.ClassName == "UIProxyObject" && Str(o["UIProxiedObjectIdentifier"]) == "IBFilesOwner");
        var action = nib.Objects.Single(o => o.ClassName == "UIRuntimeEventConnection");
        await Assert.That(action["UIDestination"]).IsSameReferenceAs(owner);
        await Assert.That(action["UIEventMask"]).IsEqualTo(4096);
        await Assert.That(nib.Objects.Any(o => o.ClassName == "UIRuntimeOutletConnection" && Str(o["UILabel"]) == "view" && o["UISource"] == owner)).IsTrue();
        // A switch's frame is restored after decoding, as Interface Builder does.
        var pair = nib.Objects.Single(o => o.ClassName == "UINibKeyValuePair");
        await Assert.That(Str(((NibArchive.Obj)pair["UIValue"]!)["NS.rectval"])).IsEqualTo("{{1, 2}, {51, 31}}");
    }

    [Test]
    public async Task TabBarControllersEmbedTheirTabs()
    {
        var dir = Temp();
        const string tabs = """
            <?xml version="1.0" encoding="UTF-8"?>
            <document type="com.apple.InterfaceBuilder3.CocoaTouch.Storyboard.XIB" version="3.0" initialViewController="T">
                <scenes>
                    <scene sceneID="a"><objects>
                        <viewController id="A" customClass="FirstViewController" sceneMemberID="viewController">
                            <view key="view" id="VA"><rect key="frame" x="0" y="0" width="320" height="480"/></view>
                            <tabBarItem key="tabBarItem" title="First" image="First" id="IA"/>
                        </viewController>
                    </objects></scene>
                    <scene sceneID="t"><objects>
                        <tabBarController id="T" sceneMemberID="viewController">
                            <tabBar key="tabBar" contentMode="scaleToFill" id="TB"><rect key="frame" x="0" y="431" width="320" height="49"/></tabBar>
                            <connections><segue destination="A" kind="relationship" relationship="viewControllers" id="r"/></connections>
                        </tabBarController>
                    </objects></scene>
                </scenes>
            </document>
            """;
        InterfaceBuilder.Compile(Write(dir, "Tabs.storyboard", tabs), dir);
        var sb = Path.Combine(dir, "Tabs.storyboardc");
        await Assert.That(Directory.GetFiles(sb, "*.nib").Select(Path.GetFileName).Order()).IsEquivalentTo(new[] { "A-view-VA.nib", "UITabBarController-T.nib" });
        var nib = NibArchive.Read(File.ReadAllBytes(Path.Combine(sb, "UITabBarController-T.nib")));
        var tabBar = nib.Objects.Single(o => o.ClassName == "UITabBar");
        var item = nib.Objects.Single(o => o.ClassName == "UITabBarItem");
        await Assert.That(((NibArchive.Obj)tabBar["UIItems"]!)["UINibEncoderEmptyKey"]).IsSameReferenceAs(item);
        await Assert.That(Str(item["UITitle"])).IsEqualTo("First");
    }

    [Test]
    public async Task UnsupportedSeguesAndRecognizersAreErrors()
    {
        var dir = Temp();
        // An unwind segue needs its action.
        await Assert.That(() => InterfaceBuilder.Compile(Write(dir, "Unwind.storyboard", Navigation.Replace("kind=\"show\"", "kind=\"unwind\"")), dir))
            .Throws<InterfaceBuilder.CompileError>();
        var pan = Navigation.Replace("<placeholder placeholderIdentifier=\"IBFirstResponder\"", "<panGestureRecognizer minimumNumberOfTouches=\"1\" id=\"PAN\"/><placeholder placeholderIdentifier=\"IBFirstResponder\"");
        await Assert.That(() => InterfaceBuilder.Compile(Write(dir, "Pan.storyboard", pan), dir))
            .Throws<InterfaceBuilder.CompileError>();
    }

    // test/ios/IBTest/Gaps.storyboard: checked at run time by the app, and against Apple's ibtool output.
    static Dictionary<string, NibArchive> CompileGaps()
    {
        var dir = Temp();
        var source = Path.Combine(Harness.RepoRoot, "test", "ios", "IBTest", "Gaps.storyboard");
        InterfaceBuilder.Compile(Write(dir, "Gaps.storyboard", File.ReadAllText(source)), dir);
        return Directory.GetFiles(Path.Combine(dir, "Gaps.storyboardc"), "*.nib")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f)!, f => NibArchive.Read(File.ReadAllBytes(f)));
    }

    [Test]
    public async Task PrototypeCellsCompileToTheirOwnNibs()
    {
        var nibs = CompileGaps();
        var table = nibs["TVC-view-TV"].Objects.Single(o => o.ClassName == "UITableView");
        var prototypes = (NibArchive.Obj)table["UITableViewCellPrototypeNibs"]!;
        var reuseIds = prototypes.Items.Where(i => i.Value is NibArchive.Obj { ClassName: "NSString" }).Select(i => Str(i.Value)).ToList();
        await Assert.That(reuseIds).IsEquivalentTo(new[] { "Cell", "Detail" });
        var uinib = prototypes.Items.Select(i => i.Value).OfType<NibArchive.Obj>().First(o => o.ClassName == "UINib");
        var cellNib = NibArchive.Read((byte[])((NibArchive.Obj)uinib["archiveData"]!)["NS.bytes"]!);
        var cell = cellNib.Objects.Single(o => o.ClassName == "UITableViewCell");
        await Assert.That(Str(cell["UIReuseIdentifier"])).IsEqualTo("Cell");
        await Assert.That(((NibArchive.Obj)cell["UITextLabel"]!).ClassName).IsEqualTo("UITableViewLabel");
        // The cell's unwind segue lives in the controller nib: reached through an upstream placeholder.
        var proxies = cellNib.Objects.Where(o => o.ClassName == "UIProxyObject").Select(o => Str(o["UIProxiedObjectIdentifier"])).ToList();
        await Assert.That(proxies).Contains("UpstreamPlaceholder-1");
        await Assert.That(cellNib.Objects.Any(o => o.ClassName == "UIRuntimeOutletConnection" && Str(o["UILabel"]) == "selectionSegueTemplate")).IsTrue();
        var detailNib = NibArchive.Read((byte[])((NibArchive.Obj)prototypes.Items.Select(i => i.Value).OfType<NibArchive.Obj>()
            .Where(o => o.ClassName == "UINib").Last()["archiveData"]!)["NS.bytes"]!);
        var detail = detailNib.Objects.Single(o => o.ClassName == "UITableViewCell");
        await Assert.That(detail["UITableViewCellStyle"]).IsEqualTo(1);
        await Assert.That(detail["UIAccessoryType"]).IsEqualTo(1);
        await Assert.That(detail["UISelectionStyle"]).IsEqualTo(0);

        var controller = nibs["UITableViewController-TVC"];
        var unwind = controller.Objects.Single(o => o.ClassName == "UIStoryboardUnwindSegueTemplate");
        await Assert.That(Str(unwind["UIAction"])).IsEqualTo("backToGaps:");
    }

    [Test]
    public async Task CollectionViewsStaticTablesAndReferencesCompile()
    {
        var nibs = CompileGaps();
        var layout = nibs["CVC-view-CV"].Objects.Single(o => o.ClassName == "UICollectionViewFlowLayout");
        await Assert.That(layout["UILineSpacing"]).IsEqualTo(4f);
        await Assert.That(layout["UIInteritemSpacing"]).IsEqualTo(6f);
        await Assert.That(layout["UIScrollDirection"]).IsEqualTo(1);

        var settings = nibs["STC-view-STV"];
        var source = settings.Objects.Single(o => o.ClassName == "UITableViewDataSource");
        await Assert.That(settings.Objects.Any(o => o.ClassName == "UIRuntimeOutletConnection" && Str(o["UILabel"]) == "staticDataSource" && o["UIDestination"] == source)).IsTrue();
        var section = settings.Objects.Single(o => o.ClassName == "UITableViewSection");
        await Assert.That(Str(section["UITableSectionHeaderTitle"])).IsEqualTo("General");
        await Assert.That(Str(section["UITableSectionFooterTitle"])).IsEqualTo("Footer");

        var reference = nibs["UIViewController-REF"].Objects.Single(o => o.ClassName == "UIStoryboardViewControllerPlaceholder");
        await Assert.That(reference["UIStoryboardIdentifier"]).IsNotNull();
    }

    [Test]
    public async Task RecognizersConfigurationsTextStylesAndCatalogColorsCompile()
    {
        var nibs = CompileGaps();
        var view = nibs["MAIN-view-V"];
        var config = view.Objects.Single(o => o.ClassName == "UIButtonConfiguration");
        await Assert.That(config["UIButtonConfigurationButtonStyle"]).IsEqualTo(3);
        await Assert.That(Str(((NibArchive.Obj)config["UIButtonConfigurationTitle"]!)["NSString"])).IsEqualTo("Filled");
        var label = view.Objects.Single(o => o.ClassName == "UILabel");
        await Assert.That(label["UIAdjustsFontForContentSizeCategory"]).IsEqualTo(true);
        await Assert.That(Str(((NibArchive.Obj)label["UIFont"]!)["UIIBTextStyle"])).IsEqualTo("UICTFontTextStyleBody");
        var color = (NibArchive.Obj)label["UITextColor"]!;
        await Assert.That(Str(color["UIDynamicCatalogName"])).IsEqualTo("Brand");
        await Assert.That(color["UIRed"]).IsEqualTo(0.2f);
        // The scene's tap recognizer is kept alive by the controller, and the view reaches it as an external object.
        var controller = nibs["UINavigationController-NAV"];
        var tap = controller.Objects.Single(o => o.ClassName == "UITapGestureRecognizer");
        await Assert.That(controller.Objects.Any(o => o.ClassName == "UIRuntimeEventConnection" && Str(o["UILabel"]) == "tapped:" && o["UISource"] == tap)).IsTrue();
        await Assert.That(view.Objects.Any(o => o.ClassName == "UIRuntimeOutletCollectionConnection" && Str(o["UILabel"]) == "gestureRecognizers")).IsTrue();
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
