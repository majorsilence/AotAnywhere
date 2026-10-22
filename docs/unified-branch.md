# The unified branch

This fork (majorsilence/AotAnywhere) keeps each feature on its own branch, so each can be offered
upstream (StuDevLabs/AotAnywhere) separately, and merges them all into `unified` to build NuGet
packages of everything together. This file and `eng/unify-branches.sh` exist only on `unified`.

## Branches

| Branch | What it adds |
|---|---|
| `master` | Upstream's master. |
| `ios-support` | `StuDev.AotAnywhere.Apple`: `net*-ios` Native AOT builds on Linux, Windows and macOS without Xcode ([iOS targets](ios-targets.md)). |
| `macos-demote-runtime-exports` | Applies the SDK's exported-symbols list to macOS shared-library links ([macOS targets](macos-targets.md)). |
| `windows-hardening` | Control Flow Guard, EH continuation metadata and `/CETCOMPAT` in Windows cross-links ([Windows targets](windows-targets.md)). |
| `unified` | `master` plus the three above, and the packaging workflow. |

`windows-cfg-cetcompat` is an earlier, experimental version of `windows-hardening` (the same code,
with a different CI workflow). It isn't merged into `unified`: merging both would only conflict.

Changes go on the branch they belong to; `unified` only ever gets merges, plus its own files
(this one, `eng/unify-branches.sh`, `.github/workflows/unified-packages.yml`).

## Updating unified

```sh
eng/unify-branches.sh --push                    # merge master and every feature branch into unified
eng/unify-branches.sh --update-features --push  # first merge master into each feature branch too
```

Run it from any branch with a clean working tree (on Windows, from Git Bash); it switches back
when done. It only merges what is new, so it's safe to re-run. Without `--push` nothing leaves
the machine. `FEATURES`, `UNIFIED` and `REMOTE` override the branch list, the unified branch and
the remote.

If a merge conflicts, the script stops with the merge in progress: resolve the files, then
`git add <files> && git commit --no-edit`, and run it again. It turns on `git rerere`, which
records a resolution and replays it the next time the same conflict comes up - in that clone.
One is known: `README.md`, between `ios-support` and `windows-hardening`. Keep the iOS
paragraph, drop the old "Windows output skips some MSVC hardening" paragraph, and keep
`windows-hardening`'s "Windows hardening" paragraph.

## Packages

Every push to `unified` runs `.github/workflows/unified-packages.yml`, which packs
`StuDev.AotAnywhere` and `StuDev.AotAnywhere.Apple` and uploads them as the run's `packages`
artifact. Nothing is published.

Both get the same version: the next patch after upstream's newest release tag, as a pre-release
numbered by the run (upstream `v1.1.0` gives `1.1.1-unified.<run>`), so they sort after the
release they build on. `StuDev.AotAnywhere.Apple` pulls in `StuDev.AotAnywhere` at its own
version.

To use them, download the artifact and add its folder as a package source:

```sh
dotnet nuget add source /path/to/packages --name aotanywhere-unified
```

and reference `Version="1.1.1-unified.<run>"`.

The packages link with the stock zig from NuGet (`Vezel.Zig.Toolsets.<host>`). A patched zig
(`eng/zig-patches`) is optional; see its README.
