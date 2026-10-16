using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// Device signing. Checked against Apple's codesign on a zig-linked iOS device executable: the
// signature is allocated at the same offset, and with the same entitlements the Requirements,
// Entitlements, DER and CMS blobs are identical and the CodeDirectory differs only in the
// identifier. A full device publish signed with a test identity and profile is "valid on disk"
// for `codesign --verify` (the designated requirement needs a real Apple-issued certificate).
public class DeviceSigningTests
{
    const uint LcSegment64 = 0x19, LcCodeSignature = 0x1d, LcBuildVersion = 0x32;

    // A minimal arm64 MH_EXECUTE with __TEXT (one section), __LINKEDIT ending the file, an
    // optional LC_BUILD_VERSION and an optional LC_CODE_SIGNATURE reservation.
    static byte[] Executable(uint? platform, bool reserveSignature, int codeSize = 40 * 1024, bool versionMinIPhoneOS = false)
    {
        var cmds = new List<byte[]>();
        var text = new byte[72 + 80];
        BinaryPrimitives.WriteUInt32LittleEndian(text, LcSegment64);
        BinaryPrimitives.WriteUInt32LittleEndian(text.AsSpan(4), (uint)text.Length);
        Encoding.ASCII.GetBytes("__TEXT").CopyTo(text, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(text.AsSpan(32), (ulong)codeSize);
        BinaryPrimitives.WriteUInt64LittleEndian(text.AsSpan(48), (ulong)codeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(text.AsSpan(64), 1);              // nsects
        Encoding.ASCII.GetBytes("__text").CopyTo(text, 72);
        BinaryPrimitives.WriteUInt32LittleEndian(text.AsSpan(72 + 48), 0x1000);    // section offset
        cmds.Add(text);

        var linkedit = new byte[72];
        BinaryPrimitives.WriteUInt32LittleEndian(linkedit, LcSegment64);
        BinaryPrimitives.WriteUInt32LittleEndian(linkedit.AsSpan(4), 72);
        Encoding.ASCII.GetBytes("__LINKEDIT").CopyTo(linkedit, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(linkedit.AsSpan(40), (ulong)codeSize);
        BinaryPrimitives.WriteUInt64LittleEndian(linkedit.AsSpan(48), 0x105); // not 16-byte aligned
        cmds.Add(linkedit);

        if (platform is uint p)
        {
            var bv = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(bv, LcBuildVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(bv.AsSpan(4), 24);
            BinaryPrimitives.WriteUInt32LittleEndian(bv.AsSpan(8), p);
            cmds.Add(bv);
        }
        if (versionMinIPhoneOS)
        {
            var vm = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(vm, 0x25); // LC_VERSION_MIN_IPHONEOS
            BinaryPrimitives.WriteUInt32LittleEndian(vm.AsSpan(4), 16);
            cmds.Add(vm);
        }
        if (reserveSignature)
        {
            var sig = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(sig, LcCodeSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(4), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(8), (uint)codeSize + 0x10);
            BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(12), 0xf5);
            cmds.Add(sig);
        }

        var data = new byte[codeSize + 0x105];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x0100000c);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)cmds.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), (uint)cmds.Sum(c => c.Length));
        int at = 32;
        foreach (var c in cmds)
        {
            c.CopyTo(data, at);
            at += c.Length;
        }
        for (int i = 0x1000; i < codeSize; i++)
            data[i] = (byte)(i * 13);
        return data;
    }

    static (uint Offset, uint Size, int Ncmds) Signature(byte[] d)
    {
        int ncmds = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16));
        int off = 32;
        for (int i = 0; i < ncmds; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off)) == LcCodeSignature)
                return (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 8)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 12)), ncmds);
            off += (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 4));
        }
        return (0, 0, ncmds);
    }

    static byte PageShift(byte[] signed)
    {
        var (offset, _, _) = Signature(signed);
        int cd = (int)BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan((int)offset + 16));
        return signed[offset + cd + 39];
    }

    [Test]
    public async Task AMissingSignatureIsAllocatedAtTheAlignedEndOfLinkedit()
    {
        var unsigned = Executable(platform: 2, reserveSignature: false);
        var allocated = MachoSigner.AllocateCodeSignature(unsigned);

        var (offset, size, ncmds) = Signature(allocated);
        await Assert.That(ncmds).IsEqualTo(4);
        await Assert.That(offset).IsEqualTo(40u * 1024 + 0x110);   // 0x105 rounded up to 16
        await Assert.That(size).IsEqualTo(0u);

        var signed = MachoSigner.Sign(unsigned, new SignOptions("com.example.app", null));
        await Assert.That(Signature(signed).Offset).IsEqualTo(offset);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan((int)offset))).IsEqualTo(0xfade0cc0u);
    }

    [Test]
    public async Task AReservedSignatureIsLeftWhereTheLinkerPutIt()
    {
        var reserved = Executable(platform: 2, reserveSignature: true);
        await Assert.That(MachoSigner.AllocateCodeSignature(reserved)).IsSameReferenceAs(reserved);
    }

    [Test]
    public async Task DeviceBinariesUse4KPagesAndSimulatorAndMacBinaries16K()
    {
        var options = new SignOptions("com.example.app", null);
        await Assert.That(PageShift(MachoSigner.Sign(Executable(2, true), options))).IsEqualTo((byte)12);   // iOS
        await Assert.That(PageShift(MachoSigner.Sign(Executable(7, true), options))).IsEqualTo((byte)14);   // iOS simulator
        await Assert.That(PageShift(MachoSigner.Sign(Executable(null, true), options))).IsEqualTo((byte)14); // macOS
        // Prebuilt frameworks often carry LC_VERSION_MIN_IPHONEOS instead (SkiaSharp's does).
        await Assert.That(PageShift(MachoSigner.Sign(Executable(null, true, versionMinIPhoneOS: true), options))).IsEqualTo((byte)12);
    }

    // --- provisioning profiles ---------------------------------------------------------

    static (X509Certificate2 Cert, byte[] Profile) MakeProfile(string applicationIdentifier, string extra = "")
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Apple Development: Test (ABCDE12345), OU=ABCDE12345, O=Test, C=US", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        string plist = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>ApplicationIdentifierPrefix</key><array><string>ABCDE12345</string></array>
              <key>DeveloperCertificates</key><array><data>{{Convert.ToBase64String(cert.RawData)}}</data></array>
              <key>Entitlements</key><dict>
                <key>application-identifier</key><string>{{applicationIdentifier}}</string>
                <key>get-task-allow</key><true/>
              </dict>
              <key>ExpirationDate</key><date>2099-01-01T00:00:00Z</date>
              <key>Name</key><string>Test Profile</string>
              <key>Platform</key><array><string>iOS</string></array>
              <key>TeamIdentifier</key><array><string>ABCDE12345</string></array>
              <key>UUID</key><string>11111111-2222-3333-4444-555555555555</string>
              {{extra}}
            </dict></plist>
            """;
        var cms = new SignedCms(new ContentInfo(Encoding.UTF8.GetBytes(plist)), detached: false);
        cms.ComputeSignature(new CmsSigner(cert)); // CreateSelfSigned's certificate carries the key
        return (new X509Certificate2(cert.RawData), cms.Encode());
    }

    [Test]
    public async Task ProfileFieldsAreRead()
    {
        var (cert, data) = MakeProfile("ABCDE12345.com.example.app", "<key>ProvisionedDevices</key><array><string>x</string></array>");
        var profile = Provisioning.Load(data);

        await Assert.That(profile.Uuid).IsEqualTo("11111111-2222-3333-4444-555555555555");
        await Assert.That(profile.TeamId).IsEqualTo("ABCDE12345");
        await Assert.That(profile.Platforms).IsEquivalentTo(new[] { "iOS" });
        await Assert.That(profile.DistributionType).IsEqualTo("Development");
        await Assert.That(profile.Allows(cert)).IsTrue();
        using var other = new CertificateRequest("CN=Other", RSA.Create(2048), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        await Assert.That(profile.Allows(other)).IsFalse();
    }

    [Test]
    public async Task AppIdsMatchExactlyOrByTrailingWildcard()
    {
        var exact = Provisioning.Load(MakeProfile("ABCDE12345.com.example.app").Profile);
        await Assert.That(exact.AppIdFor("com.example.app")).IsEqualTo("ABCDE12345.com.example.app");
        await Assert.That(exact.AppIdFor("com.example.other")).IsNull();

        var wildcard = Provisioning.Load(MakeProfile("ABCDE12345.com.example.*").Profile);
        await Assert.That(wildcard.AppIdFor("com.example.other")).IsEqualTo("ABCDE12345.com.example.other");
        await Assert.That(wildcard.AppIdFor("org.example.app")).IsNull();

        var any = Provisioning.Load(MakeProfile("ABCDE12345.*").Profile);
        await Assert.That(any.AppIdFor("org.example.app")).IsEqualTo("ABCDE12345.org.example.app");
    }

    [Test]
    public async Task DistributionTypeFollowsDevicesAndGetTaskAllow()
    {
        await Assert.That(Provisioning.Load(MakeProfile("ABCDE12345.*").Profile).DistributionType).IsEqualTo("AppStore");
        await Assert.That(Provisioning.Load(MakeProfile("ABCDE12345.*", "<key>ProvisionsAllDevices</key><true/>").Profile).DistributionType).IsEqualTo("InHouse");
    }

    [Test]
    public async Task NonProfilesAreRejected()
    {
        await Assert.That(() => Provisioning.Load("not a profile"u8.ToArray())).Throws<AppleToolException>();
    }
}
