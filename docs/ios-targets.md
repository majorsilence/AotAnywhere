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

- **Storyboards and XIBs:** views, image views, labels and Auto Layout
  constraints, which covers launch screens such as MAUI's splash and Avalonia's
  launch XIB. Anything else stops the build with an error naming the element.
- **Asset catalogs:** app icons (including Xcode 14's single 1024px icon, scaled
  to each size) and PNG image sets. Colour sets, PDF/SVG images, dark-mode
  variants and other asset types are errors.
- **Not available:** Metal shader compilation, Core ML models, SceneKit assets,
  texture atlases, and `.dSYM` bundles (symbols are stripped at link time).

These builds are for local development only; see the repository README.
