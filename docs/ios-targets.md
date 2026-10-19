# iOS targets (experimental)

`StuDev.AotAnywhere.Apple` builds `net*-ios` Native AOT apps on Windows, Linux and
macOS without Xcode, for running in the simulator or on your own devices. **It is
for local development only**: App Store and TestFlight builds are out of scope
and should be made with Apple's Xcode.

The stock iOS workload does all of the build logic. The package swaps the Xcode
tools it launches (`xcrun`, `clang`, `codesign`, `ibtool`, `actool`, `ditto`, ...)
for open-source C# implementations and zig, and points the workload at a
generated stand-in Xcode (version files only). No Apple SDK file is used: native
code compiles against declarations generated from the binding assemblies, and
links against the package's own linker stubs (see `src/Apple/sysroot/README.md`).

## Using it

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="StuDev.AotAnywhere.Apple" Version="..." />
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
</Project>
```

It is active for iOS targets on non-Mac hosts. On a Mac with Xcode it stays out
of the way unless `AotAnywhereAppleForce=true` (useful to test the off-Mac path).

```sh
# Simulator
dotnet build -c Release -r iossimulator-arm64 -p:PublishAot=true -p:_IsPublishing=true

# Device (signed; see below)
dotnet publish -c Release -r ios-arm64 \
  -p:AotAnywhereSignP12File=dev.p12 \
  -p:AotAnywhereSignP12PasswordFile=dev.p12.password \
  -p:AotAnywhereProvisioningProfile=MyApp_Development.mobileprovision
```

Native AOT is required: the Mono runtime's iOS AOT compiler only exists for
macOS hosts. Simulator RIDs can't be `dotnet publish`ed, hence the
`build -p:_IsPublishing=true` form.

### Installing the iOS workload

On macOS and Windows: `dotnet workload install ios`. On Linux that command refuses
to run, because the workload pulls in Mono's iOS AOT compilers, which only exist
for macOS and Windows. Native AOT doesn't use them, so install the rest of the
workload with the repository's script instead (into a .NET install you can write
to):

```sh
eng/install-ios-packs.sh            # the newest iOS workload
eng/install-ios-packs.sh 26.5.10318 # or a given version
```

It pins the iOS workload manifest and unpacks its SDK, reference and runtime packs
(and the Mono build tooling the SDK imports) from nuget.org. No workload is
recorded as installed, so a later `dotnet workload` command may remove the packs
as unused; run the script again afterwards.

Off a Mac, projects with storyboards or asset catalogs need iOS workload 26.5 or
later: earlier workloads run `actool` and `ibtool` through `/usr/bin/xcrun`, which
only exists on macOS.

### On Windows

The workload's Windows layer normally sends the build to a paired Mac. The package
builds locally instead: it switches the remote build off, points the SDK paths the
layer translates for the Mac back at this machine, and replaces the few Windows
targets that only copy files to and from the Mac. No Mac, SSH connection or Visual
Studio pairing is involved. In addition:

- The tool wrappers are copies of a small launcher compiled with zig on first use
  (`tools/launcher/aotanywhere-tool.c`), since Windows only runs executables. A
  `libc.dll` shim (`libc-shim.c`) covers the `realpath` that older workload versions'
  trimmer steps call on every host.
- The `.ipa` is rewritten with Unix file modes, which the workload's Windows zip
  leaves out; without them the app's executable isn't executable on the device.

A simulator build can't be run on Windows (there is no iOS simulator there); copy
the `.app` to a Mac with a simulator, or install the `.ipa` on a device.

## Signing device builds

Simulator builds are signed ad hoc and need nothing. A device build needs what
Xcode would otherwise find in your Keychain and `~/Library`:

| Property | What it is |
| --- | --- |
| `AotAnywhereSignP12File` | Your Apple Development certificate and its private key, exported as `.p12` (Keychain Access → Export, or from the Apple Developer portal). |
| `AotAnywhereSignP12PasswordFile` | A file holding the `.p12` password. It's a file rather than a property so the password stays out of build logs. Omit it for a `.p12` without a password. |
| `AotAnywhereProvisioningProfile` | A `.mobileprovision` from the Apple Developer portal that covers the app's bundle ID (exactly or by wildcard), lists your certificate, and lists the devices you'll install on. |

The build checks that the three belong together (certificate in the profile,
bundle ID covered, neither expired) before anything is compiled, then signs the
app, its frameworks and the embedded profile with that identity. Entitlements
come from the profile, as with Xcode. To build an unsigned device app instead,
set `EnableCodeSigning=false`.

Installing on a device needs a tool that talks to it, e.g. Apple Configurator
or Xcode's `devicectl` on a Mac, or `ideviceinstaller` (libimobiledevice)
elsewhere.

## What's covered

- **Storyboards and XIBs:**
  - views: view, image view, label, button (Default style, per-state titles,
    colours and images, or a Plain/Gray/Tinted/Filled button configuration),
    text field, text view, switch, slider, segmented control, activity
    indicator, scroll view and stack view;
  - table views (prototype cells in the four standard styles, with accessory
    and selection styles, or static cells in sections with header and footer
    titles) and collection views with a flow layout and prototype cells;
  - tap gesture recognizers, with their actions;
  - Dynamic Type text styles, and named colours from the app's asset catalog;
  - Auto Layout constraints, including the safe area layout guide;
  - outlets and actions (target/action with the control event) on view
    controllers, File's Owner and other objects;
  - view controllers, table view controllers, collection view controllers,
    navigation controllers and tab bar controllers, with navigation items, bar
    button items and tab bar items;
  - show, show detail, modal and unwind segues, triggered by controls, bar
    button items, cell selection or `PerformSegue`; storyboard references, and
    several storyboards in one app.

  Not supported: other gesture recognizers (pan, swipe, ...), custom collection
  view layouts and self-sizing collection cells, table header, footer and
  background views, collection view section headers and footers, and other
  views (picker, date picker, map, web view, ...). Any of these stops the build
  with an error naming it; set `AOTANYWHERE_IBTOOL_TRACE=1` to see where in the
  compiler it was found.
- **Asset catalogs:** app icons (including Xcode 14's single 1024px icon, scaled
  to each size); image sets from PNG, PDF or SVG files (vectors are rendered to
  1x, 2x and 3x bitmaps, and kept when "Preserve Vector Data" is set); sRGB
  colour sets; data sets (`NSDataAsset`); and dark-mode variants of each.
  PDFs and SVGs may use filled and stroked paths and shapes with solid colours
  and transforms; text, gradients, images and clipping in them are errors, as
  are template rendering, references to system colours, other colour spaces,
  high-contrast variants and other asset types.
- **Not available:** Metal shader compilation, Core ML models, SceneKit assets,
  texture atlases, and `.dSYM` bundles (symbols are stripped at link time).
- **Swift back-deployment:** Swift code in an app (from a binding's xcframework,
  say) links against the iOS Swift runtime, but Swift's back-deployment
  libraries (`swiftCompatibility56`, `swiftCompatibilityConcurrency`, ...) only
  ship with Xcode's toolchain and are left out, so their runtime fixes are
  missing on older iOS versions. The build warns when that matters for the app's
  deployment target: Swift code should target iOS 15.4 or later (iOS 15.0 or
  later for Swift concurrency alone).

## Validation

`.github/workflows/ios-validation.yml` builds `test/ios/IBTest` (storyboards
whose controllers drive their own outlets, actions, segues, cells and gesture
recognizers, and an asset catalog it checks at run time) on Linux, Windows and
macOS, with iOS workloads 26.0, 26.5 and 27.0, and runs every build on a Mac's
simulator. The workflow builds zig 0.17.0 with `eng/zig-patches`
(`eng/build-patched-zig.sh`) for each host to link with; a second job does the
same with zig's latest development build, as an early warning that doesn't fail
the run, as does a build with the newest iOS workload available.

`eng/build-patched-zig.sh` takes the zig version in `ZIG_VERSION` (a release,
`master`, or a development version). The patches in `eng/zig-patches` apply to
both; a version they stop applying to gets its own set in
`eng/zig-patches/<version>/` or `eng/zig-patches/<major.minor>/`. Locally: build the tools
(`dotnet build src/Apple/tools/AotAnywhere.AppleTools -c Release`), build the app
for the simulator, and run `test/ios/run-sim.py <simulator UDID> <IBTest.app>`.

These builds are for local development only; see the repository README.
