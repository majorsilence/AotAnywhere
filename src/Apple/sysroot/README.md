# iOS linker stub sysroot

Mach-O linker stubs (`.tbd` files) for the iOS system libraries and frameworks, so that
`StuDev.AotAnywhere.Apple` can link `net*-ios` apps without Xcode's SDK
(`AotAnywhereAppleLinkStubs=true`). Stubs only matter at link time; at run time the real
libraries on the device or simulator are loaded.

**No Apple SDK file is used to make them.** `eng/generate-apple-ios-sysroot.cs` builds them from
open-source inputs only, the way Microsoft.iOS's own bindings describe Apple's libraries. The rules
are in `IosStubs` (AotAnywhere.AppleTools).

**Which symbols:** those MIT-licensed inputs can make an app import:
- the undefined symbols of the iOS NativeAOT and Microsoft.iOS runtime packs' native libraries;
- the undefined symbols of well-known third-party static libraries (MAUI's Swift interop);
- Microsoft.iOS.dll's P/Invoke entry points, `[Field]` globals and wrapper classes;
- zig's libSystem list, and the C++ ABI's runtime entry points.

**Which library:**

| Symbols | Source |
| --- | --- |
| Functions and globals | The library the bindings name. Mono resolves each of those with `dlopen(path)` + `dlsym`, so the paths are exercised. |
| libSystem | zig's list (MIT) |
| libobjc, libc++, zlib, ICU | Their open-source projects' API names and install names |
| Swift | The module the mangled name encodes. Swift 5.1's open-source Darwin overlays (the libraries iOS has shipped since 12.2) bind to `libswift<Module>`; other modules bind to whatever the inputs' own objects autolink (`-lswiftX` or `-framework X`). The one library split Swift's open source records (`_Builtin_float` out of `libswiftDarwin` in iOS 18, `magic-symbols-for-install-name.c`) is a `$ld$previous$` directive. |
| Objective-C classes | Listed under the framework their binding namespace names, as a hint only. See below. |

**Objective-C classes resolve at run time.** Microsoft.iOS never names a class's library: it
finds classes with `objc_getClass`. Classes also move between frameworks; `NSLayoutConstraint`
is in the UIKit namespace but implemented in Foundation. So at link time `StubLink` leaves class
references to dyld's flat lookup, which finds each class wherever it lives on the running iOS
version. The hint only keeps the class's framework linked. Every other undefined symbol is still
a link error.

**Versions:**
- Compatibility versions are 0.0.0. dyld only requires an installed library's to be at least the
  recorded one, so 0.0.0 always passes.
- Current versions are left out.
- `SDKSettings.json` carries only the SDK version the workload targets, which zig records in
  `LC_BUILD_VERSION`.

To regenerate (any host with zig on PATH, or pass `--zig`):

```sh
dotnet run eng/generate-apple-ios-sysroot.cs
```

Update the pinned pack versions in that script when new workload or runtime packs ship.

These builds are for local development only; see the repository README.
