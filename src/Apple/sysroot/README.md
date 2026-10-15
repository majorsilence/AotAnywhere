# iOS linker stub sysroot

Mach-O linker stubs (`.tbd` files) for the iOS system libraries and frameworks, so that
`StuDev.AotAnywhere.Apple` can link `net*-ios` apps without Xcode's SDK
(`AotAnywhereAppleLinkStubs=true`). Stubs only matter at link time; at run time the real
libraries on the device or simulator are loaded.

These files are **not** copies of Apple SDK stubs. `eng/generate-apple-ios-sysroot.cs` writes
them fresh, listing only symbols that MIT-licensed inputs can make an app import:

- the undefined symbols of the iOS NativeAOT and Microsoft.iOS runtime packs' native libraries;
- Microsoft.iOS.dll's P/Invoke entry points, `[Field]` globals and wrapper classes;
- the undefined symbols of well-known third-party static libraries (MAUI's Swift interop);
- zig's own libSystem symbol list, and the C++ ABI's runtime entry points.

The local SDK is only the lookup table for which library exports each of those symbols (the
approach of `src/apple-sysroot` for macOS, and of zig's bundled libSystem stub). For them the
stubs also keep:

- **Exports vs re-exports.** A library's own symbols are listed under `exports:`, and symbols it
  re-exports from another library under `reexports:`.
- **Back-deployment directives.** `$ld$previous$`, `$ld$hide$`, `$ld$add$` and `$ld$install_name$`
  record which library held a symbol on older iOS versions.

At link time, `StubLink` (in AotAnywhere.AppleTools) uses this to bind each symbol to the
library ld64 would choose for the app's deployment target. Every public library and framework
gets a stub, even an empty one, because the workload names libraries directly (`-liconv`,
`-framework X`). `SDKSettings.json` carries only the SDK version, which zig records in
`LC_BUILD_VERSION`.

To regenerate (macOS with Xcode, and zig on PATH or passed with `--zig`):

```sh
dotnet run eng/generate-apple-ios-sysroot.cs
```

Update the pinned pack versions in that script when new workload or runtime packs ship.
