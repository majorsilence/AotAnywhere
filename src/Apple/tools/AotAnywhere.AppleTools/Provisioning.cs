using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace AotAnywhere.AppleTools;

/// <summary>
/// A provisioning profile (<c>.mobileprovision</c>): a CMS-signed property list from Apple's
/// developer portal naming the team, the app IDs and devices it covers, the certificates allowed
/// to sign with it, and the entitlements it grants. Read here instead of the workload's
/// Keychain/profile-folder lookup (DetectSigningIdentity), with the same matching rules as the
/// MIT-licensed dotnet/macios (DetectSigningIdentity.ConstructValidAppId) and dotnet/macios-devtools
/// (MobileProvision.DistributionType).
/// </summary>
public sealed class Provisioning
{
    public required string Uuid { get; init; }
    public required string Name { get; init; }
    public required string TeamId { get; init; }
    public required string AppIdPrefix { get; init; }
    public required string ApplicationIdentifier { get; init; }
    public required DateTime Expiration { get; init; }
    public required IReadOnlyList<X509Certificate2> DeveloperCertificates { get; init; }
    public required IReadOnlyList<string> Platforms { get; init; }
    public required string DistributionType { get; init; }

    public static Provisioning Load(byte[] data)
    {
        byte[] content;
        try
        {
            var cms = new SignedCms();
            cms.Decode(data);
            content = cms.ContentInfo.Content;
        }
        catch (CryptographicException e)
        {
            throw new AppleToolException($"AotAnywhere: not a provisioning profile (no CMS envelope): {e.Message}");
        }

        var plist = Plist.ReadDictionary(content);
        string S(string key) => plist.TryGetValue(key, out var v) && v is string s ? s
            : throw new AppleToolException($"AotAnywhere: the provisioning profile has no {key}.");
        List<object> L(string key) => plist.TryGetValue(key, out var v) && v is List<object> l ? l : [];

        var entitlements = plist.TryGetValue("Entitlements", out var ent) && ent is Dictionary<string, object> d ? d : [];
        string prefix = L("ApplicationIdentifierPrefix").OfType<string>().FirstOrDefault()
            ?? throw new AppleToolException("AotAnywhere: the provisioning profile has no ApplicationIdentifierPrefix.");

        string type;
        if (plist.ContainsKey("ProvisionedDevices"))
            type = entitlements.TryGetValue("get-task-allow", out var gta) && gta is true ? "Development" : "AdHoc";
        else if (plist.TryGetValue("ProvisionsAllDevices", out var all) && all is true)
            type = "InHouse";
        else
            type = "AppStore";

        return new Provisioning
        {
            Uuid = S("UUID"),
            Name = S("Name"),
            TeamId = L("TeamIdentifier").OfType<string>().FirstOrDefault() ?? prefix,
            AppIdPrefix = prefix,
            ApplicationIdentifier = entitlements.TryGetValue("application-identifier", out var a) && a is string s ? s : "",
            Expiration = plist.TryGetValue("ExpirationDate", out var x) && x is DateTime dt ? dt : DateTime.MaxValue,
            DeveloperCertificates = L("DeveloperCertificates").OfType<byte[]>().Select(b => new X509Certificate2(b)).ToList(),
            Platforms = L("Platform").OfType<string>().ToList(),
            DistributionType = type,
        };
    }

    /// <summary>
    /// The app ID ("TEAMPREFIX.bundle.id") this profile grants for <paramref name="bundleId"/>:
    /// an exact match, or a trailing-wildcard one ("TEAMPREFIX.com.example.*"). Null if it doesn't cover it.
    /// </summary>
    public string? AppIdFor(string bundleId)
    {
        string appId = AppIdPrefix + "." + bundleId;
        if (bundleId.Contains('*') || ApplicationIdentifier.Length == 0)
            return null;
        if (ApplicationIdentifier == appId)
            return appId;
        int star = ApplicationIdentifier.IndexOf('*');
        return star >= 0 && star == ApplicationIdentifier.Length - 1 && appId.StartsWith(ApplicationIdentifier[..star], StringComparison.Ordinal)
            ? appId
            : null;
    }

    public bool Allows(X509Certificate2 certificate) =>
        DeveloperCertificates.Any(c => c.Thumbprint.Equals(certificate.Thumbprint, StringComparison.OrdinalIgnoreCase));
}
