using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Compiles Interface Builder documents to the files UIKit loads at run time: <c>.xib</c> to a
/// <c>.nib</c>, and <c>.storyboard</c> to a <c>.storyboardc</c> folder (an Info.plist mapping view
/// controller identifiers to nibs, a nib per view controller UIKit can instantiate, and one per
/// controller's view).
///
/// The nib format and most of UIKit's keyed-coding keys follow the MIT-licensed WinObjC xib2nib
/// (tools/vsimporter/xib2nib); the encodings were checked against Apple-compiled nibs of the same
/// documents. Supported:
/// <list type="bullet">
/// <item>views: view, imageView, label, button (state-based), textField, textView, switch, slider,
/// segmentedControl, activityIndicatorView, scrollView, stackView;</item>
/// <item>geometry, autoresizing, Auto Layout constraints (including the safe area layout guide),
/// colors (RGB, white, named system colors), fonts, images;</item>
/// <item>outlets, actions (target/action with the control event), view controllers, navigation and
/// tab bar controllers with their items, bar button items, and show / show detail / modal
/// segues.</item>
/// </list>
/// Anything else is reported as an error naming the element rather than compiled into a nib that
/// would misbehave.
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
            var files = new Storyboard(root).Compile(out var info);
            foreach (var (file, data) in files)
            {
                string path = Path.Combine(dir, file);
                File.WriteAllBytes(path, data);
                written.Add(path);
            }
            string infoPath = Path.Combine(dir, "Info.plist");
            File.WriteAllBytes(infoPath, Plist.WriteXml(info));
            written.Add(infoPath);
        }
        else
        {
            var objects = root.Element("objects") ?? throw new CompileError("no <objects> in the document");
            var b = new NibBuilder(root);
            var top = new List<NibArchive.Obj> { b.Proxy("IBFilesOwner", "-1"), b.Proxy("IBFirstResponder", "-2") };
            foreach (var placeholder in objects.Elements("placeholder"))
            {
                string identifier = (string?)placeholder.Attribute("placeholderIdentifier") ?? "";
                if (identifier is not ("IBFilesOwner" or "IBFirstResponder"))
                    throw new CompileError($"placeholder '{identifier}' is not supported");
                b.Alias((string)placeholder.Attribute("id")!, identifier == "IBFilesOwner" ? "-1" : "-2");
                b.CollectConnections(placeholder, b.ById(identifier == "IBFilesOwner" ? "-1" : "-2"));
            }
            foreach (var el in objects.Elements())
                if (el.Name.LocalName != "placeholder")
                    top.Add(b.Element(el));
            string path = Path.Combine(outputDirectory, name + ".nib");
            File.WriteAllBytes(path, b.Build(top));
            written.Add(path);
        }
        return written;
    }

    // --- storyboards ---------------------------------------------------------------------

    /// <summary>
    /// A storyboard's scene graph. Controllers that UIKit instantiates by identifier (the initial one,
    /// segue destinations, and those with a storyboard ID) each get a nib; controllers reached by a
    /// relationship segue (a navigation controller's root, a tab bar controller's tabs) are encoded
    /// inside their parent's nib, as Interface Builder does. Every controller with a view gets a view
    /// nib, loaded when the controller loads its view; references from it to the controller or to a
    /// segue go through UIKit's external-object table ("UpstreamPlaceholder-N").
    /// </summary>
    sealed class Storyboard(XElement document)
    {
        readonly Dictionary<string, XElement> controllers = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> parentOf = new(StringComparer.Ordinal);
        readonly Dictionary<string, XElement> firstResponders = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> externals = new(StringComparer.Ordinal); // per controller: "controller" or a segue id

        public List<(string File, byte[] Data)> Compile(out Dictionary<string, object> info)
        {
            foreach (var scene in document.Descendants("scene"))
            {
                var objects = scene.Element("objects")?.Elements().ToList() ?? [];
                foreach (var controller in objects.Where(IsViewController))
                {
                    string id = (string)controller.Attribute("id")!;
                    controllers[id] = controller;
                    if (objects.FirstOrDefault(o => (string?)o.Attribute("placeholderIdentifier") == "IBFirstResponder") is { } fr)
                        firstResponders[id] = fr;
                    // Every scene also has an Exit placeholder (for unwind segues, which aren't supported).
                    foreach (var other in objects.Where(o => o != controller && o.Name.LocalName is not ("placeholder" or "exit")))
                        throw new CompileError($"scene-level '{other.Name.LocalName}' objects (outside the view controller) are not supported");
                }
            }
            foreach (var (id, controller) in controllers)
                foreach (var segue in Segues(controller).Where(s => (string?)s.Attribute("kind") == "relationship"))
                    parentOf[Target(segue)] = id;

            string? initial = (string?)document.Attribute("initialViewController");
            var owners = controllers.Keys.Where(id =>
                    id == initial || !parentOf.ContainsKey(id) || controllers[id].Attribute("storyboardIdentifier") is not null
                    || controllers.Values.SelectMany(Segues).Any(s => (string?)s.Attribute("kind") != "relationship" && Target(s) == id))
                .ToList();

            var files = new List<(string, byte[])>();
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var id in controllers.Keys)
                if (ViewOf(controllers[id]) is not null)
                    files.Add((ViewNibName(id) + ".nib", ViewNib(id)));
            foreach (var id in owners)
            {
                string nib = NibName(id);
                map[nib] = nib;
                files.Add((nib + ".nib", ControllerNib(id)));
            }
            info = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["UIStoryboardVersion"] = 1L,
                ["UIViewControllerIdentifiersToNibNames"] = map,
            };
            if (initial is not null)
                info["UIStoryboardDesignatedEntryPointIdentifier"] = NibName(initial);
            return files;
        }

        static bool IsViewController(XElement e) =>
            e.Name.LocalName.EndsWith("Controller", StringComparison.Ordinal) && (string?)e.Attribute("sceneMemberID") == "viewController";

        static string OriginalClass(XElement controller) => controller.Name.LocalName switch
        {
            "viewController" => "UIViewController",
            "navigationController" => "UINavigationController",
            "tabBarController" => "UITabBarController",
            var other => throw new CompileError($"'{other}' scenes are not supported (supported: viewController, navigationController, tabBarController)"),
        };

        string NibName(string id) =>
            (string?)Controller(id).Attribute("storyboardIdentifier") ?? $"{OriginalClass(Controller(id))}-{id}";

        XElement Controller(string id) =>
            controllers.TryGetValue(id, out var c) ? c : throw new CompileError($"no view controller with id '{id}'");

        static XElement? ViewOf(XElement controller) =>
            controller.Elements("view").FirstOrDefault(v => (string?)v.Attribute("key") == "view");

        string ViewNibName(string id) => $"{id}-view-{(string)ViewOf(Controller(id))!.Attribute("id")!}";

        // Every segue a controller is the source of: its own (manual and relationship) and those its view's controls trigger.
        static IEnumerable<XElement> Segues(XElement controller) =>
            (controller.Element("connections")?.Elements("segue") ?? [])
                .Concat(ViewOf(controller)?.Descendants("segue") ?? [])
                .Concat(controller.Element("navigationItem")?.Descendants("segue") ?? []);

        static string Target(XElement segue) => (string?)segue.Attribute("destination") ?? throw new CompileError("segue without a destination");

        // The view nib. File's Owner is the controller loading it; outlets and actions that point at the
        // controller, and segues the view's controls trigger, use upstream placeholders.
        byte[] ViewNib(string id)
        {
            var controller = Controller(id);
            var view = ViewOf(controller)!;
            var b = new NibBuilder(document);
            var owner = b.Proxy("IBFilesOwner", "-1");
            var top = new List<NibArchive.Obj> { owner, b.Proxy("IBFirstResponder", "-2") };
            if (firstResponders.TryGetValue(id, out var fr))
                b.Alias((string)fr.Attribute("id")!, "-2");

            var viewIds = view.DescendantsAndSelf().Select(e => (string?)e.Attribute("id")).Where(i => i is not null).ToHashSet();
            var outlets = controller.Element("connections")?.Elements("outlet").Where(o => viewIds.Contains((string?)o.Attribute("destination"))).ToList() ?? [];
            bool needsController = outlets.Count > 0 || view.Descendants("connections").Elements().Any(c => (string?)c.Attribute("destination") == id);
            var list = new List<string>();
            if (needsController)
                list.Add("controller");
            list.AddRange(view.Descendants("segue").Select(s => (string)s.Attribute("id")!));
            externals[id] = list;
            for (int i = 0; i < list.Count; i++)
            {
                var proxy = b.Proxy($"UpstreamPlaceholder-{i + 1}", list[i] == "controller" ? id : list[i]);
                top.Add(proxy);
            }

            var viewObj = b.Element(view);
            top.Add(viewObj);
            foreach (var outlet in outlets)
                b.Connect((string)outlet.Attribute("property")!, b.ById(id), b.ById((string)outlet.Attribute("destination")!));
            b.Connect("view", owner, viewObj);
            return b.Build(top);
        }

        // A controller's nib: the controller (and any it embeds), its items, and its segue templates.
        byte[] ControllerNib(string id)
        {
            var b = new NibBuilder(document);
            var owner = b.Proxy("IBFilesOwner", "-1");
            var top = new List<NibArchive.Obj> { owner, b.Proxy("IBFirstResponder", "-2") };
            foreach (var (controllerId, fr) in firstResponders)
                b.Alias((string)fr.Attribute("id")!, "-2");
            var storyboardProxies = new List<NibArchive.Obj>();
            var obj = EncodeController(id, b, null, storyboardProxies);
            top.InsertRange(2, storyboardProxies);
            top.Add(obj);
            b.Connect("sceneViewController", owner, obj);
            return b.Build(top);
        }

        NibArchive.Obj EncodeController(string id, NibBuilder b, NibArchive.Obj? parent, List<NibArchive.Obj> storyboardProxies)
        {
            var el = Controller(id);
            string original = OriginalClass(el);
            var o = new NibArchive.Obj("UIClassSwapper");
            b.Track(o, id);
            var storyboard = b.Proxy("UIStoryboardPlaceholder", null, track: true);
            storyboardProxies.Add(storyboard);
            b.Connect("storyboard", o, storyboard);

            var children = Segues(el).Where(s => (string?)s.Attribute("kind") == "relationship").ToList();
            foreach (var r in children)
                if ((string?)r.Attribute("relationship") is not ("rootViewController" or "viewControllers"))
                    throw new CompileError($"'{(string?)r.Attribute("relationship")}' relationships are not supported");
            var childObjs = children.Select(r => EncodeController(Target(r), b, o, storyboardProxies)).ToList();
            if (childObjs.Count > 0)
                o.Add("UIChildViewControllers", NibBuilder.Array("NSMutableArray", childObjs));

            NibArchive.Obj? navigationItem = null;
            if (el.Elements("navigationItem").FirstOrDefault(n => (string?)n.Attribute("key") == "navigationItem") is { } ni)
                o.Add("UINavigationItem", navigationItem = b.NavigationItem(ni, id));
            if (el.Elements("tabBarItem").FirstOrDefault(t => (string?)t.Attribute("key") == "tabBarItem") is { } ti)
                o.Add("UITabBarItem", b.TabBarItem(ti));

            // Segue templates, then the external objects the view nib refers to.
            var templates = new Dictionary<string, NibArchive.Obj>(StringComparer.Ordinal);
            foreach (var segue in Segues(el).Where(s => (string?)s.Attribute("kind") != "relationship"))
            {
                var t = SegueTemplate(segue);
                b.Track(t, (string)segue.Attribute("id")!);
                b.Connect("viewController", t, o);
                templates[(string)segue.Attribute("id")!] = t;
            }
            var table = new List<(string, NibArchive.Obj)>();
            if (ViewOf(el) is not null)
            {
                if (!externals.TryGetValue(id, out var list))
                    throw new CompileError($"internal error: view nib of '{id}' not compiled");
                for (int i = 0; i < list.Count; i++)
                    table.Add(($"UpstreamPlaceholder-{i + 1}", list[i] == "controller" ? o : templates[list[i]]));
            }
            if (ViewOf(el) is not null)
            {
                o.Add("UIExternalObjectsTableForViewLoading", NibBuilder.Dictionary(table));
                o.Add("UINibName", NibBuilder.String(ViewNibName(id)));
            }
            if (parent is not null)
                o.Add("UIParentViewController", parent);
            if ((string?)el.Attribute("storyboardIdentifier") is { } sid)
                o.Add("UIStoryboardIdentifier", NibBuilder.String(sid));
            if (templates.Count > 0)
                o.Add("UIStoryboardSegueTemplates", NibBuilder.Array("NSArray", templates.Values));

            switch (el.Name.LocalName)
            {
                case "navigationController":
                {
                    if (childObjs.Count != 1)
                        throw new CompileError($"navigation controller '{id}' needs exactly one root view controller");
                    o.Add("UIViewControllers", NibBuilder.Array("NSMutableArray", childObjs));
                    var bar = el.Elements("navigationBar").FirstOrDefault(n => (string?)n.Attribute("key") == "navigationBar");
                    var rootItem = (NibArchive.Obj?)childObjs[0]["UINavigationItem"];
                    var barObj = b.Bar(bar, "UINavigationBar", n =>
                    {
                        // Navigation bars always stretch across, and aren't opaque unless set to be.
                        if (!NibBuilder.Bool(bar, "opaque", false))
                            n.Items.RemoveAll(i => i.Key == "UIOpaque");
                        if (n["UIAutoresizingMask"] is null)
                            n.Items.Insert(n.Items.FindIndex(i => i.Key == "UIAutoresizeSubviews") + 1, ("UIAutoresizingMask", 2));
                        n.Add("UIViewLayoutMargins", NibBuilder.Insets(0, 0, 0, 0)).Add("UIViewLayoutMarginsAreDirectional", true).Add("UIIsLocked", true);
                        n.Add("UIItems", NibBuilder.Array("NSMutableArray", rootItem is null ? [] : [rootItem]));
                        n.Add("UIDelegate", o).Add("UIBarPosition", 2).Add("UIBarTranslucence", NibBuilder.Bool(bar, "translucent", true) ? 1 : 2);
                        if (NibBuilder.Bool(bar, "prefersLargeTitles", false))
                            n.Add("UIPrefersLargeTitles", true);
                    });
                    o.Add("UINavigationBar", barObj);
                    rootItem?.Add("UINavigationBar", barObj);
                    break;
                }
                case "tabBarController":
                {
                    var bar = el.Elements("tabBar").FirstOrDefault(n => (string?)n.Attribute("key") == "tabBar");
                    var barObj = b.Bar(bar, "UITabBar", t =>
                    {
                        t.Add("UIDelegate", o);
                        t.Add("UIItems", NibBuilder.Array("NSArray", childObjs.Select(c => (NibArchive.Obj?)c["UITabBarItem"]).OfType<NibArchive.Obj>()));
                        t.Add("UIBarTranslucence", NibBuilder.Bool(bar, "translucent", true) ? 1 : 2);
                    });
                    o.Add("UITabBar", barObj);
                    o.Add("UICustomizableViewControllers", NibBuilder.Array("NSMutableArray", childObjs));
                    o.Add("UIViewControllers", NibBuilder.Array("NSMutableArray", childObjs));
                    break;
                }
            }
            o.Add("UIClassName", NibBuilder.String((string?)el.Attribute("customClass") ?? original));
            o.Add("UIOriginalClassName", NibBuilder.String(original));

            // Controller outlets to objects in this nib (its items), and the items' actions and segues.
            foreach (var outlet in el.Element("connections")?.Elements("outlet") ?? [])
            {
                string target = (string)outlet.Attribute("destination")!;
                if (ViewOf(el)?.DescendantsAndSelf().Any(e => (string?)e.Attribute("id") == target) == true)
                    continue; // connected in the view nib
                if (!b.TryById(target, out var dest))
                    throw new CompileError($"outlet '{(string?)outlet.Attribute("property")}' of '{id}' points at an object outside its scene ('{target}')");
                b.Connect((string)outlet.Attribute("property")!, o, dest);
            }
            b.ResolveConnections();
            return o;
        }

        NibArchive.Obj SegueTemplate(XElement segue)
        {
            string kind = (string?)segue.Attribute("kind") ?? "";
            string destination = NibName(Target(segue));
            NibArchive.Obj t;
            switch (kind)
            {
                case "show" or "showDetail":
                    t = new NibArchive.Obj("UIStoryboardShowSegueTemplate");
                    if ((string?)segue.Attribute("identifier") is { } showId)
                        t.Add("UIIdentifier", NibBuilder.String(showId));
                    t.Add("UIDestinationViewControllerIdentifier", NibBuilder.String(destination));
                    t.Add("UIActionName", NibBuilder.String(kind == "show" ? "showViewController:sender:" : "showDetailViewController:sender:"));
                    return t;
                case "presentation" or "modal":
                    t = new NibArchive.Obj("UIStoryboardPresentationSegueTemplate");
                    if ((string?)segue.Attribute("identifier") is { } presentId)
                        t.Add("UIIdentifier", NibBuilder.String(presentId));
                    t.Add("UIDestinationViewControllerIdentifier", NibBuilder.String(destination));
                    if ((string?)segue.Attribute("modalPresentationStyle") is { } style)
                        t.Add("UIModalPresentationStyle", style switch
                        {
                            "fullScreen" => 0, "pageSheet" => 1, "formSheet" => 2, "currentContext" => 3, "overFullScreen" => 5,
                            "overCurrentContext" => 6, "automatic" => -2,
                            var s => throw new CompileError($"modalPresentationStyle '{s}' is not supported"),
                        });
                    if ((string?)segue.Attribute("modalTransitionStyle") is { } transition)
                        t.Add("UIModalTransitionStyle", transition switch
                        {
                            "coverVertical" => 0, "flipHorizontal" => 1, "crossDissolve" => 2, "partialCurl" => 3,
                            var s => throw new CompileError($"modalTransitionStyle '{s}' is not supported"),
                        });
                    if (!NibBuilder.Bool(segue, "animates", true))
                        t.Add("UIAnimates", false);
                    return t;
                default:
                    throw new CompileError($"'{kind}' segues are not supported (supported: show, showDetail, presentation)");
            }
        }
    }

    /// <summary>Builds one nib's object graph from Interface Builder XML.</summary>
    sealed class NibBuilder(XElement document)
    {
        // Ids of views some constraint places (explicitly, or as the owner of an item-less constraint).
        readonly HashSet<string> constrained = document.Descendants("constraint").SelectMany(c =>
        {
            string? owner = (string?)c.Parent?.Parent?.Attribute("id");
            return new[]
            {
                (string?)c.Attribute("firstItem") ?? owner,
                (string?)c.Attribute("secondItem") ?? (c.Attribute("secondAttribute") is null ? null : owner),
            };
        }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        readonly List<NibArchive.Obj> all = [];
        readonly List<NibArchive.Obj> connections = [];
        readonly Dictionary<string, NibArchive.Obj> byId = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        readonly List<(XElement Constraint, NibArchive.Obj Owner)> pendingConstraints = [];
        readonly List<NibArchive.Obj> constraints = [];
        readonly List<(XElement Connection, NibArchive.Obj Source)> pendingConnections = [];
        readonly List<NibArchive.Obj> keyValuePairs = [];

        public NibArchive.Obj Proxy(string identifier, string? id, bool track = true)
        {
            var proxy = new NibArchive.Obj("UIProxyObject").Add("UIProxiedObjectIdentifier", String(identifier));
            if (track)
                all.Add(proxy);
            if (id is not null)
                byId[id] = proxy;
            return proxy;
        }

        public void Alias(string id, string target) => aliases[id] = target;

        public void Track(NibArchive.Obj o, string? id = null)
        {
            all.Add(o);
            if (id is not null)
                byId[id] = o;
        }

        public bool TryById(string id, out NibArchive.Obj o) =>
            byId.TryGetValue(aliases.TryGetValue(id, out var a) ? a : id, out o!);

        public NibArchive.Obj ById(string id) =>
            TryById(id, out var o) ? o : throw new CompileError($"reference to an object outside this nib ('{id}')");

        public void Connect(string label, NibArchive.Obj source, NibArchive.Obj destination) =>
            connections.Add(new NibArchive.Obj("UIRuntimeOutletConnection")
                .Add("UILabel", String(label)).Add("UISource", source).Add("UIDestination", destination));

        /// <summary>Queues an element's &lt;connections&gt; (outlets, actions, segues) for resolution once every object exists.</summary>
        public void CollectConnections(XElement e, NibArchive.Obj source)
        {
            foreach (var c in e.Element("connections")?.Elements() ?? [])
                pendingConnections.Add((c, source));
        }

        public void ResolveConnections()
        {
            foreach (var (c, source) in pendingConnections)
            {
                string? destination = (string?)c.Attribute("destination");
                switch (c.Name.LocalName)
                {
                    case "outlet":
                        Connect((string)c.Attribute("property")!, source, ById(destination!));
                        break;
                    case "action":
                    {
                        var ev = new NibArchive.Obj("UIRuntimeEventConnection")
                            .Add("UILabel", String((string)c.Attribute("selector")!)).Add("UISource", source).Add("UIDestination", ById(destination!));
                        if (source.ClassName != "UIBarButtonItem")
                            ev.Add("UIEventMask", EventMask((string?)c.Attribute("eventType")));
                        connections.Add(ev);
                        break;
                    }
                    case "segue":
                    {
                        // Controls trigger a segue by sending -perform: to its template.
                        var ev = new NibArchive.Obj("UIRuntimeEventConnection")
                            .Add("UILabel", String("perform:")).Add("UISource", source).Add("UIDestination", ById((string)c.Attribute("id")!));
                        if (source.ClassName != "UIBarButtonItem")
                            ev.Add("UIEventMask", 64); // touchUpInside
                        connections.Add(ev);
                        break;
                    }
                    default:
                        throw new CompileError($"'{c.Name.LocalName}' connections are not supported");
                }
            }
            pendingConnections.Clear();
        }

        static int EventMask(string? type) => type switch
        {
            "touchDown" => 1, "touchDownRepeat" => 2, "touchDragInside" => 4, "touchDragOutside" => 8, "touchDragEnter" => 16,
            "touchDragExit" => 32, null or "touchUpInside" => 64, "touchUpOutside" => 128, "touchCancel" => 256,
            "valueChanged" => 4096, "primaryActionTriggered" => 8192, "menuActionTriggered" => 16384,
            "editingDidBegin" => 65536, "editingChanged" => 131072, "editingDidEnd" => 262144, "editingDidEndOnExit" => 524288,
            "allTouchEvents" => 0xfff, "allEditingEvents" => 0xf0000, "allEvents" => -1,
            var t => throw new CompileError($"unknown control event '{t}'"),
        };

        public byte[] Build(List<NibArchive.Obj> topLevel)
        {
            ResolveConnections();
            foreach (var (c, owner) in pendingConstraints)
                constraints.Add(Constraint(c, owner));
            foreach (var group in pendingConstraints.Zip(constraints).GroupBy(p => p.First.Owner, ReferenceEqualityComparer.Instance))
                ((NibArchive.Obj)group.Key!).Add("UIViewAutolayoutConstraints", Array("NSMutableArray", group.Select(g => g.Second)));
            var rootObj = new NibArchive.Obj("NSObject")
                .Add("UINibTopLevelObjectsKey", Array("NSArray", topLevel))
                .Add("UINibObjectsKey", Array("NSArray", all.Concat(constraints)))
                .Add("UINibConnectionsKey", Array("NSArray", connections));
            var empty = Array("NSArray", []);
            rootObj.Add("UINibVisibleWindowsKey", empty).Add("UINibAccessibilityConfigurationsKey", empty)
                .Add("UINibTraitStorageListsKey", empty).Add("UINibKeyValuePairsKey", keyValuePairs.Count > 0 ? Array("NSArray", keyValuePairs) : empty);
            return NibArchive.Write(rootObj);
        }

        // --- views -------------------------------------------------------------------------

        public NibArchive.Obj Element(XElement e) => e.Name.LocalName switch
        {
            "view" => View(e, "UIView"),
            "imageView" => ImageView(e),
            "label" => Label(e),
            "button" => Button(e),
            "textField" => TextField(e),
            "textView" => TextView(e),
            "switch" => Switch(e),
            "slider" => Slider(e),
            "segmentedControl" => SegmentedControl(e),
            "activityIndicatorView" => ActivityIndicator(e),
            "scrollView" => View(e, "UIScrollView", o => ScrollViewKeys(e, o)),
            "stackView" => StackView(e),
            var other => throw new CompileError($"'{other}' elements are not supported (supported: view, imageView, label, button, textField, textView, switch, slider, segmentedControl, activityIndicatorView, scrollView, stackView)"),
        };

        NibArchive.Obj View(XElement e, string defaultClass, Action<NibArchive.Obj>? specific = null)
        {
            var o = new NibArchive.Obj((string?)e.Attribute("customClass") ?? defaultClass);
            all.Add(o);
            if ((string?)e.Attribute("id") is { } id)
                byId[id] = o;

            var frame = Frame(e);
            o.Add("UIBounds", Geometry(0, 0, frame.W, frame.H)).Add("UICenter", Geometry(frame.X + frame.W / 2, frame.Y + frame.H / 2));

            var subviews = e.Element("subviews")?.Elements().Select(Element).ToList();
            if (subviews is { Count: > 0 })
                o.Add("UISubviews", Array("NSMutableArray", subviews));
            if (ColorFor(e, "backgroundColor") is { } background)
                o.Add("UIBackgroundColor", background);
            if (ColorFor(e, "tintColor") is { } tint)
                o.Add("UITintColor", tint);

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
            if ((string?)e.Attribute("alpha") is { } alpha && double.Parse(alpha, CultureInfo.InvariantCulture) != 1)
                o.Add("UIAlpha", (float)double.Parse(alpha, CultureInfo.InvariantCulture));
            if ((string?)e.Attribute("tag") is { } tag)
                o.Add("UITag", int.Parse(tag, CultureInfo.InvariantCulture));
            // Interface Builder keeps a view's frame (its autoresizing mask applies) unless constraints
            // or a stack view place it, even when translatesAutoresizingMaskIntoConstraints is NO.
            bool placed = (string?)e.Attribute("id") is { } vid && constrained.Contains(vid) || e.Parent?.Parent?.Name.LocalName == "stackView";
            if (!Bool(e, "translatesAutoresizingMaskIntoConstraints", true) && !Bool(e, "fixedFrame", false) && placed)
                o.Add("UIViewDoesNotTranslateAutoresizingMaskIntoConstraints", true);
            // Hugging priorities are archived when they differ from the class's own (750 for controls
            // with an intrinsic size); sliders and segmented controls always carry theirs.
            int defaultHugging = defaultClass is "UISwitch" or "UIActivityIndicatorView" ? 750 : 250;
            int hh = (int)Num(e, "horizontalHuggingPriority", 250), vh = (int)Num(e, "verticalHuggingPriority", 250);
            if (hh != defaultHugging || vh != defaultHugging || defaultClass is "UISlider" or "UISegmentedControl")
                o.Add("UIViewContentHuggingPriority", String($"{{{hh}, {vh}}}"));
            int hc = (int)Num(e, "horizontalCompressionResistancePriority", 750), vc = (int)Num(e, "verticalCompressionResistancePriority", 750);
            if (hc != 750 || vc != 750)
                o.Add("UIViewContentCompressionResistancePriority", String($"{{{hc}, {vc}}}"));
            if (defaultClass == "UIStackView")
                o.Add("UIViewLayoutMargins", Insets(0, 0, 0, 0));
            o.Add("UIViewSemanticContentAttribute", 0);

            // The safe area layout guide constraints may reference.
            foreach (var guide in e.Elements("viewLayoutGuide"))
            {
                string key = (string?)guide.Attribute("key") ?? "";
                string guideId = (string)guide.Attribute("id")!;
                if (!constrained.Contains(guideId))
                    continue; // as Interface Builder: only guides constraints use are archived
                if (key != "safeArea")
                    throw new CompileError($"constraints to the '{key}' layout guide are not supported (only the safe area)");
                o.Add("UIViewLayoutGuides", Array("NSMutableArray", [SafeAreaGuide(o, guideId)]));
            }

            specific?.Invoke(o);

            foreach (var c in e.Element("constraints")?.Elements("constraint") ?? [])
                pendingConstraints.Add((c, o));
            CollectConnections(e, o);
            return o;
        }

        NibArchive.Obj SafeAreaGuide(NibArchive.Obj view, string id)
        {
            var guide = new NibArchive.Obj("UILayoutGuide");
            byId[id] = guide;
            NibArchive.Obj System(NibArchive.Obj first, int a, NibArchive.Obj second, string name) => new NibArchive.Obj("NSLayoutConstraint")
                .Add("NSFirstItem", first).Add("NSFirstAttributeV2", a).Add("NSFirstAttribute", a)
                .Add("NSSecondItem", second).Add("NSSecondAttributeV2", a).Add("NSSecondAttribute", a)
                .Add("NSShouldBeArchived", false).Add("NSLayoutIdentifier", String("UIViewSafeAreaLayoutGuide-" + name));
            guide.Add("UILayoutGuideOwningView", view).Add("UILayoutGuideIdentifier", String("UIViewSafeAreaLayoutGuide"))
                .Add("UILayoutGuideOwningViewIsLocked", true).Add("UILayoutGuideShouldBeArchived", true)
                .Add("UILayoutGuideAllowsNegativeDimensions", true)
                .Add("UILayoutGuideSystemConstraints", Array("NSArray", [
                    System(guide, 3, view, "top"), System(guide, 1, view, "left"), System(view, 4, guide, "bottom"), System(view, 2, guide, "right")]));
            return guide;
        }

        static (double X, double Y, double W, double H) Frame(XElement e)
        {
            var frame = e.Elements("rect").FirstOrDefault(r => (string?)r.Attribute("key") == "frame");
            return (Num(frame, "x"), Num(frame, "y"), Num(frame, "width"), Num(frame, "height"));
        }

        NibArchive.Obj ImageView(XElement e) => View(e, "UIImageView", o =>
        {
            if ((string?)e.Attribute("image") is { Length: > 0 } image)
                o.Add("UIImage", Image(image));
        });

        static NibArchive.Obj Image(string name) => new NibArchive.Obj("UIImageNibPlaceholder")
            .Add("UIImageWidth", 1f).Add("UIImageHeight", 1f).Add("UIResourceName", String(name));

        NibArchive.Obj Label(XElement e) => View(e, "UILabel", o =>
        {
            if ((string?)e.Attribute("text") is { } text)
                o.Add("UIText", String(text));
            var font = e.Elements("fontDescription").FirstOrDefault();
            double pointSize = Num(font, "pointSize", 17);
            o.Add("UIFont", Font(font, 17));
            o.Add("UITextColor", ColorFor(e, "textColor") ?? SystemColor("labelColor", (0, 0, 0, 1)));
            if (ColorFor(e, "highlightedColor") is { } highlighted)
                o.Add("UIHighlightedColor", highlighted);
            o.Add("UIShadowOffset", Geometry(0, -1));
            o.Add("UITextAlignment", TextAlignment(e));
            int lineBreak = (string?)e.Attribute("lineBreakMode") switch
            {
                null or "tailTruncation" => 4, "wordWrap" => 0, "characterWrap" => 1, "clip" => 2, "headTruncation" => 3, "middleTruncation" => 5,
                var m => throw new CompileError($"unknown lineBreakMode '{m}'"),
            };
            if (lineBreak != 4)
                o.Add("UILineBreakMode", lineBreak);
            if ((string?)e.Attribute("numberOfLines") is { } lines)
                o.Add("UINumberOfLines", int.Parse(lines, CultureInfo.InvariantCulture));
            // Labels without these attributes get Interface Builder's old defaults: centred
            // baselines and shrink-to-fit.
            int baseline = (string?)e.Attribute("baselineAdjustment") switch
            {
                null or "alignCenters" => 1, "alignBaselines" => 0, "none" => 2,
                var b => throw new CompileError($"unknown baselineAdjustment '{b}'"),
            };
            if (baseline != 0)
                o.Add("UIBaselineAdjustment", baseline);
            if ((string?)e.Attribute("minimumFontSize") is { } minimum)
            {
                float min = float.Parse(minimum, CultureInfo.InvariantCulture);
                o.Add("UIAdjustsFontSizeToFit", true).Add("UIMinimumFontSize", min).Add("UIMinimumScaleFactor", (float)(min / pointSize));
            }
            else if ((string?)e.Attribute("minimumScaleFactor") is { } scale)
                o.Add("UIAdjustsFontSizeToFit", Bool(e, "adjustsFontSizeToFit", true)).Add("UIMinimumScaleFactor", float.Parse(scale, CultureInfo.InvariantCulture));
            else if (Bool(e, "adjustsFontSizeToFit", true))
                o.Add("UIAdjustsFontSizeToFit", true);
            o.Add("UIDisableUpdateTextColorOnTraitCollectionChange", false);
        });

        static int TextAlignment(XElement e) => (string?)e.Attribute("textAlignment") switch
        {
            null or "natural" => 4, "left" => 0, "center" => 1, "right" => 2, "justified" => 3,
            var a => throw new CompileError($"unknown textAlignment '{a}'"),
        };

        // UIControl: enabled/selected/highlighted and content alignment.
        static void ControlKeys(XElement e, NibArchive.Obj o)
        {
            if (Bool(e, "selected", false))
                o.Add("UISelected", true);
            if (Bool(e, "highlighted", false))
                o.Add("UIHighlighted", true);
            if (!Bool(e, "enabled", true))
                o.Add("UIDisabled", true);
            int vertical = (string?)e.Attribute("contentVerticalAlignment") switch
            {
                null or "center" => 0, "top" => 1, "bottom" => 2, "fill" => 3,
                var v => throw new CompileError($"unknown contentVerticalAlignment '{v}'"),
            };
            if (vertical != 0)
                o.Add("UIContentVerticalAlignment", vertical).Add("UIContentVerticalAlignment2", vertical);
            int horizontal = (string?)e.Attribute("contentHorizontalAlignment") switch
            {
                null or "center" => 0, "left" => 1, "right" => 2, "fill" => 3, "leading" => 4, "trailing" => 5,
                var h => throw new CompileError($"unknown contentHorizontalAlignment '{h}'"),
            };
            if (horizontal != 0)
                o.Add("UIContentHorizontalAlignment", horizontal);
        }

        NibArchive.Obj Button(XElement e) => View(e, "UIButton", o =>
        {
            if (e.Element("buttonConfiguration") is not null)
                throw new CompileError("buttons with a button configuration (Xcode's 'Plain', 'Filled', ... styles) are not supported; set the button's Style to 'Default'");
            ControlKeys(e, o);
            var content = new List<(NibArchive.Obj, NibArchive.Obj)>();
            foreach (var state in e.Elements("state"))
            {
                int key = (string?)state.Attribute("key") switch
                {
                    "normal" => 0, "highlighted" => 1, "disabled" => 2, "selected" => 4,
                    var s => throw new CompileError($"unknown button state '{s}'"),
                };
                var c = new NibArchive.Obj("UIButtonContent");
                if ((string?)state.Attribute("title") is { } title)
                    c.Add("UITitle", String(title));
                if ((string?)state.Attribute("image") is { Length: > 0 } image)
                    c.Add("UIImage", Image(image));
                if ((string?)state.Attribute("backgroundImage") is { Length: > 0 } background)
                    c.Add("UIBackgroundImage", Image(background));
                if (ColorFor(state, "titleColor") is { } titleColor)
                    c.Add("UITitleColor", titleColor);
                if (ColorFor(state, "titleShadowColor") is { } shadow)
                    c.Add("UITitleShadowColor", shadow);
                content.Add((new NibArchive.Obj("NSNumber").Add("NS.intval", key), c));
            }
            o.Add("UIButtonStatefulContent", Dictionary(content, "NSMutableDictionary"));
            o.Add("UIAdjustsImageWhenHighlighted", Bool(e, "adjustsImageWhenHighlighted", true));
            o.Add("UIAdjustsImageWhenDisabled", Bool(e, "adjustsImageWhenDisabled", true));
            int type = (string?)e.Attribute("buttonType") switch
            {
                "custom" => 0, null or "system" or "roundedRect" => 1, "detailDisclosure" => 2, "infoLight" => 3, "infoDark" => 4, "contactAdd" => 5, "close" => 7,
                var t => throw new CompileError($"unknown buttonType '{t}'"),
            };
            if (type != 0)
                o.Add("UIButtonType", type);
            o.Add("UIFont", Font(e.Element("fontDescription"), 15));
            o.Add("UIButtonConfiguration", null);
        });

        NibArchive.Obj TextField(XElement e) => View(e, "UITextField", o =>
        {
            ControlKeys(e, o);
            o.Add("UIFont", Font(e.Element("fontDescription"), 14));
            if ((string?)e.Attribute("placeholder") is { } placeholder)
                o.Add("UIPlaceholder", String(placeholder));
            o.Add("UITextAlignment", TextAlignment(e));
            if ((string?)e.Attribute("text") is { } text)
                o.Add("UIText", String(text));
            o.Add("UITextColor", ColorFor(e, "textColor") ?? SystemColor("labelColor", (0, 0, 0, 1)));
            int border = (string?)e.Attribute("borderStyle") switch
            {
                null or "none" => 0, "line" => 1, "bezel" => 2, "roundedRect" => 3,
                var s => throw new CompileError($"unknown borderStyle '{s}'"),
            };
            if (border != 0)
                o.Add("UIBorderStyle", border);
            if ((string?)e.Attribute("minimumFontSize") is { } minimum)
                o.Add("UIAdjustsFontSizeToFit", Bool(e, "adjustsFontSizeToFit", true)).Add("UIMinimumFontSize", float.Parse(minimum, CultureInfo.InvariantCulture));
            int clear = (string?)e.Attribute("clearButtonMode") switch
            {
                null or "never" => 0, "whileEditing" => 1, "unlessEditing" => 2, "always" => 3,
                var m => throw new CompileError($"unknown clearButtonMode '{m}'"),
            };
            if (clear != 0)
                o.Add("UIClearButtonMode", clear);
            o.Add("UIClearButtonOffset", Geometry(3, 1));
            if (Bool(e, "clearsOnBeginEditing", false))
                o.Add("UIClearsOnBeginEditing", true);
            TextInputTraits(e, o);
            o.Add("UIDisableTextColorUpdateOnTraitCollectionChange", true);
            if (border == 3)
                o.Add("UIRoundedRectBackgroundCornerRadius", new NibArchive.Double(0));
        });

        static void TextInputTraits(XElement e, NibArchive.Obj o)
        {
            var traits = e.Element("textInputTraits");
            if (traits is null)
                return;
            int Map(string attribute, Dictionary<string, int> values)
            {
                string? v = (string?)traits.Attribute(attribute);
                return v is null ? 0 : values.TryGetValue(v, out int n) ? n : throw new CompileError($"unknown {attribute} '{v}'");
            }
            int capitalization = (string?)traits.Attribute("autocapitalizationType") switch
            {
                null => -1, "none" => 0, "words" => 1, "sentences" => 2, "allCharacters" => 3,
                var v => throw new CompileError($"unknown autocapitalizationType '{v}'"),
            };
            if (capitalization is >= 0 and not 2) // sentences is the default
                o.Add("UIAutocapitalizationType", capitalization);
            int correction = Map("autocorrectionType", new() { ["default"] = 0, ["no"] = 1, ["yes"] = 2 });
            if (correction != 0)
                o.Add("UIAutocorrectionType", correction);
            int keyboard = Map("keyboardType", new()
            {
                ["default"] = 0, ["ASCIICapable"] = 1, ["numbersAndPunctuation"] = 2, ["URL"] = 3, ["numberPad"] = 4, ["phonePad"] = 5,
                ["namePhonePad"] = 6, ["emailAddress"] = 7, ["decimalPad"] = 8, ["twitter"] = 9, ["webSearch"] = 10, ["ASCIICapableNumberPad"] = 11,
            });
            if (keyboard != 0)
                o.Add("UIKeyboardType", keyboard);
            int returnKey = Map("returnKeyType", new()
            {
                ["default"] = 0, ["go"] = 1, ["google"] = 2, ["join"] = 3, ["next"] = 4, ["route"] = 5, ["search"] = 6, ["send"] = 7,
                ["yahoo"] = 8, ["done"] = 9, ["emergencyCall"] = 10, ["continue"] = 11,
            });
            if (returnKey != 0)
                o.Add("UIReturnKeyType", returnKey);
            if (Bool(traits, "secureTextEntry", false))
                o.Add("UISecureTextEntry", true);
            if (Bool(traits, "enablesReturnKeyAutomatically", false))
                o.Add("UIEnablesReturnKeyAutomatically", true);
        }

        static void ScrollViewKeys(XElement e, NibArchive.Obj o)
        {
            if (!Bool(e, "showsHorizontalScrollIndicator", true))
                o.Add("UIShowsHorizontalScrollIndicator", false);
            if (!Bool(e, "showsVerticalScrollIndicator", true))
                o.Add("UIShowsVerticalScrollIndicator", false);
            if (Bool(e, "pagingEnabled", false))
                o.Add("UIPagingEnabled", true);
            if (!Bool(e, "scrollEnabled", true))
                o.Add("UIScrollDisabled", true);
            if (!Bool(e, "bounces", true))
                o.Add("UIBounceEnabled", false);
            o.Add("UIScrollViewIndicatorInsetAdjustmentBehavior", 1);
            o.Add("UIBouncesZoom", Bool(e, "bouncesZoom", true));
            o.Add("UIScrollViewContentInsetAdjustmentBehavior", (string?)e.Attribute("contentInsetAdjustmentBehavior") switch
            {
                null or "automatic" => 0, "scrollableAxes" => 1, "never" => 2, "always" => 3,
                var b => throw new CompileError($"unknown contentInsetAdjustmentBehavior '{b}'"),
            });
        }

        NibArchive.Obj TextView(XElement e) => View(e, "UITextView", o =>
        {
            ScrollViewKeys(e, o);
            string text = (string?)e.Attribute("text") ?? "";
            var font = Font(e.Element("fontDescription"), 14);
            var color = ColorFor(e, "textColor") ?? SystemColor("labelColor", (0, 0, 0, 1));
            int alignment = TextAlignment(e);
            var paragraph = new NibArchive.Obj("NSMutableParagraphStyle").Add("NSAlignment", alignment).Add("NSTabStops", null);
            var textObj = String(text);
            o.Add("UIText", textObj);
            o.Add("UIAttributedText", new NibArchive.Obj("NSAttributedString").Add("NSString", textObj)
                .Add("NSAttributes", Dictionary([(String("NSColor"), color), (String("NSFont"), font), (String("NSParagraphStyle"), paragraph)])));
            o.Add("UIFont", font).Add("UITextColor", color).Add("UITextAlignment", alignment);
            if (!Bool(e, "editable", true))
                o.Add("UIEditable", false);
            TextInputTraits(e, o);
        });

        NibArchive.Obj Switch(XElement e)
        {
            var o = View(e, "UISwitch", o =>
            {
                ControlKeys(e, o);
                o.Add("UISwitchEnabled", true);
                if (Bool(e, "on", false))
                    o.Add("UISwitchOn", true);
                if (ColorFor(e, "onTintColor") is { } onTint)
                    o.Add("UISwitchOnTintColor", onTint);
            });
            // A switch has an intrinsic size; Interface Builder restores its frame after decoding.
            var (x, y, w, h) = Frame(e);
            keyValuePairs.Add(new NibArchive.Obj("UINibKeyValuePair").Add("UIObject", o)
                .Add("UIValue", new NibArchive.Obj("NSValue").Add("NS.special", 3).Add("NS.rectval", String($"{{{{{G(x)}, {G(y)}}}, {{{G(w)}, {G(h)}}}}}")))
                .Add("UIKeyPath", String("frame")));
            return o;
        }

        static string G(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

        NibArchive.Obj Slider(XElement e) => View(e, "UISlider", o =>
        {
            ControlKeys(e, o);
            o.Add("UIValue", (float)Num(e, "value", 0.5));
            if (Num(e, "minValue", 0) != 0)
                o.Add("UIMinValue", (float)Num(e, "minValue"));
            if (Num(e, "maxValue", 1) != 1)
                o.Add("UIMaxValue", (float)Num(e, "maxValue"));
            if (!Bool(e, "continuous", true))
                o.Add("UIContinuous", false);
            o.Add("UIBehavioralStyle", 0);
        });

        NibArchive.Obj SegmentedControl(XElement e)
        {
            var segments = e.Element("segments")?.Elements("segment").ToList() ?? [];
            int selected = (int)Num(e, "selectedSegmentIndex", -1);
            var objs = segments.Select((s, i) =>
            {
                string title = (string?)s.Attribute("title")
                    ?? throw new CompileError("segments with images are not supported (only titles)");
                // Segments are archived zero-width, the control's height; UIKit sizes them at layout.
                double height = Frame(e).H;
                var seg = new NibArchive.Obj("UISegment")
                    .Add("UIBounds", Geometry(0, 0, 0, height)).Add("UICenter", Geometry(0, height / 2))
                    .Add("UIDeepDrawRect", true).Add("UIAutoresizeSubviews", true).Add("UIViewSemanticContentAttribute", 0)
                    .Add("UISegmentInfo", String(title)).Add("UISegmentStyle", 5);
                // As Interface Builder archives it (UIKit recomputes positions at layout).
                if (i == 1)
                    seg.Add("UISegmentPosition", 3);
                else if (i >= 2)
                    seg.Add("UISegmentPosition", 1);
                return seg;
            }).ToList();
            return View(e, "UISegmentedControl", o =>
            {
                // The selected segment is the last subview (drawn on top).
                var subviews = objs.Where((_, i) => i != selected).Concat(selected >= 0 && selected < objs.Count ? [objs[selected]] : []).ToList();
                o.Items.Insert(o.Items.FindIndex(i => i.Key == "UIDeepDrawRect"), ("UISubviews", Array("NSMutableArray", subviews)));
                foreach (var s in objs)
                    all.Add(s);
                ControlKeys(e, o);
                o.Add("UIPointerInteractionEnabled", true);
                o.Add("UISegments", Array("NSMutableArray", objs));
                o.Add("UISelectedSegmentIndex", selected);
                if (Bool(e, "momentary", false))
                    o.Add("UIMomentary", true);
            });
        }

        NibArchive.Obj ActivityIndicator(XElement e) => View(e, "UIActivityIndicatorView", o =>
        {
            var (modern, legacy) = (string?)e.Attribute("style") switch
            {
                null or "medium" => (100, 1), "large" => (101, 0),
                "white" => (1, 1), "whiteLarge" => (0, 0), "gray" => (2, 2),
                var s => throw new CompileError($"unknown activity indicator style '{s}'"),
            };
            o.Add("UIActivityIndicatorViewStyle-Modern", modern).Add("UIActivityIndicatorViewStyle", legacy);
            o.Add("UIHidesWhenStopped", Bool(e, "hidesWhenStopped", false));
            o.Add("UIAnimating", Bool(e, "animating", false));
            o.Add("UIColor", ColorFor(e, "color"));
            if (o["UITintColor"] is null)
                o.Add("UITintColor", SystemColor("secondaryLabelColor", (0.235294, 0.235294, 0.262745, 0.6)));
        });

        NibArchive.Obj StackView(XElement e) => View(e, "UIStackView", o =>
        {
            o.Add("UIStackViewArrangedSubviews", Array("NSArray", ((NibArchive.Obj?)o["UISubviews"])?.Items.Where(i => i.Key == "UINibEncoderEmptyKey").Select(i => (NibArchive.Obj)i.Value!) ?? []));
            o.Add("UIStackViewAxis", (string?)e.Attribute("axis") == "vertical" ? 1 : 0);
            o.Add("UIStackViewDistribution", (string?)e.Attribute("distribution") switch
            {
                null or "fill" => 0, "fillEqually" => 1, "fillProportionally" => 2, "equalSpacing" => 3, "equalCentering" => 4,
                var d => throw new CompileError($"unknown stack view distribution '{d}'"),
            });
            o.Add("UIStackViewAlignment", (string?)e.Attribute("alignment") switch
            {
                null or "fill" => 0, "leading" or "top" => 1, "firstBaseline" => 2, "center" => 3, "trailing" or "bottom" => 4, "lastBaseline" => 5,
                var a => throw new CompileError($"unknown stack view alignment '{a}'"),
            });
            o.Add("UIStackViewSpacing", new NibArchive.Double(Num(e, "spacing")));
            o.Add("UIStackViewBaselineRelative", Bool(e, "baselineRelativeArrangement", false));
            o.Add("UIStackViewLayoutMarginsRelative", Bool(e, "layoutMarginsRelativeArrangement", false));
        });

        // --- bars and items ----------------------------------------------------------------

        public NibArchive.Obj Bar(XElement? e, string className, Action<NibArchive.Obj> specific) =>
            e is null ? Bar(new XElement("bar"), className, specific) : View(e, className, specific);

        public NibArchive.Obj NavigationItem(XElement e, string controllerId)
        {
            var o = new NibArchive.Obj("UINavigationItem");
            Track(o, (string?)e.Attribute("id"));
            if ((string?)e.Attribute("title") is { } title)
            {
                var t = String(title);
                o.Add("UITitle", t).Add("UIAttributedTitle", new NibArchive.Obj("NSAttributedString").Add("NSString", t).Add("NSAttributes", Dictionary(new List<(string, NibArchive.Obj)>())));
            }
            if ((string?)e.Attribute("prompt") is { } prompt)
                o.Add("UIPrompt", String(prompt));
            var left = e.Elements("barButtonItem").Where(b => (string?)b.Attribute("key") == "leftBarButtonItem").Concat(e.Element("leftBarButtonItems")?.Elements("barButtonItem") ?? []).Select(BarButtonItem).ToList();
            var right = e.Elements("barButtonItem").Where(b => (string?)b.Attribute("key") == "rightBarButtonItem").Concat(e.Element("rightBarButtonItems")?.Elements("barButtonItem") ?? []).Select(BarButtonItem).ToList();
            if (e.Elements("barButtonItem").FirstOrDefault(b => (string?)b.Attribute("key") == "backBarButtonItem") is { } back)
                o.Add("UIBackBarButtonItem", BarButtonItem(back));
            if (left.Count > 0)
                o.Add("UILeftBarButtonItem", left[0]);
            if (right.Count > 0)
                o.Add("UIRightBarButtonItem", right[0]);
            o.Add("UICenterItemGroups", Array("NSArray", []));
            if (left.Count > 0)
                o.Add("UILeftBarButtonItems", Array("NSArray", left));
            if (right.Count > 0)
                o.Add("UIRightBarButtonItems", Array("NSArray", right));
            o.Add("UILargeTitleDisplayMode", (string?)e.Attribute("largeTitleDisplayMode") switch
            {
                null or "automatic" => 0, "always" => 1, "never" => 2,
                var m => throw new CompileError($"unknown largeTitleDisplayMode '{m}'"),
            });
            return o;
        }

        NibArchive.Obj BarButtonItem(XElement e)
        {
            var o = new NibArchive.Obj("UIBarButtonItem");
            Track(o, (string?)e.Attribute("id"));
            o.Add("UIEnabled", Bool(e, "enabled", true));
            if ((string?)e.Attribute("title") is { } title)
                o.Add("UITitle", String(title));
            if ((string?)e.Attribute("image") is { Length: > 0 } image)
                o.Add("UIImage", Image(image));
            if ((string?)e.Attribute("systemItem") is { } system)
            {
                int item = system switch
                {
                    "done" => 0, "cancel" => 1, "edit" => 2, "save" => 3, "add" => 4, "flexibleSpace" => 5, "fixedSpace" => 6,
                    "compose" => 7, "reply" => 8, "action" => 9, "organize" => 10, "bookmarks" => 11, "search" => 12, "refresh" => 13,
                    "stop" => 14, "camera" => 15, "trash" => 16, "play" => 17, "pause" => 18, "rewind" => 19, "fastForward" => 20,
                    "undo" => 21, "redo" => 22, "close" => 24,
                    var s => throw new CompileError($"unknown bar button systemItem '{s}'"),
                };
                o.Add("UIIsSystemItem", true).Add("UISystemItem", item);
            }
            o.Add("UIStyle", (string?)e.Attribute("style") switch
            {
                null => 1, "plain" => 0, "done" => 2,
                var s => throw new CompileError($"unknown bar button style '{s}'"),
            });
            if (ColorFor(e, "tintColor") is { } tint)
                o.Add("UITintColor", tint);
            if (e.Element("customView") is not null)
                throw new CompileError("bar button items with a custom view are not supported");
            CollectConnections(e, o);
            return o;
        }

        public NibArchive.Obj TabBarItem(XElement e)
        {
            var o = new NibArchive.Obj("UITabBarItem");
            Track(o, (string?)e.Attribute("id"));
            o.Add("UIEnabled", Bool(e, "enabled", true));
            if ((string?)e.Attribute("systemItem") is { } system)
                throw new CompileError($"system tab bar items ('{system}') are not supported; give the item a title and image");
            if ((string?)e.Attribute("title") is { } title)
                o.Add("UITitle", String(title));
            if ((string?)e.Attribute("image") is { Length: > 0 } image)
                o.Add("UIImage", Image(image));
            if ((string?)e.Attribute("selectedImage") is { Length: > 0 } selectedImage)
                o.Add("UISelectedImage", Image(selectedImage));
            if ((string?)e.Attribute("badgeValue") is { } badge)
                o.Add("UIBadgeValue", String(badge));
            if ((string?)e.Attribute("tag") is { } tag)
                o.Add("UITag", int.Parse(tag, CultureInfo.InvariantCulture));
            return o;
        }

        // --- values ------------------------------------------------------------------------

        // System fonts by size and weight; named fonts by name (keys as in WinObjC's UIFont).
        static NibArchive.Obj Font(XElement? f, double defaultSize)
        {
            double size = Num(f, "pointSize", defaultSize);
            string type = (string?)f?.Attribute("type") ?? "system";
            var o = new NibArchive.Obj("UIFont");
            if ((string?)f?.Attribute("name") is { } fontName)
            {
                o.Add("UIFontName", String(fontName)).Add("UIFontPointSize", new NibArchive.Double(size));
                return o;
            }
            if ((string?)f?.Attribute("style") is { } style)
                throw new CompileError($"text style fonts ('{style}', Dynamic Type) are not supported; use a system font with a size");
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
                : TryById(id, out var o) ? o : throw new CompileError($"constraint references unknown item '{id}'");
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

        /// <summary>An inlined dictionary: keys and values alternate.</summary>
        public static NibArchive.Obj Dictionary(IEnumerable<(NibArchive.Obj Key, NibArchive.Obj Value)> entries, string className = "NSDictionary")
        {
            var o = new NibArchive.Obj(className).Add("NSInlinedValue", true);
            foreach (var (k, v) in entries)
                o.Add("UINibEncoderEmptyKey", k).Add("UINibEncoderEmptyKey", v);
            return o;
        }

        public static NibArchive.Obj Dictionary(IEnumerable<(string Key, NibArchive.Obj Value)> entries) =>
            Dictionary(entries.Select(e => (String(e.Key), e.Value)));

        public static NibArchive.Obj Array(string className, IEnumerable<NibArchive.Obj> items)
        {
            var o = new NibArchive.Obj(className).Add("NSInlinedValue", true);
            foreach (var item in items)
                o.Add("UINibEncoderEmptyKey", item);
            return o;
        }

        // CGRect/CGPoint/UIEdgeInsets values: a 0x07 (double) marker, then the components as doubles.
        static byte[] Geometry(params double[] values)
        {
            var bytes = new byte[1 + values.Length * 8];
            bytes[0] = 7;
            for (int i = 0; i < values.Length; i++)
                BitConverter.GetBytes(values[i]).CopyTo(bytes, 1 + i * 8);
            return bytes;
        }

        public static byte[] Insets(double top, double left, double bottom, double right) => Geometry(top, left, bottom, right);

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

        public static bool Bool(XElement? e, string name, bool fallback) => (string?)e?.Attribute(name) switch
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
