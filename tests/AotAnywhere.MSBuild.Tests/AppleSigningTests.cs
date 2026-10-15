using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// The ad-hoc signer was checked against Apple's codesign on a real simulator bundle:
// identical signature layout, identical Requirements / Entitlements / DER / CMS
// blobs, CodeDirectory differing only in the CodeResources slot (whose input, the
// separately-signed dylibs, differs in signature padding), and
// `codesign --verify --deep --strict` accepts the result. These pin the format on a
// synthetic binary.
public class AppleSigningTests
{
    const uint LcSegment64 = 0x19, LcCodeSignature = 0x1d;

    // A minimal MH_EXECUTE: __TEXT (40 KiB of content), __LINKEDIT, and an
    // LC_CODE_SIGNATURE reserving space at the end, as zig/ld64 emit.
    static byte[] MakeExecutable(int codeSize = 40 * 1024)
    {
        var text = Segment("__TEXT", fileOff: 0, fileSize: (ulong)codeSize);
        var linkedit = Segment("__LINKEDIT", fileOff: (ulong)codeSize, fileSize: 0x100);
        var sig = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(sig, LcCodeSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(8), (uint)codeSize + 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(12), 0xf0);

        var cmds = new[] { text, linkedit, sig };
        var data = new byte[codeSize + 0x100];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 2);   // MH_EXECUTE
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)cmds.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), (uint)cmds.Sum(c => c.Length));
        int at = 32;
        foreach (var c in cmds)
        {
            c.CopyTo(data, at);
            at += c.Length;
        }
        for (int i = 0x400; i < codeSize; i++)
            data[i] = (byte)(i * 7);
        return data;
    }

    static byte[] Segment(string name, ulong fileOff, ulong fileSize)
    {
        var s = new byte[72];
        BinaryPrimitives.WriteUInt32LittleEndian(s, LcSegment64);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(4), 72);
        Encoding.ASCII.GetBytes(name).CopyTo(s, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(32), fileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(40), fileOff);
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(48), fileSize);
        return s;
    }

    static Dictionary<uint, byte[]> Blobs(byte[] signed, out uint codeLimit)
    {
        codeLimit = 40 * 1024 + 0x10;
        int sb = (int)codeLimit;
        if (BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan(sb)) != 0xfade0cc0)
            throw new InvalidOperationException("no superblob");
        uint count = BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan(sb + 8));
        var result = new Dictionary<uint, byte[]>();
        for (int i = 0; i < count; i++)
        {
            uint type = BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan(sb + 12 + 8 * i));
            int off = (int)BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan(sb + 16 + 8 * i));
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan(sb + off + 4));
            result[type] = signed.AsSpan(sb + off, len).ToArray();
        }
        return result;
    }

    static readonly byte[] EmptyEntitlements = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict/>\n</plist>\n");

    [Test]
    public async Task AdHocSignatureHasCodesignsLayout()
    {
        var signed = MachoSigner.Sign(MakeExecutable(), new SignOptions("com.example.app", EntitlementsXml: EmptyEntitlements, InfoPlist: [1, 2, 3], CodeResources: [4, 5]));
        var blobs = Blobs(signed, out uint codeLimit);

        // CodeDirectory, Requirements, Entitlements, DER entitlements, (empty) CMS wrapper.
        await Assert.That(blobs.Keys.ToArray()).IsEquivalentTo(new uint[] { 0, 2, 5, 7, 0x10000 });
        await Assert.That(blobs[2]).IsEquivalentTo(new byte[] { 0xfa, 0xde, 0x0c, 0x01, 0, 0, 0, 12, 0, 0, 0, 0 });
        await Assert.That(blobs[0x10000]).IsEquivalentTo(new byte[] { 0xfa, 0xde, 0x0b, 0x01, 0, 0, 0, 8 });
        await Assert.That(blobs[5].AsSpan(8).ToArray()).IsEquivalentTo(EmptyEntitlements);
        await Assert.That(blobs[7].AsSpan(8).ToArray()).IsEquivalentTo(new byte[] { 0x70, 0x05, 0x02, 0x01, 0x01, 0xb0, 0x00 });

        var cd = blobs[0];
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(8))).IsEqualTo(0x20400u);       // version
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(12))).IsEqualTo(0x2u);          // adhoc
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(24))).IsEqualTo(7u);            // special slots
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(28))).IsEqualTo(3u);            // 40 KiB + 16 -> 3 x 16 KiB pages
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(32))).IsEqualTo(codeLimit);
        await Assert.That(cd[39]).IsEqualTo((byte)14);                                                    // 16 KiB pages
        await Assert.That(BinaryPrimitives.ReadUInt64BigEndian(cd.AsSpan(72))).IsEqualTo(40UL * 1024);   // execSegLimit = __TEXT filesize
        await Assert.That(BinaryPrimitives.ReadUInt64BigEndian(cd.AsSpan(80))).IsEqualTo(1UL);           // main binary

        // Page hashes cover the signed file (whose header now records the final signature size).
        int hashOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(16));
        await Assert.That(cd.AsSpan(hashOffset, 32).ToArray()).IsEquivalentTo(SHA256.HashData(signed.AsSpan(0, 16384)));
        await Assert.That(cd.AsSpan(hashOffset + 64, 32).ToArray()).IsEquivalentTo(SHA256.HashData(signed.AsSpan(32768, (int)codeLimit - 32768)));

        // Special slots: Info.plist (1), CodeResources (3), entitlements (5).
        await Assert.That(cd.AsSpan(hashOffset - 32, 32).ToArray()).IsEquivalentTo(SHA256.HashData(new byte[] { 1, 2, 3 }));
        await Assert.That(cd.AsSpan(hashOffset - 96, 32).ToArray()).IsEquivalentTo(SHA256.HashData(new byte[] { 4, 5 }));
        await Assert.That(cd.AsSpan(hashOffset - 160, 32).ToArray()).IsEquivalentTo(SHA256.HashData(blobs[5]));
        await Assert.That(cd.AsSpan(hashOffset - 128, 32).ToArray().All(b => b == 0)).IsTrue();          // slot 4 unused
    }

    [Test]
    public async Task SignatureSizeFollowsCodesignsReservation()
    {
        var signed = MachoSigner.Sign(MakeExecutable(), new SignOptions("lib"));
        uint datasize = BinaryPrimitives.ReadUInt32LittleEndian(signed.AsSpan(32 + 72 + 72 + 12));
        var blobs = Blobs(signed, out uint codeLimit);
        int superBlobLength = (int)BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan((int)codeLimit + 4));

        await Assert.That(signed.Length).IsEqualTo((int)(codeLimit + datasize));
        await Assert.That((int)datasize).IsEqualTo((superBlobLength + 18000 + 15) / 16 * 16);
        // Without entitlements or a bundle only the requirements slot is special.
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(blobs[0].AsSpan(24))).IsEqualTo(2u);
    }

    [Test]
    public async Task GetTaskAllowMarksTheExecSegmentAllowUnsigned()
    {
        var ents = Encoding.UTF8.GetBytes("<plist version=\"1.0\"><dict><key>get-task-allow</key><true/></dict></plist>");
        var cd = Blobs(MachoSigner.Sign(MakeExecutable(), new SignOptions("a", EntitlementsXml: ents)), out _)[0];

        await Assert.That(BinaryPrimitives.ReadUInt64BigEndian(cd.AsSpan(80))).IsEqualTo(0x11UL);
    }

    [Test]
    public async Task IdentitySignatureCarriesAVerifiableCmsOverTheCodeDirectory()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Apple Development: Test (ABCDE12345), OU=TEAM123456, O=Test, C=US", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var identity = new SigningIdentity(cert, [], "TEAM123456", "Apple Development: Test (ABCDE12345)");

        var blobs = Blobs(MachoSigner.Sign(MakeExecutable(), new SignOptions("com.example.app", identity)), out _);
        var cd = blobs[0];

        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(12))).IsEqualTo(0u);   // not ad-hoc
        int teamOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(48));
        await Assert.That(Encoding.ASCII.GetString(cd, teamOffset, 10)).IsEqualTo("TEAM123456");

        var cms = new SignedCms(new ContentInfo(cd), detached: true);
        cms.Decode(blobs[0x10000].AsSpan(8).ToArray());
        cms.CheckSignature(verifySignatureOnly: true);
        var attrs = cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).ToList();
        await Assert.That(attrs).Contains("1.2.840.113635.100.9.1");
        await Assert.That(attrs).Contains("1.2.840.113635.100.9.2");

        // A designated requirement naming the identifier and the leaf CN.
        var reqText = Encoding.UTF8.GetString(blobs[2]);
        await Assert.That(reqText).Contains("com.example.app");
        await Assert.That(reqText).Contains("Apple Development: Test (ABCDE12345)");
    }

    [Test]
    public async Task BinaryWithoutCodeSignatureCommandIsRejected()
    {
        var data = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xfeedfacf);

        await Assert.That(() => MachoSigner.Sign(data, new SignOptions("x"))).Throws<AppleToolException>();
    }
}

public class EntitlementsDerTests
{
    [Test]
    public async Task EmptyDictionaryMatchesCodesign()
    {
        // From an Apple-signed simulator app: `codesign -d --entitlements - --der`.
        await Assert.That(EntitlementsDer.Encode([])).IsEquivalentTo(new byte[] { 0x70, 0x05, 0x02, 0x01, 0x01, 0xb0, 0x00 });
    }

    [Test]
    public async Task MixedEntitlementsMatchCodesign()
    {
        // `codesign --sign - --entitlements <this> --generate-entitlement-der` on Xcode 26.6:
        // bytewise key order, long-form lengths, minimal two's-complement integers.
        var ents = Plist.ReadDictionary(Encoding.UTF8.GetBytes("""
            <plist version="1.0"><dict><key>b</key><true/><key>a</key><array><string>x</string></array><key>c</key><integer>1</integer>
            <key>application-identifier</key><string>TEAM.com.x</string><key>keychain-access-groups</key><array><string>TEAM.*</string></array>
            <key>get-task-allow</key><false/><key>big</key><integer>300</integer><key>neg</key><integer>-2</integer></dict></plist>
            """));

        await Assert.That(EntitlementsDer.Encode(ents)).IsEquivalentTo(Convert.FromHexString(
            "708194020101b0818e30080c016130030c017830240c166170706c69636174696f6e2d6964656e7469666965720c0a5445414d2e636f6d2e78" +
            "30060c01620101ff30090c036269670202012c30060c016302010130130c0e6765742d7461736b2d616c6c6f7701010030220c166b6579636861" +
            "696e2d6163636573732d67726f75707330080c065445414d2e2a30080c036e65670201fe"));
    }
}

public class PlistTests
{
    // python3 plistlib.dumps({...}, fmt=FMT_BINARY)
    const string BinaryFixture =
        "62706c6973743030d70102030405060708090a0b0c0f105342696754426c6f625f1012434642756e646c6545786563757461626c655f1012434642756e646c654964656e746966696572544c697374514e524f6b5f10147878787878787878787878787878787878787878420102534170705f100f636f6d2e6578616d706c652e617070a20d0e51615162102a0908171b20354a4f51546b6e728487898b8d000000000000010100000000000000110000000000000000000000000000008e";

    [Test]
    public async Task ReadsBinaryPlists()
    {
        var d = Plist.ReadDictionary(Convert.FromHexString(BinaryFixture));

        await Assert.That(d["CFBundleIdentifier"]).IsEqualTo("com.example.app");
        await Assert.That(d["CFBundleExecutable"]).IsEqualTo("App");
        await Assert.That(d["N"]).IsEqualTo(42L);
        await Assert.That(d["Ok"]).IsEqualTo(true);
        await Assert.That((List<object>)d["List"]).IsEquivalentTo(new object[] { "a", "b" });
        await Assert.That((byte[])d["Blob"]).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(d["Big"]).IsEqualTo(new string('x', 20));   // length >= 15 uses an extended count
    }

    [Test]
    public async Task XmlRoundTrips()
    {
        var value = new Dictionary<string, object>
        {
            ["s"] = "a<b&c",
            ["i"] = 7L,
            ["t"] = true,
            ["d"] = new byte[] { 9, 8 },
            ["l"] = new List<object> { "x", new Dictionary<string, object>() },
        };

        var back = Plist.ReadDictionary(Plist.WriteXml(value));

        await Assert.That(back["s"]).IsEqualTo("a<b&c");
        await Assert.That(back["i"]).IsEqualTo(7L);
        await Assert.That(back["t"]).IsEqualTo(true);
        await Assert.That((byte[])back["d"]).IsEquivalentTo(new byte[] { 9, 8 });
        await Assert.That(((List<object>)back["l"]).Count).IsEqualTo(2);
    }
}
