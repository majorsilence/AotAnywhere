#!/usr/bin/env bash
# Builds zig with eng/zig-patches applied, for one or more host targets, from a Linux or macOS
# machine - the way zig's own release CI does (ci/*-release.* in the zig repository): the official
# zig release compiles the patched source against a prebuilt static LLVM/Clang/LLD "devkit" for
# each target from ziglang.org/deps, so no LLVM is built and every output is a self-contained zig.
#
#   eng/build-patched-zig.sh <out-dir> <target>...
#
# Targets with a devkit: x86_64-linux-musl, aarch64-linux-musl, aarch64-macos-none,
# x86_64-windows-gnu. Each lands in <out-dir>/zig-<target>.tar.gz (.zip for Windows), holding
# zig-<target>/zig[.exe] and zig-<target>/lib - the layout of an official release.
set -euo pipefail

ZIG_VERSION=${ZIG_VERSION:-0.17.0}
ZIG_SRC_SHA256=${ZIG_SRC_SHA256:-b6c7f1728f043700d6529bac980800792f824256a9d2f1839b3d62beed0b8abd}
# The devkit zig's own 0.17.0 CI scripts use.
ZIG_DEVKIT=${ZIG_DEVKIT:-0.17.0-dev.203+073889523}

out=$(mkdir -p "$1" && cd "$1" && pwd); shift
repo=$(cd "$(dirname "$0")/.." && pwd)
work=${ZIG_BUILD_WORK:-$out/.work}
mkdir -p "$work"
cd "$work"

fetch() { [ -f "$2" ] || { curl -sSfL -o "$2.part" "$1" && mv "$2.part" "$2"; }; }

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)  host=x86_64-linux ;;
  Linux-aarch64) host=aarch64-linux ;;
  Darwin-arm64)  host=aarch64-macos ;;
  Darwin-x86_64) host=x86_64-macos ;;
  *) echo "unsupported build host $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

# The official zig, to compile with.
fetch "https://ziglang.org/download/$ZIG_VERSION/zig-$host-$ZIG_VERSION.tar.xz" zig-host.tar.xz
[ -d host ] || { mkdir host && tar xf zig-host.tar.xz -C host --strip-components 1; }

# The patched source.
fetch "https://ziglang.org/download/$ZIG_VERSION/zig-$ZIG_VERSION.tar.xz" zig-src.tar.xz
echo "$ZIG_SRC_SHA256  zig-src.tar.xz" | { command -v sha256sum >/dev/null && sha256sum -c || shasum -a 256 -c; }
rm -rf src && mkdir src && tar xf zig-src.tar.xz -C src --strip-components 1
for p in "$repo"/eng/zig-patches/*.patch; do (cd src && git apply -v "$p"); done

for target in "$@"; do
  devkit="devkit-$target"
  if [ ! -d "$devkit" ]; then
    mkdir "$devkit"
    case "$target" in
      *-windows-*)
        fetch "https://ziglang.org/deps/zig+llvm+lld+clang-$target-$ZIG_DEVKIT.zip" "$devkit.zip"
        (cd "$devkit" && unzip -q "../$devkit.zip")
        # The zip holds one top-level folder, written with Windows separators: fix up the modes.
        chmod -R u+rwX "$devkit" ;;
      *)
        fetch "https://ziglang.org/deps/zig+llvm+lld+clang-$target-$ZIG_DEVKIT.tar.xz" "$devkit.tar.xz"
        tar xf "$devkit.tar.xz" -C "$devkit" ;;
    esac
  fi
  prefix=$(cd "$devkit"/zig+llvm+lld+clang-* && pwd)

  echo "== zig $ZIG_VERSION (patched) for $target"
  rm -rf "build-$target"
  (cd src && ../host/zig build --prefix "../build-$target" --search-prefix "$prefix" \
     -Dtarget="$target" -Dcpu=baseline -Doptimize=ReleaseFast -Dstrip -Duse-zig-libcxx -Denable-llvm \
     -Dversion-string="$ZIG_VERSION" -Dno-lib)

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
