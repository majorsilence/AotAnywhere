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
		Window.RootViewController = UIStoryboard.FromName (name, null).InstantiateInitialViewController ();
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
