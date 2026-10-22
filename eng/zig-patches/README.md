# Zig patches

Changes to zig's own source, for a zig built with `eng/build-patched-zig.sh`. **Builds don't
need them:** by default the packages link with the stock zig they bring from NuGet
(`Vezel.Zig.Toolsets.<host>`, pinned in `src/ZigVersion.props`). A patched zig is used only when
a build opts in with `UseExternalZig=true` and puts it on `PATH`.

| Patch | What it changes | What uses it |
|---|---|---|
| `0001-link.MachO-honor-exported_symbols_list.patch` | zig's Mach-O linker honours `-exported_symbols_list` and `-exported_symbol` (ld64's matching, with wildcards): globals not on the list become private extern, so they leave the export trie and `-x` strips them. It also keeps a private-extern C common symbol private when it becomes a definition. | macOS `NativeLib=Shared` builds with `AotAnywhereZigExportsList=true` (see [macOS targets](../../docs/macos-targets.md)): without the patch, AotAnywhere hides non-exported symbols itself before the link, except roughly a hundred C common symbols, which only the patched linker hides. iOS builds don't use it: the iOS workload passes an exported-symbols list only for the app's executable, whose exports nothing loads by name, and AotAnywhere drops it (`ClangToZig.cs`). |

The patches aren't intended for zig upstream, which doesn't accept LLM-authored patches.

## Building a patched zig

```sh
eng/build-patched-zig.sh <out-dir> <target>...   # e.g. x86_64-linux-musl aarch64-macos-none x86_64-windows-gnu
```

`ZIG_VERSION` picks the zig (a release such as `0.17.0`, `master`, or a development version). It
runs on Linux or macOS and cross-builds for every target that zig publishes a prebuilt LLVM
"devkit" for: x86_64 and aarch64 Linux, aarch64 macOS and x86_64 Windows (no Intel Mac or
Windows on Arm). The iOS CI workflow (`.github/workflows/ios-validation.yml`) builds and caches
these for 0.17.0 and zig master, and checks the exports-list patch takes effect. Each build lands
in `zig-<target>.tar.gz` (`.zip` for Windows) with the layout of an official release.

## When zig moves on

The patches here apply to every version they still apply to cleanly. When a zig version needs a
different set, put it in `<version>/` or `<major.minor>/` next to this file; the build script
uses that folder for matching versions and this one otherwise.
