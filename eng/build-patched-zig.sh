#!/usr/bin/env bash
# Builds zig with eng/zig-patches applied, for one or more host targets, from a Linux or macOS
# machine - the way zig's own release CI does (ci/*-release.* in the zig repository): an official
# zig compiles the patched source against a prebuilt static LLVM/Clang/LLD "devkit" for each target
# from ziglang.org/deps, so no LLVM is built and every output is a self-contained zig.
#
#   eng/build-patched-zig.sh <out-dir> <target>...
#
# ZIG_VERSION picks the zig: a release ("0.17.0", the default), "master" (the latest development
# build), or a development version ("0.18.0-dev.253+16d31ceed", while ziglang.org still serves it).
# Its source, the official zig to compile with, and the devkit version are looked up from
# ziglang.org/download/index.json and that version's own CI scripts; ZIG_DEVKIT overrides the
# devkit version.
#
# Patches: eng/zig-patches/<version>/ or eng/zig-patches/<major.minor>/ when one exists (for a
# version the default series doesn't apply to), else eng/zig-patches/*.patch.
#
# Targets with devkits: x86_64-linux-musl, aarch64-linux-musl, aarch64-macos-none,
# x86_64-windows-gnu. Each lands in <out-dir>/zig-<target>.tar.gz (.zip for Windows), holding
# zig-<target>/zig[.exe] and zig-<target>/lib - the layout of an official release - and
# <out-dir>/VERSION records the zig version built.
set -euo pipefail

requested=${ZIG_VERSION:-0.17.0}
out=$(mkdir -p "$1" && cd "$1" && pwd); shift
repo=$(cd "$(dirname "$0")/.." && pwd)
work=${ZIG_BUILD_WORK:-$out/.work}
mkdir -p "$work"
cd "$work"

fetch() { [ -f "$2" ] || { curl -sSfL -o "$2.part" "$1" && mv "$2.part" "$2"; }; }
sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)  host=x86_64-linux ;;
  Linux-aarch64) host=aarch64-linux ;;
  Darwin-arm64)  host=aarch64-macos ;;
  Darwin-x86_64) host=x86_64-macos ;;
  *) echo "unsupported build host $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

# Resolve the version: "<version> <src-url> <src-sha256> <host-url> <host-sha256>" (empty hashes
# for development builds index.json no longer lists).
curl -sSfL -o index.json https://ziglang.org/download/index.json
read -r version src_url src_sha host_url host_sha < <(python3 - "$requested" "$host" <<'PY'
import json, sys
requested, host = sys.argv[1], sys.argv[2]
index = json.load(open("index.json"))
entry = index.get(requested)
if entry is None and requested == index["master"]["version"]:
    entry = index["master"]
if entry is not None:
    version = entry.get("version", requested)
    print(version, entry["src"]["tarball"], entry["src"]["shasum"], entry[host]["tarball"], entry[host]["shasum"])
elif "-dev." in requested:
    base = "https://ziglang.org/builds"
    print(requested, f"{base}/zig-{requested}.tar.xz", "-", f"{base}/zig-{host}-{requested}.tar.xz", "-")
else:
    sys.exit(f"zig {requested} is not in ziglang.org/download/index.json")
PY
)
echo "zig $version (requested: $requested)"

check() { [ "$2" = "-" ] || [ "$(sha256 "$1")" = "$2" ] || { echo "checksum mismatch for $1" >&2; exit 1; }; }

# The official zig of that version, to compile with.
rm -rf host && mkdir host
fetch "$host_url" "zig-host-$version.tar.xz"; check "zig-host-$version.tar.xz" "$host_sha"
tar xf "zig-host-$version.tar.xz" -C host --strip-components 1

# The patched source.
fetch "$src_url" "zig-src-$version.tar.xz"; check "zig-src-$version.tar.xz" "$src_sha"
rm -rf src && mkdir src && tar xf "zig-src-$version.tar.xz" -C src --strip-components 1
patches="$repo/eng/zig-patches"
minor=$(echo "$version" | cut -d. -f1-2)
if [ -d "$patches/$version" ]; then patches="$patches/$version"; elif [ -d "$patches/$minor" ]; then patches="$patches/$minor"; fi
for p in "$patches"/*.patch; do echo "applying $(basename "$p")"; (cd src && git apply "$p"); done

# The devkit zig's own CI uses for this version (the same for every target).
devkit=${ZIG_DEVKIT:-}
if [ -z "$devkit" ]; then
  case "$version" in
    *-dev.*) ref="commit/${version##*+}" ;;
    *)       ref="tag/$version" ;;
  esac
  devkit=$(curl -sSfL "https://codeberg.org/ziglang/zig/raw/$ref/ci/aarch64-macos-release.sh" \
    | sed -n 's/.*zig+llvm+lld+clang-\$TARGET-\([^"]*\)".*/\1/p' | head -1)
  [ -n "$devkit" ] || { echo "could not find the devkit version for zig $version; set ZIG_DEVKIT" >&2; exit 1; }
fi
echo "devkit $devkit"

for target in "$@"; do
  dk="devkit-$target-$devkit"
  if [ ! -d "$dk" ]; then
    mkdir "$dk"
    case "$target" in
      *-windows-*)
        fetch "https://ziglang.org/deps/zig+llvm+lld+clang-$target-$devkit.zip" "$dk.zip"
        # The zip is written with Windows separators: unzip warns (exit status 1) and converts them,
        # and the folders it creates need their modes fixing up.
        (cd "$dk" && { unzip -q "../$dk.zip" || [ $? -eq 1 ]; })
        chmod -R u+rwX "$dk" ;;
      *)
        fetch "https://ziglang.org/deps/zig+llvm+lld+clang-$target-$devkit.tar.xz" "$dk.tar.xz"
        tar xf "$dk.tar.xz" -C "$dk" ;;
    esac
  fi
  prefix=$(cd "$dk"/zig+llvm+lld+clang-* && pwd)

  echo "== zig $version (patched) for $target"
  rm -rf "build-$target"
  (cd src && ../host/zig build --prefix "../build-$target" --search-prefix "$prefix" \
     -Dtarget="$target" -Dcpu=baseline -Doptimize=ReleaseFast -Dstrip -Duse-zig-libcxx -Denable-llvm \
     -Dversion-string="$version" -Dno-lib)

  name="zig-$target"
  rm -rf "$name" && mkdir "$name"
  cp build-$target/bin/zig* "$name/"
  cp -R src/lib "$name/lib"
  case "$target" in
    *-windows-*) rm -f "$out/$name.zip"; zip -qr "$out/$name.zip" "$name" ;;
    *)           tar czf "$out/$name.tar.gz" "$name" ;;
  esac
  echo "   -> $(ls "$out/$name".*)"
done
echo "$version" > "$out/VERSION"
