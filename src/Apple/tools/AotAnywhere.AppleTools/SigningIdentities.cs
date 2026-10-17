using System.Security.Cryptography.X509Certificates;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Loads the signing identity from the .p12 named by <c>AOTANYWHERE_SIGN_P12</c> (password in the
/// file named by <c>AOTANYWHERE_SIGN_P12_PASSWORD_FILE</c>, so it never appears in a command line or
/// build log). Off-Mac there is no keychain; the identity the workload asks for (a SHA-1 thumbprint or
/// certificate name from DetectSigningIdentity) is checked against the .p12's certificate.
/// </summary>
public static class SigningIdentities
{
    public static SigningIdentity Load(string requested)
    {
        string p12 = Environment.GetEnvironmentVariable("AOTANYWHERE_SIGN_P12") is { Length: > 0 } p ? p
            : throw new AppleToolException("AotAnywhere: code signing needs a certificate: set AotAnywhereSignP12File (and AotAnywhereSignP12PasswordFile) to your Apple Development or Distribution certificate exported as .p12.");
        var identity = LoadP12(p12, Environment.GetEnvironmentVariable("AOTANYWHERE_SIGN_P12_PASSWORD_FILE"));
        if (!Matches(requested, identity.Certificate, identity.CommonName))
            Console.Error.WriteLine($"AotAnywhere: warning: the build asked for signing identity '{requested}', but the .p12 holds '{identity.CommonName}'; signing with the .p12's certificate.");
        return identity;
    }

    /// <summary>The certificate with a private key in <paramref name="p12"/>, plus any chain certificates in it.</summary>
    public static SigningIdentity LoadP12(string p12, string? passwordFile)
    {
        string password = passwordFile is { Length: > 0 } ? File.ReadAllText(passwordFile).TrimEnd('\r', '\n') : "";

        var collection = new X509Certificate2Collection();
        // macOS has no ephemeral key storage; elsewhere keep the key out of any persistent store.
        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        collection.Import(p12, password, flags);

        var cert = collection.FirstOrDefault(c => c.HasPrivateKey)
            ?? throw new AppleToolException($"AotAnywhere: '{p12}' contains no certificate with a private key.");
        var chain = new X509Certificate2Collection(collection.Where(c => !c.HasPrivateKey).ToArray());

        string cn = cert.GetNameInfo(X509NameType.SimpleName, false);
        string teamId = OrganizationalUnit(cert)
            ?? throw new AppleToolException($"AotAnywhere: the certificate '{cn}' has no team ID (subject OU); is it an Apple-issued signing certificate?");

        return new SigningIdentity(cert, chain, teamId, cn);
    }

    static bool Matches(string requested, X509Certificate2 cert, string cn) =>
        string.Equals(requested, cert.Thumbprint, StringComparison.OrdinalIgnoreCase)
        || cn.StartsWith(requested, StringComparison.Ordinal)
        || requested.StartsWith(cn, StringComparison.Ordinal);

    static string? OrganizationalUnit(X509Certificate2 cert)
    {
        foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
            if (rdn.GetSingleElementType().Value == "2.5.4.11")
                return rdn.GetSingleElementValue();
        return null;
    }
}
