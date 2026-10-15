#!/usr/bin/env python3
"""Regenerates the .xip test fixtures (python3 make-fixtures.py, from this directory).

Apple's .xip archives cannot be committed or fetched in CI, so these reproduce the format with an
independent encoder (Python's lzma module) to check the C# decoder against:

  xar  { zlib XML TOC, heap: "Content" }      (header: "xar!", big-endian sizes)
  pbzx { "pbzx", u64 chunk size, chunks of { u64 uncompressed, u64 compressed, xz | stored } }
  cpio "odc" (070707), paths under "./Xcode.app/", with directories, a symlink and a hard link

and plain .xz streams exercising the LZMA2 decoder (several lc/lp/pb settings, multi-chunk
streams, dictionary-spanning matches). Expected outputs are recorded as SHA-256 in expected.txt.
"""
import hashlib, lzma, os, random, struct, zlib

HERE = os.path.dirname(os.path.abspath(__file__))
rng = random.Random(1234)


def text_like(n):
    words = [b"Foundation", b"UIKit", b"NSObject", b"@property", b"(nonatomic)", b"API_AVAILABLE(ios(15.0))",
             b"#import", b"<UIKit/UIKit.h>", b"typedef", b"NS_ENUM", b"\n", b"  ", b";", b"*", b"_Nullable"]
    out = bytearray()
    while len(out) < n:
        out += rng.choice(words)
        if rng.random() < 0.3:
            out += b" "
    return bytes(out[:n])


def xz(data, check=lzma.CHECK_CRC64, **lzma2):
    filters = [dict(id=lzma.FILTER_LZMA2, preset=6, **lzma2)]
    return lzma.compress(data, format=lzma.FORMAT_XZ, check=check, filters=filters)


expected = []

# --- Plain xz streams for the LZMA2 decoder ---------------------------------------------------
cases = {
    "text.xz": (text_like(300_000), {}),
    "random.xz": (rng.randbytes(70_000), {}),                                  # mostly uncompressed LZMA2 chunks
    "lc0lp2pb0.xz": (text_like(120_000), dict(lc=0, lp=2, pb=0)),
    "lc4lp0pb4.xz": (text_like(120_000), dict(lc=4, lp=0, pb=4)),
    "longmatch.xz": ((text_like(40_000) + rng.randbytes(5_000)) * 60, {}),     # far, long rep matches; > 2 MiB -> many LZMA2 chunks
    "nocheck.xz": (text_like(50_000), dict(check=lzma.CHECK_NONE)),
    "empty.xz": (b"", {}),
}
for name, (data, opts) in cases.items():
    check = opts.pop("check", lzma.CHECK_CRC64)
    with open(os.path.join(HERE, name), "wb") as f:
        f.write(xz(data, check=check, **opts))
    expected.append(f"{name} {len(data)} {hashlib.sha256(data).hexdigest()}")


# --- A synthetic Xcode .xip -------------------------------------------------------------------
def odc(name, mode, data=b"", ino=0, nlink=1):
    n = name.encode() + b"\0"
    hdr = b"070707" + b"%06o" % 0o1 + b"%06o" % ino + b"%06o" % mode + b"%06o" % 0 + b"%06o" % 0 \
        + b"%06o" % nlink + b"%06o" % 0 + b"%011o" % 0 + b"%06o" % len(n) + b"%011o" % len(data)
    assert len(hdr) == 76
    return hdr + n + data


sdk = "./Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs"
uikit_h = text_like(200_000)
tbd = b"--- !tapi-tbd\ntbd-version: 4\ntargets: [ arm64-ios ]\ninstall-name: /usr/lib/libSystem.B.dylib\n"
version_plist = b'<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict><key>CFBundleShortVersionString</key><string>27.0</string></dict></plist>'
entries = [
    odc("./Xcode.app", 0o40755),
    odc("./Xcode.app/Contents", 0o40755),
    odc("./Xcode.app/Contents/version.plist", 0o100644, version_plist),
    odc("./Xcode.app/Contents/Info.plist", 0o100644, b"<plist version=\"1.0\"><dict/></plist>"),
    odc("./Xcode.app/Contents/MacOS", 0o40755),
    odc("./Xcode.app/Contents/MacOS/Xcode", 0o100755, rng.randbytes(150_000)),          # not wanted
    odc("./Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Info.plist", 0o100644, b"<plist version=\"1.0\"><dict/></plist>"),
    odc("./Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/Library/big.bin", 0o100644, rng.randbytes(90_000)),  # not wanted
    odc(sdk, 0o40755),
    odc(sdk + "/iPhoneOS27.0.sdk", 0o40755),
    odc(sdk + "/iPhoneOS27.0.sdk/System/Library/Frameworks/UIKit.framework/Headers/UIKit.h", 0o100644, uikit_h),
    odc(sdk + "/iPhoneOS27.0.sdk/usr/lib/libSystem.tbd", 0o100644, tbd, ino=77, nlink=2),
    odc(sdk + "/iPhoneOS27.0.sdk/usr/lib/libSystem.B.tbd", 0o100644, b"", ino=77, nlink=2),  # odc hard link: data stored once
    odc(sdk + "/iPhoneOS.sdk", 0o120755, b"iPhoneOS27.0.sdk"),                                # symlink
    odc("TRAILER!!!", 0),
]
cpio = b"".join(entries)

# pbzx: 64 KiB chunks, mostly xz (alternating checks) plus one stored chunk of incompressible data.
chunk = 64 * 1024
pbzx = bytearray(b"pbzx" + struct.pack(">Q", chunk))
for i in range(0, len(cpio), chunk):
    piece = cpio[i:i + chunk]
    packed = xz(piece, check=lzma.CHECK_CRC64 if (i // chunk) % 2 == 0 else lzma.CHECK_NONE)
    if len(packed) >= len(piece):
        packed = piece   # stored
    pbzx += struct.pack(">QQ", len(piece), len(packed)) + packed

content = bytes(pbzx)
meta = b"meta"


# Like real .xip files: SHA-1 checksums, with the TOC's checksum stored first in the heap.
def data_xml(blob, offset):
    h = hashlib.sha1(blob).hexdigest()
    return (f"<data><length>{len(blob)}</length><offset>{offset}</offset><size>{len(blob)}</size>"
            f'<encoding style="application/octet-stream"/>'
            f'<extracted-checksum style="sha1">{h}</extracted-checksum><archived-checksum style="sha1">{h}</archived-checksum></data>')


toc = f"""<?xml version="1.0" encoding="UTF-8"?>
<xar><toc><creation-time>2026-01-01T00:00:00</creation-time>
<checksum style="sha1"><offset>0</offset><size>20</size></checksum>
<file id="1"><name>Metadata</name><type>file</type>{data_xml(meta, 20)}</file>
<file id="2"><name>Content</name><type>file</type>{data_xml(content, 20 + len(meta))}</file>
</toc></xar>""".encode()
toc_z = zlib.compress(toc)
header = b"xar!" + struct.pack(">HHQQI", 28, 1, len(toc_z), len(toc), 1)   # checksum algorithm 1 = SHA-1
with open(os.path.join(HERE, "synthetic.xip"), "wb") as f:
    f.write(header + toc_z + hashlib.sha1(toc_z).digest() + meta + content)

expected.append(f"UIKit.h {len(uikit_h)} {hashlib.sha256(uikit_h).hexdigest()}")
expected.append(f"libSystem.tbd {len(tbd)} {hashlib.sha256(tbd).hexdigest()}")

with open(os.path.join(HERE, "expected.txt"), "w") as f:
    f.write("\n".join(expected) + "\n")
print("\n".join(expected))
