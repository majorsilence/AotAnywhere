using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AotAnywhere.AppleTools;

/// <summary>A code-signing certificate (with private key), its chain and the Apple team it belongs to.</summary>
public sealed record SigningIdentity(X509Certificate2 Certificate, X509Certificate2Collection Chain, string TeamId, string CommonName);

/// <param name="Identifier">The code identifier: the bundle ID for an app's executable, the file name (minus extension) for a loose dylib.</param>
/// <param name="Identity">Null for an ad-hoc signature (simulator builds).</param>
/// <param name="EntitlementsXml">The entitlements plist, embedded verbatim (slot 5); its DER form goes in slot 7.</param>
/// <param name="InfoPlist">The bundle's Info.plist bytes (slot 1), for a bundle's main executable.</param>
/// <param name="CodeResources">The bundle's _CodeSignature/CodeResources bytes (slot 3), for a bundle's main executable.</param>
public sealed record SignOptions(
    string Identifier,
    SigningIdentity? Identity = null,
    byte[]? EntitlementsXml = null,
    byte[]? InfoPlist = null,
    byte[]? CodeResources = null);

/// <summary>
/// Embeds a code signature into a thin 64-bit Mach-O, laid out as codesign does for iOS:
/// SuperBlob { CodeDirectory (v0x20400, SHA-256, 16 KiB pages), Requirements, Entitlements,
/// DER Entitlements, CMS } at LC_CODE_SIGNATURE's data offset (reserved by the linker, or added here).
/// </summary>
public static class MachoSigner
{
    const uint MhMagic64 = 0xfeedfacf;
    const uint MhExecute = 0x2;
    const uint LcSegment64 = 0x19;
    const uint LcCodeSignature = 0x1d;

    const uint CsMagicEmbeddedSignature = 0xfade0cc0;
    const uint CsMagicCodeDirectory = 0xfade0c02;
    const uint CsMagicRequirements = 0xfade0c01;
    const uint CsMagicRequirement = 0xfade0c00;
    const uint CsMagicEmbeddedEntitlements = 0xfade7171;
    const uint CsMagicEmbeddedDerEntitlements = 0xfade7172;
    const uint CsMagicBlobWrapper = 0xfade0b01;

    const uint SlotCodeDirectory = 0, SlotInfo = 1, SlotRequirements = 2, SlotResources = 3,
        SlotEntitlements = 5, SlotDerEntitlements = 7, SlotSignature = 0x10000;

    const uint CsAdhoc = 0x2;
    const ulong CsExecSegMainBinary = 0x1, CsExecSegAllowUnsigned = 0x10;

    // Code page size, as codesign picks it: 16 KiB for arm64 macOS and simulator binaries, 4 KiB
    // for iOS/tvOS/watchOS/visionOS device binaries (and x86_64).
    const int PageShift16K = 14, PageShift4K = 12;
    const uint LcBuildVersion = 0x32, LcVersionMinIPhoneOS = 0x25, LcVersionMinTvOS = 0x2f, LcVersionMinWatchOS = 0x30, CpuTypeArm64 = 0x0100000c;
    const int CodeDirectoryHeaderSize = 88;   // through execSegFlags (version 0x20400)
    const int HashSize = 32;
    const byte HashTypeSha256 = 2;
    const ulong LinkeditVmAlign = 0x4000;

    // codesign's signature reservation: the signature data is padded with this much
    // room (for the CMS blob it cannot size before hashing the header that records it).
    const int SignatureReserve = 18000;

    public static byte[] Sign(byte[] macho, SignOptions options)
    {
        macho = AllocateCodeSignature(macho);
        var layout = Layout.Parse(macho);

        var requirements = RequirementsBlob(options);
        var entitlements = options.EntitlementsXml is null ? null : Blob(CsMagicEmbeddedEntitlements, options.EntitlementsXml);
        Dictionary<string, object>? entitlementsDict = options.EntitlementsXml is null ? null : Plist.ReadDictionary(options.EntitlementsXml);
        var derEntitlements = entitlementsDict is null ? null : Blob(CsMagicEmbeddedDerEntitlements, EntitlementsDer.Encode(entitlementsDict));

        // Special slots, by index; absent slots below the highest present one hash as zeros.
        var special = new SortedDictionary<uint, byte[]> { [SlotRequirements] = SHA256.HashData(requirements) };
        if (options.InfoPlist is not null)
            special[SlotInfo] = SHA256.HashData(options.InfoPlist);
        if (options.CodeResources is not null)
            special[SlotResources] = SHA256.HashData(options.CodeResources);
        if (entitlements is not null)
            special[SlotEntitlements] = SHA256.HashData(entitlements);
        if (derEntitlements is not null)
            special[SlotDerEntitlements] = SHA256.HashData(derEntitlements);
        int nSpecialSlots = (int)special.Keys.Max();

        uint codeLimit = layout.SignatureOffset;
        int pageShift = layout.PageShift;
        int nCodeSlots = (int)((codeLimit + (1u << pageShift) - 1) >> pageShift);
        string? teamId = options.Identity?.TeamId;
        int cdSize = CodeDirectoryHeaderSize + Encoding.UTF8.GetByteCount(options.Identifier) + 1
            + (teamId is null ? 0 : Encoding.UTF8.GetByteCount(teamId) + 1)
            + (nSpecialSlots + nCodeSlots) * HashSize;

        var blobTypes = new List<uint> { SlotCodeDirectory, SlotRequirements };
        int nonCmsSize = cdSize + requirements.Length;
        if (entitlements is not null)
        {
            blobTypes.Add(SlotEntitlements);
            nonCmsSize += entitlements.Length;
        }
        if (derEntitlements is not null)
        {
            blobTypes.Add(SlotDerEntitlements);
            nonCmsSize += derEntitlements.Length;
        }
        blobTypes.Add(SlotSignature);
        int superBlobHeader = 12 + 8 * blobTypes.Count;
        uint dataSize = Align((uint)(superBlobHeader + nonCmsSize + 8 + SignatureReserve), 16);

        // Fix the layout first (it is part of the hashed header), then hash.
        var output = new byte[codeLimit + dataSize];
        macho.AsSpan(0, (int)codeLimit).CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(layout.CodeSignatureCommand + 12), dataSize);
        ulong linkeditFileSize = codeLimit + dataSize - layout.LinkeditFileOffset;
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(layout.LinkeditCommand + 48), linkeditFileSize);   // filesize
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(layout.LinkeditCommand + 32), Align(linkeditFileSize, LinkeditVmAlign));   // vmsize

        ulong execSegFlags = 0;
        if (layout.FileType == MhExecute)
            execSegFlags |= CsExecSegMainBinary;
        if (entitlementsDict is not null && entitlementsDict.TryGetValue("get-task-allow", out var gta) && gta is true)
            execSegFlags |= CsExecSegAllowUnsigned;

        var cd = CodeDirectory(output.AsSpan(0, (int)codeLimit), pageShift, options.Identifier, teamId, special, nSpecialSlots, nCodeSlots,
            flags: options.Identity is null ? CsAdhoc : 0, layout.TextFileSize, execSegFlags);

        var cms = options.Identity is null ? Blob(CsMagicBlobWrapper, []) : Blob(CsMagicBlobWrapper, Cms(cd, options.Identity));

        var blobs = new List<(uint Type, byte[] Data)> { (SlotCodeDirectory, cd), (SlotRequirements, requirements) };
        if (entitlements is not null)
            blobs.Add((SlotEntitlements, entitlements));
        if (derEntitlements is not null)
            blobs.Add((SlotDerEntitlements, derEntitlements));
        blobs.Add((SlotSignature, cms));

        var superBlob = SuperBlob(blobs);
        if (superBlob.Length > dataSize)
            throw new AppleToolException($"AotAnywhere: the code signature ({superBlob.Length} bytes) does not fit the reserved space ({dataSize} bytes).");
        superBlob.CopyTo(output, codeLimit);
        return output;
    }

    /// <summary>The CDHash: the first 20 bytes of the SHA-256 of the CodeDirectory blob.</summary>
    public static byte[] CdHash(byte[] codeDirectory) => SHA256.HashData(codeDirectory)[..20];

    static byte[] CodeDirectory(ReadOnlySpan<byte> code, int pageShift, string identifier, string? teamId, SortedDictionary<uint, byte[]> special,
        int nSpecialSlots, int nCodeSlots, uint flags, ulong execSegLimit, ulong execSegFlags)
    {
        var ident = Encoding.UTF8.GetBytes(identifier);
        var team = teamId is null ? null : Encoding.UTF8.GetBytes(teamId);
        int identOffset = CodeDirectoryHeaderSize;
        int teamOffset = team is null ? 0 : identOffset + ident.Length + 1;
        int hashOffset = (team is null ? identOffset + ident.Length + 1 : teamOffset + team.Length + 1) + nSpecialSlots * HashSize;
        int length = hashOffset + nCodeSlots * HashSize;

        var cd = new byte[length];
        var s = cd.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, CsMagicCodeDirectory);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], 0x20400);
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], flags);
        BinaryPrimitives.WriteUInt32BigEndian(s[16..], (uint)hashOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s[20..], (uint)identOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s[24..], (uint)nSpecialSlots);
        BinaryPrimitives.WriteUInt32BigEndian(s[28..], (uint)nCodeSlots);
        BinaryPrimitives.WriteUInt32BigEndian(s[32..], (uint)code.Length);   // codeLimit
        s[36] = HashSize;
        s[37] = HashTypeSha256;
        s[38] = 0;                                                            // platform
        s[39] = (byte)pageShift;
        // spare2 (40), scatterOffset (44)
        BinaryPrimitives.WriteUInt32BigEndian(s[48..], (uint)teamOffset);
        // spare3 (52), codeLimit64 (56), execSegBase (64)
        BinaryPrimitives.WriteUInt64BigEndian(s[72..], execSegLimit);
        BinaryPrimitives.WriteUInt64BigEndian(s[80..], execSegFlags);

        ident.CopyTo(cd, identOffset);
        team?.CopyTo(cd, teamOffset);

        // Special slot n lives n hashes before hashOffset.
        foreach (var (slot, hash) in special)
            hash.CopyTo(cd, hashOffset - (int)slot * HashSize);

        int page = 1 << pageShift;
        for (int i = 0; i < nCodeSlots; i++)
        {
            int start = i * page;
            SHA256.HashData(code.Slice(start, Math.Min(page, code.Length - start)), s.Slice(hashOffset + i * HashSize, HashSize));
        }
        return cd;
    }

    // Ad-hoc: an empty requirement set. Signed: the designated requirement codesign
    // derives for an Apple-issued (development or distribution) certificate:
    //   identifier "<id>" and anchor apple generic and certificate leaf[subject.CN] = "<cn>"
    //   and certificate 1[field.1.2.840.113635.100.6.2.1] /* exists */
    static byte[] RequirementsBlob(SignOptions options)
    {
        if (options.Identity is null)
            return Blob(CsMagicRequirements, [0, 0, 0, 0]);

        var expr = new List<byte>();
        void Op(uint op) => expr.AddRange(BigEndian(op));
        void Data(byte[] d)
        {
            expr.AddRange(BigEndian((uint)d.Length));
            expr.AddRange(d);
            expr.AddRange(new byte[(4 - d.Length % 4) % 4]);
        }

        const uint opIdent = 2, opAnd = 6, opCertField = 11, opCertGeneric = 14, opAppleGenericAnchor = 15;
        const uint matchExists = 0, matchEqual = 1;
        Op(opAnd);
        Op(opIdent); Data(Encoding.UTF8.GetBytes(options.Identifier));
        Op(opAnd);
        Op(opAppleGenericAnchor);
        Op(opAnd);
        Op(opCertField); Op(0); Data(Encoding.UTF8.GetBytes("subject.CN")); Op(matchEqual); Data(Encoding.UTF8.GetBytes(options.Identity.CommonName));
        Op(opCertGeneric); Op(1); Data(OidContents("1.2.840.113635.100.6.2.1")); Op(matchExists);

        // Requirement blob: magic, length, kind (1 = expression), expression.
        var requirement = Blob(CsMagicRequirement, [.. BigEndian(1), .. expr]);

        // Requirement set: count 1, { type 3 (designated), offset 20 }, then the requirement.
        return Blob(CsMagicRequirements, [.. BigEndian(1), .. BigEndian(3), .. BigEndian(20), .. requirement]);
    }

    static byte[] Cms(byte[] codeDirectory, SigningIdentity identity)
    {
        var cdHashFull = SHA256.HashData(codeDirectory);

        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, identity.Certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.None,
        };
        signer.Certificates.AddRange(identity.Chain);
        if (!signer.Certificates.Contains(identity.Certificate))
            signer.Certificates.Insert(0, identity.Certificate);
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));

        // Apple hash agility v1: a plist { cdhashes = [ <truncated cdhash> ] }.
        var agility = Plist.WriteXml(new Dictionary<string, object> { ["cdhashes"] = new List<object> { cdHashFull[..20] } });
        var v1 = new AsnWriter(AsnEncodingRules.DER);
        v1.WriteOctetString(agility);
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113635.100.9.1"), v1.Encode()));

        // Apple hash agility v2: SEQUENCE { SEQUENCE { digestAlgorithm OID, OCTET STRING full cdhash } }.
        var v2 = new AsnWriter(AsnEncodingRules.DER);
        using (v2.PushSequence())
        {
            v2.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
            v2.WriteOctetString(cdHashFull);
        }
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113635.100.9.2"), v2.Encode()));

        var cms = new SignedCms(new ContentInfo(codeDirectory), detached: true);
        cms.ComputeSignature(signer, silent: true);
        return cms.Encode();
    }

    static byte[] SuperBlob(List<(uint Type, byte[] Data)> blobs)
    {
        int header = 12 + 8 * blobs.Count;
        int length = header + blobs.Sum(b => b.Data.Length);
        var result = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(result, CsMagicEmbeddedSignature);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)blobs.Count);
        int offset = header;
        for (int i = 0; i < blobs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12 + 8 * i), blobs[i].Type);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16 + 8 * i), (uint)offset);
            blobs[i].Data.CopyTo(result, offset);
            offset += blobs[i].Data.Length;
        }
        return result;
    }

    static byte[] Blob(uint magic, byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, magic);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)b.Length);
        payload.CopyTo(b, 8);
        return b;
    }

    static byte[] OidContents(string oid)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        w.WriteObjectIdentifier(oid);
        var encoded = w.Encode();
        return encoded[2..];   // drop tag and (short-form) length
    }

    static byte[] BigEndian(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    /// <summary>
    /// What codesign_allocate does for a binary linked without a signature reservation (zig
    /// reserves one for macOS and the simulator but not for iOS devices): add an empty
    /// LC_CODE_SIGNATURE in the header padding, pointing at the 16-byte-aligned end of the file,
    /// which must be the end of __LINKEDIT. <see cref="Sign"/> then sizes it.
    /// </summary>
    public static byte[] AllocateCodeSignature(byte[] d)
    {
        if (d.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(d) != MhMagic64)
            throw new AppleToolException("AotAnywhere: codesign only supports thin 64-bit Mach-O files.");
        uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16));
        uint sizeofcmds = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(20));

        int linkedit = -1;
        ulong firstSection = ulong.MaxValue;
        int off = 32;
        for (uint i = 0; i < ncmds; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 4));
            if (cmd == LcCodeSignature)
                return d;
            if (cmd == LcSegment64)
            {
                if (Encoding.ASCII.GetString(d, off + 8, 16).TrimEnd('\0') == "__LINKEDIT")
                    linkedit = off;
                uint nsects = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 64));
                for (int s = 0; s < nsects; s++)
                {
                    uint sectOffset = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 72 + 80 * s + 48));
                    if (sectOffset != 0)
                        firstSection = Math.Min(firstSection, sectOffset);
                }
            }
            off += (int)size;
        }

        if (linkedit < 0)
            throw new AppleToolException("AotAnywhere: the binary has no __LINKEDIT segment.");
        ulong linkeditOff = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(linkedit + 40));
        ulong linkeditSize = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(linkedit + 48));
        if (linkeditOff + linkeditSize != (ulong)d.Length)
            throw new AppleToolException("AotAnywhere: cannot add a code signature: __LINKEDIT does not end the file.");
        int commandsEnd = 32 + (int)sizeofcmds;
        if (firstSection < (ulong)commandsEnd + 16 || d.AsSpan(commandsEnd, 16).IndexOfAnyExcept((byte)0) >= 0)
            throw new AppleToolException("AotAnywhere: cannot add a code signature: no room after the load commands (link with -headerpad_max_install_names).");

        uint dataOff = Align((uint)d.Length, 16);
        var output = new byte[dataOff];
        d.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(commandsEnd), LcCodeSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(commandsEnd + 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(commandsEnd + 8), dataOff);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), ncmds + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(20), sizeofcmds + 16);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(linkedit + 48), dataOff - linkeditOff);
        return output;
    }

    static uint Align(uint v, uint a) => (v + a - 1) / a * a;
    static ulong Align(ulong v, ulong a) => (v + a - 1) / a * a;

    sealed record Layout(uint FileType, int CodeSignatureCommand, uint SignatureOffset, int LinkeditCommand, ulong LinkeditFileOffset, ulong TextFileSize, int PageShift)
    {
        public static Layout Parse(byte[] d)
        {
            if (d.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(d) != MhMagic64)
                throw new AppleToolException("AotAnywhere: codesign only supports thin 64-bit Mach-O files.");
            uint cpuType = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
            uint fileType = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12));
            uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16));

            uint platform = 1; // macOS, if no LC_BUILD_VERSION
            int sigCmd = -1, linkedit = -1;
            uint sigOffset = 0;
            ulong linkeditFileOff = 0, textFileSize = 0;
            int off = 32;
            for (uint i = 0; i < ncmds; i++)
            {
                uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 4));
                if (cmd == LcSegment64)
                {
                    string name = Encoding.ASCII.GetString(d, off + 8, 16).TrimEnd('\0');
                    if (name == "__TEXT")
                        textFileSize = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(off + 48));
                    if (name == "__LINKEDIT")
                    {
                        linkedit = off;
                        linkeditFileOff = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(off + 40));
                    }
                }
                else if (cmd == LcBuildVersion)
                {
                    platform = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 8));
                }
                // Older binaries (e.g. prebuilt frameworks) carry LC_VERSION_MIN_* instead.
                else if (cmd is LcVersionMinIPhoneOS or LcVersionMinTvOS or LcVersionMinWatchOS)
                {
                    platform = cmd == LcVersionMinIPhoneOS ? 2u : cmd == LcVersionMinTvOS ? 3u : 4u;
                }
                else if (cmd == LcCodeSignature)
                {
                    sigCmd = off;
                    sigOffset = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 8));
                }
                off += (int)size;
            }

            if (sigCmd < 0)
                throw new AppleToolException("AotAnywhere: the binary has no LC_CODE_SIGNATURE load command.");
            if (linkedit < 0)
                throw new AppleToolException("AotAnywhere: the binary has no __LINKEDIT segment.");
            if (sigOffset > d.Length || sigOffset < linkeditFileOff)
                throw new AppleToolException("AotAnywhere: LC_CODE_SIGNATURE points outside __LINKEDIT.");
            // Device platforms: iOS 2, tvOS 3, watchOS 4, bridgeOS 5, visionOS 11.
            bool device = platform is 2 or 3 or 4 or 5 or 11;
            int pageShift = cpuType == CpuTypeArm64 && !device ? PageShift16K : PageShift4K;
            return new Layout(fileType, sigCmd, sigOffset, linkedit, linkeditFileOff, textFileSize, pageShift);
        }
    }
}
