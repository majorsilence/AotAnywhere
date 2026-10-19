#!/usr/bin/env bash
# Installs the iOS workload's packs on Linux, where `dotnet workload install ios` refuses to run:
# the workload extends Mono's iOS AOT compilers, which only exist for macOS and Windows hosts.
# Native AOT builds don't use them, so this installs the rest of the workload - its manifest and
# the iOS SDK, reference and runtime packs from nuget.org - into the .NET install that `dotnet`
# runs from (which must be writable).
#
#   eng/install-ios-packs.sh [<iOS workload version, e.g. 26.5.10318> | latest]
#
# Also works on macOS and Windows (Git Bash), where `dotnet workload install ios` is the usual way.
# No workload is recorded as installed, so later `dotnet workload` commands may remove the packs
# again (as unused); run this again afterwards.
set -euo pipefail

requested=${1:-latest}
root=$(dotnet --list-sdks | tail -1 | sed -E 's/^[^[]*\[(.*)\]$/\1/')
root=$(cd "$root/.." && pwd)
sdk=$(dotnet --version)
# Workload manifests are published for the .100 band of each .NET version, which serves all its SDKs.
band=$(echo "$sdk" | sed -E 's/^([0-9]+)\.([0-9]+)\..*/\1.\2.100/')
feed=https://api.nuget.org/v3-flatcontainer
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

if [ "$requested" = latest ]; then
  requested=$(curl -sSfL "$feed/microsoft.net.sdk.ios.manifest-$band/index.json" \
    | python3 -c 'import json,sys; print([v for v in json.load(sys.stdin)["versions"] if "-" not in v][-1])')
fi
echo "iOS workload $requested (SDK $sdk, band $band, .NET at $root)"

# The manifest: pinned with a rollback file, the way `dotnet workload install --from-rollback-file`
# does it. With no workloads installed, `workload update` only installs manifests.
dotnet workload config --update-mode manifests
echo "{ \"microsoft.net.sdk.ios\": \"$requested/$band\" }" > "$work/rollback.json"
dotnet workload update --from-rollback-file "$work/rollback.json"
manifest="$root/sdk-manifests/$band/microsoft.net.sdk.ios/$requested/WorkloadManifest.json"
[ -f "$manifest" ] || { echo "the iOS manifest $requested was not installed ($manifest)" >&2; exit 1; }

# The packs of the ios workload and the workloads it extends (across all installed manifests),
# from those available on this host: on Linux that leaves out Mono's iOS AOT compilers and
# runtime packs (not used by Native AOT) but keeps the Mono build tooling the iOS SDK imports.
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) rid=linux-x64 ;; Linux-aarch64) rid=linux-arm64 ;;
  Darwin-arm64) rid=osx-arm64 ;; Darwin-x86_64) rid=osx-x64 ;;
  MINGW*-x86_64|MSYS*-x86_64) rid=win-x64 ;; *) rid=win-arm64 ;;
esac
python3 - "$root/sdk-manifests/$band" "$requested" "$rid" > "$work/packs" <<'PY'
import json, os, re, sys
root, ios_version, rid = sys.argv[1:]
def key(v): return [int(x) if x.isdigit() else x for x in re.split(r"[.-]", v)]
workloads, packs = {}, {}
for manifest in os.listdir(root):
    versions = [v for v in os.listdir(os.path.join(root, manifest)) if os.path.isfile(os.path.join(root, manifest, v, "WorkloadManifest.json"))]
    if not versions:
        continue
    version = ios_version if manifest == "microsoft.net.sdk.ios" else max(versions, key=key)
    text = open(os.path.join(root, manifest, version, "WorkloadManifest.json")).read()
    m = json.loads(re.sub(r",(\s*[}\]])", r"\1", text))  # manifests may have trailing commas
    workloads.update(m.get("workloads", {}))
    packs.update(m.get("packs", {}))
seen, wanted = set(), {}
def visit(name):
    if name in seen:
        return
    seen.add(name)
    w = workloads[name]
    if "platforms" not in w or rid in w["platforms"]:
        for p in w.get("packs", []):
            wanted[p] = packs[p]
    for e in w.get("extends", []):
        visit(e)
visit("ios")
for pack, info in wanted.items():
    if info["kind"] == "template":
        continue
    alias = info.get("alias-to")
    nuget = (alias.get(rid) or alias.get("any")) if alias else pack
    if nuget:
        print(nuget, info["version"])
PY

while read -r id version; do
  dest="$root/packs/$id/$version"
  if [ -d "$dest" ]; then echo "  $id $version: installed"; continue; fi
  lower=$(echo "$id" | tr '[:upper:]' '[:lower:]')
  echo "  $id $version"
  curl -sSfL -o "$work/pack.nupkg" "$feed/$lower/$version/$lower.$version.nupkg"
  mkdir -p "$dest.part"
  (cd "$dest.part" && unzip -q "$work/pack.nupkg" && rm -rf _rels package '[Content_Types].xml' ./*.nuspec)
  mv "$dest.part" "$dest"
done < "$work/packs"
echo "done"
