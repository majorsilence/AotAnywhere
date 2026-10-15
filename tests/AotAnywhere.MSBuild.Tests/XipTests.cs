using System.Security.Cryptography;
using AotAnywhere.AppleTools;
using AotAnywhere.AppleTools.Xip;

namespace AotAnywhere.MSBuild.Tests;

// Fixtures come from fixtures/xip/make-fixtures.py, which encodes with Python's lzma
// (an independent xz encoder; the `xz` CLI validates them) and builds the .xip so
// that Apple's own `xar` tool lists and extracts it. Expected outputs are SHA-256
// sums in fixtures/xip/expected.txt.
public class XipTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "xip", name);

    static readonly Dictionary<string, (int Length, string Sha256)> Expected =
        File.ReadAllLines(Fixture("expected.txt"))
            .Where(l => l.Length > 0)
            .Select(l => l.Split(' '))
            .ToDictionary(p => p[0], p => (int.Parse(p[1]), p[2]));

    [Test]
    [Arguments("text.xz")]
    [Arguments("random.xz")]
    [Arguments("lc0lp2pb0.xz")]
    [Arguments("lc4lp0pb4.xz")]
    [Arguments("longmatch.xz")]
    [Arguments("nocheck.xz")]
    [Arguments("empty.xz")]
    public async Task XzDecodesToTheEncodedBytes(string name)
    {
        var (length, sha) = Expected[name];

        var output = XzDecoder.Decode(File.ReadAllBytes(Fixture(name)), length);

        await Assert.That(Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant()).IsEqualTo(sha);
    }

    [Test]
    public async Task XzWrongDeclaredSizeIsAnError()
    {
        var data = File.ReadAllBytes(Fixture("text.xz"));

        await Assert.That(() => XzDecoder.Decode(data, Expected["text.xz"].Length - 1)).Throws<XipFormatException>();
    }

    [Test]
    public async Task CorruptXzIsAnErrorNotACrash()
    {
        var data = File.ReadAllBytes(Fixture("text.xz"));
        for (int i = 200; i < data.Length - 100; i += 997)
            data[i] ^= 0x5a;

        await Assert.That(() => XzDecoder.Decode(data, Expected["text.xz"].Length)).Throws<XipFormatException>();
    }

    [Test]
    public async Task ImportsOnlyTheIosSdkFromAXip()
    {
        string output = Path.Combine(Path.GetTempPath(), "aotanywhere-xip-" + Guid.NewGuid().ToString("N"), "Xcode.app");

        XcodeImporter.ImportXip(Fixture("synthetic.xip"), output);

        string sdks = Path.Combine(output, "Contents", "Developer", "Platforms", "iPhoneOS.platform", "Developer", "SDKs");
        string uikit = Path.Combine(sdks, "iPhoneOS27.0.sdk", "System", "Library", "Frameworks", "UIKit.framework", "Headers", "UIKit.h");
        await Assert.That(Sha(uikit)).IsEqualTo(Expected["UIKit.h"].Sha256);
        await Assert.That(File.Exists(Path.Combine(output, "Contents", "version.plist"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "Contents", "Developer", "Platforms", "iPhoneOS.platform", "Info.plist"))).IsTrue();

        // odc hard link: the second name carries no data and must get the first's.
        string lib = Path.Combine(sdks, "iPhoneOS27.0.sdk", "usr", "lib");
        await Assert.That(Sha(Path.Combine(lib, "libSystem.B.tbd"))).IsEqualTo(Expected["libSystem.tbd"].Sha256);

        // The symlinked unversioned SDK resolves (a link on Unix, a copy on Windows).
        await Assert.That(File.Exists(Path.Combine(sdks, "iPhoneOS.sdk", "usr", "lib", "libSystem.tbd"))).IsTrue();

        // Everything outside the allowlist is left behind; Contents/MacOS/Xcode is only an
        // empty placeholder for the workload's existence check.
        await Assert.That(new FileInfo(Path.Combine(output, "Contents", "MacOS", "Xcode")).Length).IsEqualTo(0L);
        await Assert.That(Directory.Exists(Path.Combine(output, "Contents", "Developer", "usr", "bin"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "Contents", "Developer", "Platforms", "iPhoneOS.platform", "Developer", "Library", "big.bin"))).IsFalse();

        // The importer's own SDK lookup finds the result.
        await Assert.That(Path.GetFileName(AppleSdk.SdkPath(Path.Combine(output, "Contents", "Developer"), "iphoneos"))).IsEqualTo("iPhoneOS27.0.sdk");
    }

    [Test]
    public async Task NonXipInputIsAnError()
    {
        string bogus = Path.Combine(Path.GetTempPath(), "aotanywhere-bogus-" + Guid.NewGuid().ToString("N") + ".xip");
        File.WriteAllBytes(bogus, new byte[64]);

        await Assert.That(() => XcodeImporter.ImportXip(bogus, bogus + ".out")).Throws<XipFormatException>();
    }

    [Test]
    [Arguments("Contents/version.plist", true)]
    [Arguments("Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS27.0.sdk/usr/lib/libz.tbd", true)]
    [Arguments("Contents/Developer/Platforms/iPhoneSimulator.platform/Info.plist", true)]
    [Arguments("Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/lib/swift/iphonesimulator/libswiftCompatibility56.a", true)]
    [Arguments("Contents/Developer/Platforms/MacOSX.platform/Developer/SDKs/MacOSX.sdk/usr/lib/libz.tbd", false)]
    [Arguments("Contents/Developer/Platforms/iPhoneOS.platform/Library/Developer/CoreSimulator/x", false)]
    [Arguments("Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/clang", false)]
    [Arguments("Contents/MacOS/Xcode", false)]
    public async Task Allowlist(string path, bool wanted)
    {
        await Assert.That(XcodeImporter.Wanted(path)).IsEqualTo(wanted);
    }

    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
