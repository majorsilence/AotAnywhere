using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Compiles Interface Builder documents to the files UIKit loads at run time, for the content
/// launch screens and simple layouts use: <c>.xib</c> to a <c>.nib</c>, and <c>.storyboard</c> to a
/// <c>.storyboardc</c> folder (an Info.plist mapping view controllers to nibs, a nib per view
/// controller and one per controller's view).
///
/// The nib format and UIKit's keyed-coding keys follow the MIT-licensed WinObjC xib2nib
/// (tools/vsimporter/xib2nib). Supported: view, imageView and label - geometry, autoresizing, content
/// mode, the common flags, colors (RGB, white, named system colors), fonts, text attributes, images
/// and Auto Layout constraints. Anything else (segues, other controls, outlets to custom classes)
/// is reported as an error rather than compiled into a nib that would misbehave.
/// </summary>
public static class InterfaceBuilder
{
    public sealed class CompileError(string message) : Exception(message);

    /// <summary>Compiles <paramref name="input"/> into <paramref name="outputDirectory"/>; returns the paths written.</summary>
    public static List<string> Compile(string input, string outputDirectory)
    {
        var doc = XDocument.Load(input, LoadOptions.None);
        var root = doc.Root ?? throw new CompileError("empty document");
        string name = Path.GetFileNameWithoutExtension(input);
        Directory.CreateDirectory(outputDirectory);
        var written = new List<string>();

        if (input.EndsWith(".storyboard", StringComparison.OrdinalIgnoreCase))
        {
            string dir = Path.Combine(outputDirectory, name + ".storyboardc");
            Directory.CreateDirectory(dir);
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            string? entry = null;
            string? initial = (string?)root.Attribute("initialViewController");
            foreach (var controller in root.Descendants("scene").SelectMany(s => s.Element("objects")?.Elements() ?? []).Where(IsViewController))
            {
                string id = (string)controller.Attribute("id")!;
                string nibName = (string?)controller.Attribute("storyboardIdentifier") ?? $"{ControllerClass(controller)}-{id}";
                map[nibName] = nibName;
                if (id == initial)
                    entry = nibName;
                foreach (var (file, data) in CompileScene(controller, id, nibName))
                {
                    string path = Path.Combine(dir, file);
                    File.WriteAllBytes(path, data);
                    written.Add(path);
                }
            }
            var info = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["UIStoryboardVersion"] = 1L,
                ["UIViewControllerIdentifiersToNibNames"] = map,
            };
            if (entry is not null)
                info["UIStoryboardDesignatedEntryPointIdentifier"] = entry;
            string infoPath = Path.Combine(dir, "Info.plist");
            File.WriteAllBytes(infoPath, Plist.WriteXml(info));
            written.Add(infoPath);
        }
        else
        {
            var objects = root.Element("objects") ?? throw new CompileError("no <objects> in the document");
            var b = new NibBuilder(root);
            var owner = b.Proxy("IBFilesOwner");
            var firstResponder = b.Proxy("IBFirstResponder");
            var top = new List<NibArchive.Obj> { owner, firstResponder };
            foreach (var el in objects.Elements())
            {
                if (el.Name.LocalName == "placeholder")
                    continue;
                top.Add(b.Element(el));
            }
            b.Connections(objects, owner);
            string path = Path.Combine(outputDirectory, name + ".nib");
            File.WriteAllBytes(path, b.Build(top));
            written.Add(path);
        }
        return written;
    }

    static bool IsViewController(XElement e) =>
        e.Name.LocalName.EndsWith("Controller", StringComparison.Ordinal) && (string?)e.Attribute("sceneMemberID") == "viewController";

    static string ControllerClass(XElement controller) => controller.Name.LocalName switch
    {
        "viewController" => "UIViewController",
        var other => throw new CompileError($"'{other}' scenes are not supported (only viewController)"),
    };

    // A scene: the controller's nib, and its view's nib (loaded when the controller loads its view).
    static IEnumerable<(string File, byte[] Data)> CompileScene(XElement controller, string id, string nibName)
    {
        if (controller.Descendants("segue").Any())
            throw new CompileError("segues are not supported");
        var viewElement = controller.Elements("view").FirstOrDefault(v => (string?)v.Attribute("key") == "view");
        string? viewNib = null;
        if (viewElement is not null)
        {
            string viewId = (string)viewElement.Attribute("id")!;
            viewNib = $"{id}-view-{viewId}";
            var vb = new NibBuilder(controller.Document!.Root!);
            var owner = vb.Proxy("IBFilesOwner");
            var firstResponder = vb.Proxy("IBFirstResponder");
            var view = vb.Element(viewElement);
            vb.Connect("view", owner, view);
            yield return (viewNib + ".nib", vb.Build([owner, firstResponder, view]));
        }

        var b = new NibBuilder(controller.Document!.Root!);
        var controllerOwner = b.Proxy("IBFilesOwner");
        var controllerResponder = b.Proxy("IBFirstResponder");
        var storyboard = b.Proxy("UIStoryboardPlaceholder");
        string className = ControllerClass(controller);
        var swapper = new NibArchive.Obj("UIClassSwapper")
            .Add("UIExternalObjectsTableForViewLoading", NibBuilder.Dictionary())
            .Add("UIClassName", NibBuilder.String((string?)controller.Attribute("customClass") ?? className))
            .Add("UIOriginalClassName", NibBuilder.String(className));
        if (viewNib is not null)
            swapper.Add("UINibName", NibBuilder.String(viewNib));
        b.Track(swapper);
        b.Connect("storyboard", swapper, storyboard);
        b.Connect("sceneViewController", controllerOwner, swapper);
        yield return (nibName + ".nib", b.Build([controllerOwner, controllerResponder, storyboard, swapper]));
    }

    /// <summary>Builds one nib's object graph from Interface Builder XML.</summary>
    sealed class NibBuilder(XElement document)
    {
        readonly List<NibArchive.Obj> all = [];
        readonly List<NibArchive.Obj> connections = [];
        readonly Dictionary<string, NibArchive.Obj> byId = new(StringComparer.Ordinal);
        readonly List<(XElement Constraint, NibArchive.Obj Owner, NibArchive.Obj OwnerView)> pendingConstraints = [];
        readonly List<NibArchive.Obj> constraints = [];

        public NibArchive.Obj Proxy(string identifier)
        {
            var proxy = new NibArchive.Obj("UIProxyObject").Add("UIProxiedObjectIdentifier", String(identifier));
            all.Add(proxy);
            byId[identifier switch { "IBFilesOwner" => "-1", "IBFirstResponder" => "-2", _ => identifier }] = proxy;
            return proxy;
        }

        public void Track(NibArchive.Obj o) => all.Add(o);

        public void Connect(string label, NibArchive.Obj source, NibArchive.Obj destination) =>
            connections.Add(new NibArchive.Obj("UIRuntimeOutletConnection")
                .Add("UILabel", String(label)).Add("UISource", source).Add("UIDestination", destination));

        // <connections><outlet property="x" destination="id"/></connections> on File's Owner.
        public void Connections(XElement objects, NibArchive.Obj owner)
        {
            foreach (var placeholder in objects.Elements("placeholder").Where(p => (string?)p.Attribute("placeholderIdentifier") == "IBFilesOwner"))
                foreach (var outlet in placeholder.Element("connections")?.Elements("outlet") ?? [])
                    if (byId.TryGetValue((string)outlet.Attribute("destination")!, out var dest))
                        Connect((string)outlet.Attribute("property")!, owner, dest);
            foreach (var other in objects.Descendants("connections").SelectMany(c => c.Elements()).Where(c => c.Name.LocalName != "outlet" || c.Parent?.Parent?.Name.LocalName != "placeholder"))
                throw new CompileError($"'{other.Name.LocalName}' connections are not supported");
        }

        public byte[] Build(List<NibArchive.Obj> topLevel)
        {
            foreach (var (c, owner, ownerView) in pendingConstraints)
                constraints.Add(Constraint(c, ownerView));
            foreach (var group in pendingConstraints.Zip(constraints).GroupBy(p => p.First.Owner, ReferenceEqualityComparer.Instance))
                ((NibArchive.Obj)group.Key!).Add("UIViewAutolayoutConstraints", Array("NSMutableArray", group.Select(g => g.Second)));
            var rootObj = new NibArchive.Obj("NSObject")
                .Add("UINibTopLevelObjectsKey", Array("NSArray", topLevel))
                .Add("UINibObjectsKey", Array("NSArray", all.Concat(constraints)))
                .Add("UINibConnectionsKey", Array("NSArray", connections));
            var empty = Array("NSArray", []);
            rootObj.Add("UINibVisibleWindowsKey", empty).Add("UINibAccessibilityConfigurationsKey", empty)
                .Add("UINibTraitStorageListsKey", empty).Add("UINibKeyValuePairsKey", empty);
            return NibArchive.Write(rootObj);
        }

        public NibArchive.Obj Element(XElement e) => e.Name.LocalName switch
        {
            "view" => View(e, "UIView"),
            "imageView" => ImageView(e),
            "label" => Label(e),
            var other => throw new CompileError($"'{other}' elements are not supported (supported: view, imageView, label)"),
        };

        NibArchive.Obj View(XElement e, string defaultClass, Action<NibArchive.Obj>? specific = null)
        {
            var o = new NibArchive.Obj((string?)e.Attribute("customClass") ?? defaultClass);
            all.Add(o);
            if ((string?)e.Attribute("id") is { } id)
                byId[id] = o;

            var frame = e.Elements("rect").FirstOrDefault(r => (string?)r.Attribute("key") == "frame");
            double x = Num(frame, "x"), y = Num(frame, "y"), w = Num(frame, "width"), h = Num(frame, "height");
            o.Add("UIBounds", Geometry(0, 0, w, h)).Add("UICenter", Geometry(x + w / 2, y + h / 2));

            var subviews = e.Element("subviews")?.Elements().Select(Element).ToList();
            if (subviews is { Count: > 0 })
                o.Add("UISubviews", Array("NSMutableArray", subviews));
            if (ColorFor(e, "backgroundColor") is { } background)
                o.Add("UIBackgroundColor", background);

            o.Add("UIDeepDrawRect", true);
            if (Bool(e, "opaque", true))
                o.Add("UIOpaque", true);
            if (Bool(e, "hidden", false))
                o.Add("UIHidden", true);
            if (!Bool(e, "userInteractionEnabled", true))
                o.Add("UIUserInteractionDisabled", true);
            if (Bool(e, "multipleTouchEnabled", false))
                o.Add("UIMultipleTouchEnabled", true);
            o.Add("UIAutoresizeSubviews", true);
            int mask = AutoresizingMask(e.Elements("autoresizingMask").FirstOrDefault());
            if (mask != 0)
                o.Add("UIAutoresizingMask", mask);
            int contentMode = ContentMode((string?)e.Attribute("contentMode"));
            if (contentMode != 0)
                o.Add("UIContentMode", contentMode);
            if (Bool(e, "clipsSubviews", false))
                o.Add("UIClipsToBounds", true);
            if ((string?)e.Attribute("tag") is { } tag)
                o.Add("UITag", int.Parse(tag, CultureInfo.InvariantCulture));
            // fixedFrame="YES": Interface Builder keeps the frame (the autoresizing mask applies)
            // even when translatesAutoresizingMaskIntoConstraints is NO and no constraints place the view.
            if (!Bool(e, "translatesAutoresizingMaskIntoConstraints", true) && !Bool(e, "fixedFrame", false))
                o.Add("UIViewDoesNotTranslateAutoresizingMaskIntoConstraints", true);
            int hh = (int)Num(e, "horizontalHuggingPriority", 250), vh = (int)Num(e, "verticalHuggingPriority", 250);
            if (hh != 250 || vh != 250)
                o.Add("UIViewContentHuggingPriority", String($"{{{hh}, {vh}}}"));
            int hc = (int)Num(e, "horizontalCompressionResistancePriority", 750), vc = (int)Num(e, "verticalCompressionResistancePriority", 750);
            if (hc != 750 || vc != 750)
                o.Add("UIViewContentCompressionResistancePriority", String($"{{{hc}, {vc}}}"));
            o.Add("UIViewSemanticContentAttribute", 0);

            specific?.Invoke(o);

            foreach (var c in e.Element("constraints")?.Elements("constraint") ?? [])
                pendingConstraints.Add((c, o, o));
            return o;
        }

        NibArchive.Obj ImageView(XElement e) => View(e, "UIImageView", o =>
        {
            if ((string?)e.Attribute("image") is { Length: > 0 } image)
                o.Add("UIImage", new NibArchive.Obj("UIImageNibPlaceholder")
                    .Add("UIImageWidth", 1f).Add("UIImageHeight", 1f).Add("UIResourceName", String(image)));
        });

        NibArchive.Obj Label(XElement e) => View(e, "UILabel", o =>
        {
            if ((string?)e.Attribute("text") is { } text)
                o.Add("UIText", String(text));
            var font = e.Elements("fontDescription").FirstOrDefault();
            double pointSize = Num(font, "pointSize", 17);
            o.Add("UIFont", Font(font));
            o.Add("UITextColor", ColorFor(e, "textColor") ?? Color(0, 0, 0, 1));
            if (ColorFor(e, "highlightedColor") is { } highlighted)
                o.Add("UIHighlightedColor", highlighted);
            o.Add("UIShadowOffset", Geometry(0, -1));
            int alignment = (string?)e.Attribute("textAlignment") switch
            {
                null or "natural" => 4, "left" => 0, "center" => 1, "right" => 2, "justified" => 3,
                var a => throw new CompileError($"unknown textAlignment '{a}'"),
            };
            o.Add("UITextAlignment", alignment);
            int lineBreak = (string?)e.Attribute("lineBreakMode") switch
            {
                null or "tailTruncation" => 4, "wordWrap" => 0, "characterWrap" => 1, "clip" => 2, "headTruncation" => 3, "middleTruncation" => 5,
                var m => throw new CompileError($"unknown lineBreakMode '{m}'"),
            };
            if (lineBreak != 4)
                o.Add("UILineBreakMode", lineBreak);
            if ((string?)e.Attribute("numberOfLines") is { } lines)
                o.Add("UINumberOfLines", int.Parse(lines, CultureInfo.InvariantCulture));
            if ((string?)e.Attribute("minimumFontSize") is { } minimum)
            {
                float min = float.Parse(minimum, CultureInfo.InvariantCulture);
                o.Add("UIAdjustsFontSizeToFit", true).Add("UIMinimumFontSize", min).Add("UIMinimumScaleFactor", (float)(min / pointSize));
            }
            else if ((string?)e.Attribute("minimumScaleFactor") is { } scale)
                o.Add("UIAdjustsFontSizeToFit", Bool(e, "adjustsFontSizeToFit", true)).Add("UIMinimumScaleFactor", float.Parse(scale, CultureInfo.InvariantCulture));
        });

        // System fonts by size and weight; named fonts by name (keys as in WinObjC's UIFont).
        static NibArchive.Obj Font(XElement? f)
        {
            double size = Num(f, "pointSize", 17);
            string type = (string?)f?.Attribute("type") ?? "system";
            var o = new NibArchive.Obj("UIFont");
            if ((string?)f?.Attribute("name") is { } fontName)
            {
                o.Add("UIFontName", String(fontName)).Add("UIFontPointSize", new NibArchive.Double(size));
                return o;
            }
            (string Name, int Traits) = type switch
            {
                "system" => (".SFUI-Regular", 0),
                "boldSystem" => (".SFUI-Bold", 2),
                "italicSystem" => (".SFUI-Italic", 1),
                var t => throw new CompileError($"fontDescription type '{t}' is not supported"),
            };
            o.Add("UIFontName", String(Name)).Add("UIFontPointSize", new NibArchive.Double(size))
                .Add("UIFontTraits", Traits).Add("UISystemFont", true);
            return o;
        }

        NibArchive.Obj? ColorFor(XElement e, string key)
        {
            var c = e.Elements("color").FirstOrDefault(x => (string?)x.Attribute("key") == key);
            return c is null ? null : ParseColor(c);
        }

        NibArchive.Obj ParseColor(XElement c)
        {
            if ((string?)c.Attribute("cocoaTouchSystemColor") is { } legacy)
                return SystemColor(legacy, LegacySystemColorFallback(legacy));
            if ((string?)c.Attribute("systemColor") is { } system)
            {
                // Storyboards record the named color's light-mode value in <resources>.
                var fallback = document.Element("resources")?.Elements("systemColor")
                    .FirstOrDefault(s => (string?)s.Attribute("name") == system)?.Element("color");
                return SystemColor(system, fallback is null ? (0, 0, 0, 1) : Components(fallback));
            }
            if ((string?)c.Attribute("name") is { } named)
                throw new CompileError($"named colors from asset catalogs ('{named}') are not supported");
            var (r, g, b, a) = Components(c);
            return Color(r, g, b, a);
        }

        static (double R, double G, double B, double A) Components(XElement c)
        {
            double a = Num(c, "alpha", 1);
            if (c.Attribute("white") is not null)
            {
                double white = Num(c, "white");
                return (white, white, white, a);
            }
            return (Num(c, "red"), Num(c, "green"), Num(c, "blue"), a);
        }

        static (double, double, double, double) LegacySystemColorFallback(string name) => name switch
        {
            "darkTextColor" => (0, 0, 0, 1),
            "lightTextColor" => (1, 1, 1, 0.6),
            "groupTableViewBackgroundColor" => (0.94, 0.94, 0.96, 1),
            "clearColor" => (0, 0, 0, 0),
            _ => (0, 0, 0, 1),
        };

        static NibArchive.Obj SystemColor(string name, (double R, double G, double B, double A) fallback)
        {
            var o = Color(fallback.R, fallback.G, fallback.B, fallback.A);
            o.Items.Insert(0, ("UISystemColorName", String(name)));
            return o;
        }

        static NibArchive.Obj Color(double r, double g, double b, double a) => new NibArchive.Obj("UIColor")
            .Add("UIColorComponentCount", 4)
            .Add("UIRed", (float)r).Add("UIRed-Double", new NibArchive.Double(r))
            .Add("UIGreen", (float)g).Add("UIGreen-Double", new NibArchive.Double(g))
            .Add("UIBlue", (float)b).Add("UIBlue-Double", new NibArchive.Double(b))
            .Add("UIAlpha", (float)a)
            .Add("NSRGB", Encoding.ASCII.GetBytes(FormattableString.Invariant($"{r:0.###} {g:0.###} {b:0.###}")))
            .Add("NSColorSpace", 2);

        NibArchive.Obj Constraint(XElement c, NibArchive.Obj ownerView)
        {
            NibArchive.Obj Item(string? id) => id is null ? ownerView
                : byId.TryGetValue(id, out var o) ? o : throw new CompileError($"constraint references unknown item '{id}'");
            int Attr(string? name) => name switch
            {
                "left" => 1, "right" => 2, "top" => 3, "bottom" => 4, "leading" => 5, "trailing" => 6,
                "width" => 7, "height" => 8, "centerX" => 9, "centerY" => 10, "lastBaseline" or "baseline" => 11, "firstBaseline" => 12,
                "leftMargin" => 13, "rightMargin" => 14, "topMargin" => 15, "bottomMargin" => 16, "leadingMargin" => 17,
                "trailingMargin" => 18, "centerXWithinMargins" => 19, "centerYWithinMargins" => 20, null => 0,
                var a => throw new CompileError($"unknown constraint attribute '{a}'"),
            };
            var o = new NibArchive.Obj("NSLayoutConstraint");
            int first = Attr((string?)c.Attribute("firstAttribute"));
            o.Add("NSFirstItem", Item((string?)c.Attribute("firstItem"))).Add("NSFirstAttributeV2", first).Add("NSFirstAttribute", first);
            string? relation = (string?)c.Attribute("relation");
            if (relation is not null)
                o.Add("NSRelation", relation switch { "lessThanOrEqual" => -1, "greaterThanOrEqual" => 1, _ => 0 });
            if ((string?)c.Attribute("multiplier") is { } multiplier)
                o.Add("NSMultiplier", new NibArchive.Double((float)Fraction(multiplier)));
            int second = Attr((string?)c.Attribute("secondAttribute"));
            if (c.Attribute("secondItem") is not null || second != 0)
            {
                o.Add("NSSecondItem", Item((string?)c.Attribute("secondItem")));
                o.Add("NSSecondAttributeV2", second).Add("NSSecondAttribute", second);
            }
            if (Bool(c, "symbolic", false))
                o.Add("NSSymbolicConstant", String("NSSpace"));
            else if (c.Attribute("constant") is not null && Num(c, "constant") != 0)
                o.Add("NSConstantV2", new NibArchive.Double(Num(c, "constant"))).Add("NSConstant", new NibArchive.Double(Num(c, "constant")));
            o.Add("NSShouldBeArchived", true);
            if ((string?)c.Attribute("priority") is { } priority && int.Parse(priority, CultureInfo.InvariantCulture) != 1000)
                o.Add("NSPriority", int.Parse(priority, CultureInfo.InvariantCulture));
            return o;
        }

        // --- value helpers ----------------------------------------------------------------

        public static NibArchive.Obj String(string s) => new NibArchive.Obj("NSString").Add("NS.bytes", Encoding.UTF8.GetBytes(s));

        public static NibArchive.Obj Dictionary() => new NibArchive.Obj("NSDictionary").Add("NSInlinedValue", true);

        public static NibArchive.Obj Array(string className, IEnumerable<NibArchive.Obj> items)
        {
            var o = new NibArchive.Obj(className).Add("NSInlinedValue", true);
            foreach (var item in items)
                o.Add("UINibEncoderEmptyKey", item);
            return o;
        }

        // CGRect/CGPoint values: a 0x07 (double) marker, then the components as doubles.
        static byte[] Geometry(params double[] values)
        {
            var bytes = new byte[1 + values.Length * 8];
            bytes[0] = 7;
            for (int i = 0; i < values.Length; i++)
                BitConverter.GetBytes(values[i]).CopyTo(bytes, 1 + i * 8);
            return bytes;
        }

        static int AutoresizingMask(XElement? m)
        {
            if (m is null)
                return 36; // Interface Builder's default: flexible right and bottom margins
            int mask = 0;
            if (Bool(m, "flexibleMinX", false)) mask |= 1;
            if (Bool(m, "widthSizable", false)) mask |= 2;
            if (Bool(m, "flexibleMaxX", false)) mask |= 4;
            if (Bool(m, "flexibleMinY", false)) mask |= 8;
            if (Bool(m, "heightSizable", false)) mask |= 16;
            if (Bool(m, "flexibleMaxY", false)) mask |= 32;
            return mask;
        }

        static int ContentMode(string? mode) => mode switch
        {
            null or "scaleToFill" => 0, "scaleAspectFit" => 1, "scaleAspectFill" => 2, "redraw" => 3, "center" => 4, "top" => 5,
            "bottom" => 6, "left" => 7, "right" => 8, "topLeft" => 9, "topRight" => 10, "bottomLeft" => 11, "bottomRight" => 12,
            var m => throw new CompileError($"unknown contentMode '{m}'"),
        };

        static bool Bool(XElement? e, string name, bool fallback) => (string?)e?.Attribute(name) switch
        {
            "YES" => true,
            "NO" => false,
            _ => fallback,
        };

        static double Num(XElement? e, string name, double fallback = 0) =>
            (string?)e?.Attribute(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

        static double Fraction(string s)
        {
            int slash = s.IndexOf('/');
            return slash < 0 ? double.Parse(s, CultureInfo.InvariantCulture)
                : double.Parse(s[..slash], CultureInfo.InvariantCulture) / double.Parse(s[(slash + 1)..], CultureInfo.InvariantCulture);
        }
    }
}
