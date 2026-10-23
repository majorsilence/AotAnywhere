namespace IBTest;

static class Log {
	public static void Line (string s) => Console.WriteLine ("IBTEST " + s);
	public static void After (double seconds, Action a) => NSTimer.CreateScheduledTimer (seconds, false, _ => {
		try { a (); } catch (Exception e) { Line ("FAIL exception " + e); }
	});
	// The first control of type T in a view tree.
	public static T? Find<T> (UIView v, Func<T, bool>? match = null) where T : UIView
	{
		if (v is T t && (match is null || match (t)))
			return t;
		foreach (var s in v.Subviews)
			if (Find (s, match) is { } found)
				return found;
		return null;
	}
}

[Register ("AppDelegate")]
public class AppDelegate : UIApplicationDelegate {
	public override bool FinishedLaunching (UIApplication application, NSDictionary? launchOptions) => true;
	public override UISceneConfiguration GetConfiguration (UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options)
		=> new UISceneConfiguration ("Default Configuration", connectingSceneSession.Role);
}

[Register ("SceneDelegate")]
public class SceneDelegate : UIResponder, IUIWindowSceneDelegate {
	[Export ("window")]
	public UIWindow? Window { get; set; }

	[Export ("scene:willConnectToSession:options:")]
	public void WillConnect (UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
	{
		var args = NSProcessInfo.ProcessInfo.Arguments;
		int i = Array.IndexOf (args, "--storyboard");
		string name = i >= 0 && i + 1 < args.Length ? args [i + 1] : "Showcase";
		Log.Line ("loading storyboard " + name);
		Window = new UIWindow ((UIWindowScene) scene);
		Window.RootViewController = name == "Assets" ? new AssetsViewController () : UIStoryboard.FromName (name, null).InstantiateInitialViewController ();
		Log.Line ("initial " + Window.RootViewController?.GetType ().Name + " / " + Window.RootViewController?.Class.Name);
		Window.MakeKeyAndVisible ();
	}
}

[Register ("MainViewController")]
public class MainViewController : UIViewController {
	public MainViewController (IntPtr handle) : base (handle) { }
	[Outlet] public UITextField? nameField { get; set; }
	[Outlet] public UILabel? statusLabel { get; set; }
	[Outlet] public UISwitch? toggle { get; set; }
	bool ran;

	[Action ("greet:")]
	public void Greet (NSObject sender)
	{
		statusLabel!.Text = $"Hello, {nameField!.Text}{(toggle!.On ? "!" : ".")}";
		Log.Line ("greet: sender=" + sender.GetType ().Name + " label=" + statusLabel.Text);
	}

	public override void ViewDidAppear (bool animated)
	{
		base.ViewDidAppear (animated);
		if (ran)
			return;
		ran = true;
		Log.Line ($"outlets nameField={nameField is not null} statusLabel={statusLabel?.Text} toggle={toggle?.On} title={NavigationItem.Title} nav={NavigationController is not null}");
		Log.Line ($"stack frame {Log.Find<UIStackView> (View!)?.Frame} field frame {nameField?.Frame} placeholder={nameField?.Placeholder}");
		Log.After (1.5, () => {
			nameField!.Text = "Bob";
			toggle!.On = false;
			Log.Find<UIButton> (View!, b => b.Title (UIControlState.Normal) == "Greet")!.SendActionForControlEvents (UIControlEvent.TouchUpInside);
			Log.After (1.5, () => {
				Log.Find<UIButton> (View!, b => b.Title (UIControlState.Normal) == "Next")!.SendActionForControlEvents (UIControlEvent.TouchUpInside);
				Log.After (1.5, () => {
					var top = NavigationController!.TopViewController!;
					Log.Line ($"after segue top={top.NavigationItem.Title} depth={NavigationController.ViewControllers!.Length} label={Log.Find<UILabel> (top.View!)?.Text}");
					Log.Line (top.NavigationItem.Title == "Second" && statusLabel!.Text == "Hello, Bob." ? "showcase ok" : "FAIL showcase");
				});
			});
		});
	}
}

[Register ("ControlsViewController")]
public class ControlsViewController : UIViewController {
	public ControlsViewController (IntPtr handle) : base (handle) { }
	[Outlet] public UISlider? slider { get; set; }
	[Outlet] public UISegmentedControl? segments { get; set; }
	bool ran, slid, added;

	[Action ("sliderChanged:")]
	public void SliderChanged (UISlider s) { slid = true; Log.Line ("sliderChanged: " + s.Value); }

	[Action ("add:")]
	public void Add (NSObject sender) { added = true; Log.Line ("add: sender=" + sender.GetType ().Name); }

	public override void ViewDidAppear (bool animated)
	{
		base.ViewDidAppear (animated);
		if (ran)
			return;
		ran = true;
		var titles = string.Join (",", Enumerable.Range (0, (int) segments!.NumberOfSegments).Select (i => segments.TitleAt (i)));
		Log.Line ($"slider={slider?.Value} [{slider?.MinValue},{slider?.MaxValue}] segments={titles} selected={segments.SelectedSegment} frame={segments.Frame}");
		var spinner = Log.Find<UIActivityIndicatorView> (View!);
		var text = Log.Find<UITextView> (View!);
		var present = Log.Find<UIButton> (View!, b => b.Title (UIControlState.Normal) == "Present");
		Log.Line ($"spinner animating={spinner?.IsAnimating} textView='{text?.Text}' frame={text?.Frame} button highlightedTitle={present?.Title (UIControlState.Highlighted)} titleColor={present?.TitleColor (UIControlState.Normal)}");
		Log.Line ($"right bar item system={NavigationItem.RightBarButtonItem is not null} title={NavigationItem.Title}");
		Log.After (1.5, () => {
			slider!.Value = 0.8f;
			slider.SendActionForControlEvents (UIControlEvent.ValueChanged);
			var item = NavigationItem.RightBarButtonItem!;
			UIApplication.SharedApplication.SendAction (item.Action!, item.Target, item, null);
			PerformSegue ("manual", this);
			Log.After (1.5, () => {
				Log.Line ("after manual segue top=" + NavigationController!.TopViewController!.View!.BackgroundColor);
				NavigationController.PopViewController (false);
				Log.After (1.0, () => {
					present!.SendActionForControlEvents (UIControlEvent.TouchUpInside);
					Log.After (1.5, () => {
						var presented = PresentedViewController;
						Log.Line ($"presented={presented?.GetType ().Name} style={presented?.ModalPresentationStyle}");
						Log.Line (slid && added && presented is not null ? "controls ok" : "FAIL controls");
					});
				});
			});
		});
	}
}

[Register ("FirstViewController")]
public class FirstViewController : UIViewController {
	public FirstViewController (IntPtr handle) : base (handle) { }
	public override void ViewDidAppear (bool animated)
	{
		base.ViewDidAppear (animated);
		var tabs = TabBarController!;
		Log.Line ($"first appeared tabs={string.Join (",", tabs.ViewControllers!.Select (v => v.TabBarItem.Title))} label={Log.Find<UILabel> (View!)?.Text}");
		Log.After (1.5, () => tabs.SelectedIndex = 1);
	}
}

[Register ("SecondViewController")]
public class SecondViewController : UIViewController {
	public SecondViewController (IntPtr handle) : base (handle) { }
	public override void ViewDidAppear (bool animated)
	{
		base.ViewDidAppear (animated);
		Log.Line ($"second appeared label={Log.Find<UILabel> (View!)?.Text}");
		Log.Line ("tabbed ok");
	}
}

// The asset catalog's colours, images (bitmap with a dark variant, PDF and SVG vectors) and data.
public class AssetsViewController : UIViewController {
	public override void ViewDidAppear (bool animated)
	{
		base.ViewDidAppear (animated);
		var problems = new List<string> ();
		void Check (bool ok, string what) { if (!ok) problems.Add (what); }

		var brand = UIColor.FromName ("Brand");
		Check (brand is not null, "Brand color");
		if (brand is not null) {
			brand.GetResolvedColor (UITraitCollection.FromUserInterfaceStyle (UIUserInterfaceStyle.Light)).GetRGBA (out var r, out var g, out var b, out _);
			brand.GetResolvedColor (UITraitCollection.FromUserInterfaceStyle (UIUserInterfaceStyle.Dark)).GetRGBA (out var dr, out var dg, out var db, out _);
			Log.Line ($"Brand light={r:0.00},{g:0.00},{b:0.00} dark={dr:0.00},{dg:0.00},{db:0.00}");
			Check (Math.Abs (r - 0.2) < 0.01 && Math.Abs (b - 0.8) < 0.01, "Brand light value");
			Check (Math.Abs (dr - 0.9) < 0.01 && Math.Abs (db - 0.1) < 0.01, "Brand dark value");
		}

		var stack = new UIStackView { Axis = UILayoutConstraintAxis.Vertical, Spacing = 12, Frame = new CGRect (40, 120, 200, 400) };
		foreach (var (name, w) in new[] { ("Logo", 32.0), ("Glyph", 24.0), ("Photo", 20.0) }) {
			var image = UIImage.FromBundle (name);
			Log.Line ($"{name} size={image?.Size} scale={image?.CurrentScale}");
			Check (image is not null && Math.Abs (image.Size.Width - w) < 0.5, name + " image");
			if (image is not null)
				stack.AddArrangedSubview (new UIImageView (image) { ContentMode = UIViewContentMode.ScaleAspectFit, BackgroundColor = UIColor.SystemGray5 });
		}
		var dark = UIImage.FromBundle ("Photo", null, UITraitCollection.FromUserInterfaceStyle (UIUserInterfaceStyle.Dark));
		Check (dark is not null, "Photo dark variant");
		// The dark variant is near-black, the default white: sample one pixel of each.
		Check (dark is not null && Brightness (dark) < 0.2 && Brightness (UIImage.FromBundle ("Photo")!) > 0.8, "Photo dark variant differs");

		var data = new NSDataAsset ("Config");
		string? text = data.Data?.ToString ();
		Log.Line ($"Config uti={data.TypeIdentifier} data={text?.Trim ()}");
		Check (text?.Trim () == "{\"answer\":42}", "Config data asset");

		View!.BackgroundColor = brand;
		View.AddSubview (stack);
		Log.Line (problems.Count == 0 ? "assets ok" : "FAIL assets: " + string.Join (", ", problems));
	}

	// The brightness of an image's top-left pixel, drawn into a 1x1 bitmap.
	static double Brightness (UIImage image)
	{
		var pixel = new byte [4];
		using var space = CGColorSpace.CreateDeviceRGB ();
		using (var ctx = new CGBitmapContext (pixel, 1, 1, 8, 4, space, CGImageAlphaInfo.PremultipliedLast))
			ctx.DrawImage (new CGRect (0, 0, 1, 1), image.CGImage!);
		return (pixel [0] + pixel [1] + pixel [2]) / (3 * 255.0);
	}
}

// Gaps.storyboard: Dynamic Type, catalog colors, button configurations, a scene gesture recognizer,
// prototype table and collection cells, an unwind segue, a static table and a storyboard reference.
[Register ("GapsViewController")]
public class GapsViewController : UIViewController {
	public GapsViewController (IntPtr handle) : base (handle) { }
	readonly List<string> problems = new ();
	bool ran, unwound;
	void Check (bool ok, string what) { if (!ok) problems.Add (what); }

	[Action ("tapped:")]
	public void Tapped (UITapGestureRecognizer sender) => Log.Line ("tapped:");

	[Action ("backToGaps:")]
	public void BackToGaps (UIStoryboardSegue segue) { unwound = true; Log.Line ("backToGaps: from " + segue.SourceViewController.GetType ().Name); }

	static (nfloat R, nfloat G, nfloat B) Rgb (UIColor? c, UIUserInterfaceStyle style)
	{
		nfloat r = -1, g = -1, b = -1;
		c?.GetResolvedColor (UITraitCollection.FromUserInterfaceStyle (style)).GetRGBA (out r, out g, out b, out _);
		return (r, g, b);
	}

	public override void ViewDidAppear (bool animated)
	{
		base.ViewDidAppear (animated);
		if (ran)
			return;
		ran = true;
		var nav = NavigationController!;
		var body = Log.Find<UILabel> (View!, l => l.Text == "Body text")!;
		var preferred = UIFont.GetPreferredFontForTextStyle (UIFontTextStyle.Body);
		Log.Line ($"body font={body.Font.PointSize} preferred={preferred.PointSize} adjusts={body.AdjustsFontForContentSizeCategory}");
		Check (body.AdjustsFontForContentSizeCategory && Math.Abs (body.Font.PointSize - preferred.PointSize) < 0.01, "Dynamic Type font");
		var light = Rgb (body.TextColor, UIUserInterfaceStyle.Light);
		var dark = Rgb (body.TextColor, UIUserInterfaceStyle.Dark);
		Log.Line ($"body color light={light} dark={dark}");
		// The asset catalog's Brand: dark differs from the storyboard's (light) fallback.
		Check (Math.Abs (light.R - 0.2) < 0.01 && Math.Abs (dark.R - 0.9) < 0.01, "named color from the asset catalog");

		var filled = Log.Find<UIButton> (View!, b => b.Configuration is not null)!;
		Log.Line ($"configured button title={filled?.Configuration?.Title} size={filled?.Frame.Size}");
		Check (filled?.Configuration?.Title == "Filled", "button configuration");

		var tapView = Log.Find<UIView> (View!, v => v.GestureRecognizers?.Length > 0);
		var tap = tapView?.GestureRecognizers?.OfType<UITapGestureRecognizer> ().FirstOrDefault ();
		Log.Line ($"tap recognizer={tap is not null} taps={tap?.NumberOfTapsRequired} enabled={tap?.Enabled}");
		Check (tap is not null && tap.Enabled && tap.NumberOfTapsRequired == 1, "tap gesture recognizer");

		Log.After (1.0, () => {
			filled!.SendActionForControlEvents (UIControlEvent.TouchUpInside);
			Log.After (1.5, () => {
				var items = nav.TopViewController as ItemsController;
				Check (items is not null, "show segue from a configured button");
				var plain = items?.TableView.CellAt (NSIndexPath.FromRowSection (0, 0));
				var detail = items?.TableView.CellAt (NSIndexPath.FromRowSection (1, 0));
				Log.Line ($"cells plain={plain?.TextLabel?.Text} detail={detail?.TextLabel?.Text}/{detail?.DetailTextLabel?.Text} accessory={detail?.Accessory} selection={detail?.SelectionStyle} rows={items?.TableView.NumberOfRowsInSection (0)}");
				Check (plain?.TextLabel?.Text == "Title" && plain.Accessory == UITableViewCellAccessory.None, "prototype cell");
				Check (detail?.DetailTextLabel?.Text == "Detail", "Value1 cell style");
				Check (detail?.Accessory == UITableViewCellAccessory.DisclosureIndicator, "cell accessory type");
				Check (detail?.SelectionStyle == UITableViewCellSelectionStyle.None, "cell selection style");
				// Select the cell the way a tap does, so its selection segue (the unwind) runs.
				var select = new ObjCRuntime.Selector ("_userSelectRowAtPendingSelectionIndexPath:");
				if (items?.TableView.RespondsToSelector (select) == true)
					items.TableView.PerformSelector (select, NSIndexPath.FromRowSection (0, 0), 0);
				else {
					Log.Line ("no user selection entry point; performing the unwind segue directly");
					items?.PerformSegue ("back", plain!);
				}
				Log.After (1.5, () => {
					Log.Line ($"after unwind depth={nav.ViewControllers!.Length} unwound={unwound}");
					Check (unwound && nav.ViewControllers!.Length == 1, "unwind segue");
					var grid = (GridController) Storyboard!.InstantiateViewController ("Grid");
					nav.PushViewController (grid, false);
					Log.After (1.5, () => {
						var layout = (UICollectionViewFlowLayout) grid.CollectionView.CollectionViewLayout;
						var cells = grid.CollectionView.VisibleCells;
						Log.Line ($"grid item={layout.ItemSize} line={layout.MinimumLineSpacing} interitem={layout.MinimumInteritemSpacing} direction={layout.ScrollDirection} inset={layout.SectionInset} cells={cells.Length}");
						Check (layout.ItemSize == new CGSize (50, 50) && layout.MinimumLineSpacing == 4 && layout.MinimumInteritemSpacing == 6, "flow layout sizes");
						Check (layout.ScrollDirection == UICollectionViewScrollDirection.Horizontal, "flow layout scroll direction");
						Check (layout.SectionInset == new UIEdgeInsets (12, 10, 16, 14), "flow layout section inset");
						Check (cells.Length == 3 && Math.Abs (Rgb (cells [0].ContentView.BackgroundColor, UIUserInterfaceStyle.Light).B - 0.8) < 0.01, "collection view cells");
						nav.PopViewController (false);
						var settings = (UITableViewController) Storyboard!.InstantiateViewController ("Settings");
						nav.PushViewController (settings, false);
						Log.After (1.5, () => {
							var tv = settings.TableView;
							var row = tv.CellAt (NSIndexPath.FromRowSection (0, 0));
							string? header = tv.DataSource?.TitleForHeader (tv, 0);
							string? footer = tv.DataSource?.TitleForFooter (tv, 0);
							Log.Line ($"static sections={tv.NumberOfSections ()} rows={tv.NumberOfRowsInSection (0)} cell={row?.TextLabel?.Text} header={header} footer={footer} style={tv.Style}");
							Check (tv.NumberOfSections () == 1 && row?.TextLabel?.Text == "Notifications" && tv.Style == UITableViewStyle.InsetGrouped, "static table");
							Check (header == "General" && footer == "Footer", "static table section titles");
							nav.PopViewController (false);
							Log.Find<UIButton> (View!, b => b.Title (UIControlState.Normal) == "Other storyboard")!.SendActionForControlEvents (UIControlEvent.TouchUpInside);
							Log.After (1.5, () => {
								var top = nav.TopViewController!;
								Log.Line ($"storyboard reference top={top.NavigationItem.Title} storyboard={top.Storyboard is not null}");
								Check (top.NavigationItem.Title == "Other", "storyboard reference");
								Log.Line (problems.Count == 0 ? "gaps ok" : "FAIL gaps: " + string.Join (", ", problems));
							});
						});
					});
				});
			});
		});
	}
}

[Register ("ItemsController")]
public class ItemsController : UITableViewController {
	public ItemsController (IntPtr handle) : base (handle) { }
	public override nint RowsInSection (UITableView tableView, nint section) => 2;
	public override UITableViewCell GetCell (UITableView tableView, NSIndexPath indexPath)
		=> tableView.DequeueReusableCell (indexPath.Row == 0 ? "Cell" : "Detail", indexPath);
}

[Register ("GridController")]
public class GridController : UICollectionViewController {
	public GridController (IntPtr handle) : base (handle) { }
	public override nint GetItemsCount (UICollectionView collectionView, nint section) => 3;
	public override UICollectionViewCell GetCell (UICollectionView collectionView, NSIndexPath indexPath)
		=> (UICollectionViewCell) collectionView.DequeueReusableCell ("Tile", indexPath);
}

// More.storyboard: picker, date picker, map and web views; pan, swipe, pinch, rotation, long press,
// screen edge and double tap recognizers; table header and footer views; collection view section
// headers and footers; and a custom collection view layout.
[Register ("MoreViewController")]
public class MoreViewController : UIViewController, IUIPickerViewDataSource, IUIPickerViewDelegate {
	public MoreViewController (IntPtr handle) : base (handle) { }
	[Outlet] public UIPickerView? picker { get; set; }
	[Outlet] public UIDatePicker? datePicker { get; set; }
	[Outlet] public MapKit.MKMapView? map { get; set; }
	[Outlet] public WebKit.WKWebView? web { get; set; }
	[Outlet] public UIView? gestureView { get; set; }
	readonly List<string> problems = new ();
	bool ran;
	void Check (bool ok, string what) { if (!ok) problems.Add (what); }

	[Export ("numberOfComponentsInPickerView:")]
	public nint GetComponentCount (UIPickerView pickerView) => 2;
	[Export ("pickerView:numberOfRowsInComponent:")]
	public nint GetRowsInComponent (UIPickerView pickerView, nint component) => component == 0 ? 3 : 5;
	[Export ("pickerView:titleForRow:forComponent:")]
	public string GetTitle (UIPickerView pickerView, nint row, nint component) => $"{component}:{row}";

	[Action ("dateChanged:")] public void DateChanged (UIDatePicker sender) { }
	[Action ("panned:")] public void Panned (UIPanGestureRecognizer r) { }
	[Action ("swiped:")] public void Swiped (UISwipeGestureRecognizer r) { }
	[Action ("pinched:")] public void Pinched (UIPinchGestureRecognizer r) { }
	[Action ("rotated:")] public void Rotated (UIRotationGestureRecognizer r) { }
	[Action ("longPressed:")] public void LongPressed (UILongPressGestureRecognizer r) { }
	[Action ("doubleTapped:")] public void DoubleTapped (UITapGestureRecognizer r) { }
	[Action ("edgePanned:")] public void EdgePanned (UIScreenEdgePanGestureRecognizer r) { }

	public override void ViewDidAppear (bool animated)
	{
		base.ViewDidAppear (animated);
		if (ran)
			return;
		ran = true;
		var nav = NavigationController!;
		Log.Line ($"picker components={picker?.NumberOfComponents} rows={picker?.RowsInComponent (1)} frame={picker?.Frame}");
		Check (picker?.NumberOfComponents == 2 && picker.RowsInComponent (1) == 5, "picker data source");
		Log.Line ($"date picker mode={datePicker?.Mode} style={datePicker?.PreferredDatePickerStyle} interval={datePicker?.MinuteInterval}");
		Check (datePicker?.Mode == UIDatePickerMode.Date && datePicker.PreferredDatePickerStyle == UIDatePickerStyle.Compact, "date picker");
		Log.Line ($"map type={map?.MapType} zoom={map?.ZoomEnabled} compass={map?.ShowsCompass} frame={map?.Frame}");
		Check (map is not null && map.MapType == MapKit.MKMapType.Standard && map.ZoomEnabled && map.ShowsCompass, "map view");
		Log.Line ($"web view={web is not null} detectors={web?.Configuration.DataDetectorTypes} inline={web?.Configuration.AllowsInlineMediaPlayback}");
		// Interface Builder's default: every kind of data detector (the seven bits, not the All mask).
		Check (web is not null && (ulong) web.Configuration.DataDetectorTypes == 127, "web view configuration");

		var recognizers = gestureView?.GestureRecognizers ?? [];
		var pan = recognizers.OfType<UIPanGestureRecognizer> ().FirstOrDefault (r => r is not UIScreenEdgePanGestureRecognizer);
		var swipe = recognizers.OfType<UISwipeGestureRecognizer> ().FirstOrDefault ();
		var press = recognizers.OfType<UILongPressGestureRecognizer> ().FirstOrDefault ();
		var tap = recognizers.OfType<UITapGestureRecognizer> ().FirstOrDefault ();
		var edge = View!.GestureRecognizers?.OfType<UIScreenEdgePanGestureRecognizer> ().FirstOrDefault ();
		Log.Line ($"recognizers={string.Join (",", recognizers.Select (r => r.GetType ().Name))} pan max={pan?.MaximumNumberOfTouches} swipe={swipe?.Direction}/{swipe?.NumberOfTouchesRequired} press={press?.MinimumPressDuration}/{press?.AllowableMovement} taps={tap?.NumberOfTapsRequired}/{tap?.NumberOfTouchesRequired} edges={edge?.Edges}");
		Check (recognizers.Length == 6 && recognizers.Any (r => r is UIPinchGestureRecognizer) && recognizers.Any (r => r is UIRotationGestureRecognizer), "recognizers attached");
		Check (pan?.MaximumNumberOfTouches == 2 && pan.MinimumNumberOfTouches == 1, "pan touches");
		Check (swipe?.Direction == UISwipeGestureRecognizerDirection.Left && swipe.NumberOfTouchesRequired == 2, "swipe direction and touches");
		Check (press is not null && Math.Abs (press.MinimumPressDuration - 0.75) < 0.001 && Math.Abs (press.AllowableMovement - 12) < 0.001, "long press settings");
		Check (tap?.NumberOfTapsRequired == 2 && tap.NumberOfTouchesRequired == 2, "double tap");
		Check (edge?.Edges == UIRectEdge.Left, "screen edge");

		bool scripted = false;
		web?.LoadHtmlString ("<html><body><p id='p'>hello</p></body></html>", null);
		Log.After (2.0, () => {
			web?.EvaluateJavaScript ("document.getElementById('p').textContent + (40 + 2)", (result, error) => {
				Log.Line ($"web script result={result} error={error?.LocalizedDescription}");
				scripted = result?.ToString () == "hello42";
			});
			Log.After (1.0, () => {
				Check (scripted, "web view loads and runs script");
				PerformSegue ("table", this);
				Log.After (1.5, () => {
					var table = (HeaderTableController) nav.TopViewController!;
					var header = table.TableView.TableHeaderView;
					var footer = table.TableView.TableFooterView;
					Log.Line ($"table header={header?.Frame} label={Log.Find<UILabel> (header ?? new UIView ())?.Text} footer={footer?.Frame} rows={table.TableView.VisibleCells.Length}");
					Check (Log.Find<UILabel> (header ?? new UIView ())?.Text == "Header" && footer is not null && footer.Frame.Height == 40, "table header and footer views");
					Check (table.TableView.VisibleCells.Length == 3, "table rows below the header");
					nav.PopViewController (false);
					PerformSegue ("flow", this);
					Log.After (1.5, () => {
						var flow = (SectionsController) nav.TopViewController!;
						var headers = flow.CollectionView.GetVisibleSupplementaryViews (UICollectionElementKindSectionKey.Header);
						var footers = flow.CollectionView.GetVisibleSupplementaryViews (UICollectionElementKindSectionKey.Footer);
						Log.Line ($"flow headers={headers.Length} footers={footers.Length} header={headers.FirstOrDefault ()?.GetType ().Name}/{headers.FirstOrDefault ()?.Frame} cells={flow.CollectionView.VisibleCells.Length}");
						Check (headers.Length == 2 && footers.Length == 2 && flow.CollectionView.VisibleCells.Length == 4, "collection view section headers and footers");
						nav.PopViewController (false);
						PerformSegue ("custom", this);
						Log.After (1.5, () => {
							var custom = (CustomLayoutController) nav.TopViewController!;
							var layout = custom.CollectionView.CollectionViewLayout;
							Log.Line ($"custom layout={layout.GetType ().Name} cells={custom.CollectionView.VisibleCells.Length} first={custom.CollectionView.VisibleCells.FirstOrDefault ()?.Frame}");
							Check (layout is GridLayout && custom.CollectionView.VisibleCells.Length == 6, "custom collection view layout");
							Log.Line (problems.Count == 0 ? "more ok" : "FAIL more: " + string.Join (", ", problems));
						});
					});
				});
			});
		});
	}
}

[Register ("HeaderTableController")]
public class HeaderTableController : UITableViewController {
	public HeaderTableController (IntPtr handle) : base (handle) { }
	public override nint RowsInSection (UITableView tableView, nint section) => 3;
	public override UITableViewCell GetCell (UITableView tableView, NSIndexPath indexPath) => tableView.DequeueReusableCell ("Row", indexPath);
}

[Register ("SectionsController")]
public class SectionsController : UICollectionViewController {
	public SectionsController (IntPtr handle) : base (handle) { }
	public override nint NumberOfSections (UICollectionView collectionView) => 2;
	public override nint GetItemsCount (UICollectionView collectionView, nint section) => 2;
	public override UICollectionViewCell GetCell (UICollectionView collectionView, NSIndexPath indexPath)
		=> (UICollectionViewCell) collectionView.DequeueReusableCell ("Item", indexPath);
	public override UICollectionReusableView GetViewForSupplementaryElement (UICollectionView collectionView, NSString elementKind, NSIndexPath indexPath)
		=> collectionView.DequeueReusableSupplementaryView (elementKind, elementKind == UICollectionElementKindSectionKey.Header ? "Header" : "Footer", indexPath);
}

[Register ("CustomLayoutController")]
public class CustomLayoutController : UICollectionViewController {
	public CustomLayoutController (IntPtr handle) : base (handle) { }
	public override nint GetItemsCount (UICollectionView collectionView, nint section) => 6;
	public override UICollectionViewCell GetCell (UICollectionView collectionView, NSIndexPath indexPath)
		=> (UICollectionViewCell) collectionView.DequeueReusableCell ("Cell", indexPath);
}

// A custom layout: two columns of 60-point squares.
[Register ("GridLayout")]
public class GridLayout : UICollectionViewLayout {
	public GridLayout (IntPtr handle) : base (handle) { }
	public GridLayout () { }
	[Export ("initWithCoder:")]
	public GridLayout (NSCoder coder) : base (coder) { }
	int Count => (int) (CollectionView?.NumberOfItemsInSection (0) ?? 0);
	CGRect FrameAt (int i) => new CGRect (20 + (i % 2) * 80, 120 + (i / 2) * 80, 60, 60);
	public override CGSize CollectionViewContentSize => new CGSize (CollectionView?.Bounds.Width ?? 0, 120 + (Count + 1) / 2 * 80);
	public override UICollectionViewLayoutAttributes[] LayoutAttributesForElementsInRect (CGRect rect)
		=> Enumerable.Range (0, Count).Select (i => LayoutAttributesForItem (NSIndexPath.FromItemSection (i, 0))!).Where (a => a.Frame.IntersectsWith (rect)).ToArray ();
	public override UICollectionViewLayoutAttributes? LayoutAttributesForItem (NSIndexPath indexPath)
	{
		var a = UICollectionViewLayoutAttributes.CreateForCell<UICollectionViewLayoutAttributes> (indexPath);
		a.Frame = FrameAt ((int) indexPath.Item);
		return a;
	}
}
