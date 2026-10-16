using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Signs an iOS bundle (.app / .appex) the way codesign does: seal every resource into
/// <c>_CodeSignature/CodeResources</c>, then sign the main executable with the bundle ID as its
/// identifier and the Info.plist and CodeResources hashes in its special slots. Nested code
/// (the loose dylibs and frameworks the workload bundles) is signed beforehand by the workload's own per-file
/// codesign calls and is sealed here as a plain resource, as codesign does for iOS bundles.
/// </summary>
public static class BundleSigner
{
    // codesign's default iOS resource rules (version 2), as written into CodeResources.
    static readonly (string Pattern, bool Omit, bool Optional, double Weight)[] Rules =
    [
        ("^.*", false, false, 0),
        ("^.*\\.lproj/", false, true, 1000),
        ("^.*\\.lproj/locversion.plist$", true, false, 1100),
        ("^Base\\.lproj/", false, false, 1010),
        ("^version.plist$", false, false, 0),
    ];

    static readonly (string Pattern, bool Omit, bool Optional, double Weight)[] Rules2 =
    [
        (".*\\.dSYM($|/)", false, false, 11),
        ("^(.*/)?\\.DS_Store$", true, false, 2000),
        ("^.*", false, false, 0),
        ("^.*\\.lproj/", false, true, 1000),
        ("^.*\\.lproj/locversion.plist$", true, false, 1100),
        ("^Base\\.lproj/", false, false, 1010),
        ("^Info\\.plist$", true, false, 20),
        ("^PkgInfo$", true, false, 20),
        ("^embedded\\.provisionprofile$", false, false, 20),
        ("^version\\.plist$", false, false, 20),
    ];

    public static void Sign(string bundleDir, SigningIdentity? identity, byte[]? entitlementsXml)
    {
        string infoPath = Path.Combine(bundleDir, "Info.plist");
        if (!File.Exists(infoPath))
            throw new AppleToolException($"AotAnywhere: '{bundleDir}' has no Info.plist.");
        byte[] info = File.ReadAllBytes(infoPath);
        var infoDict = Plist.ReadDictionary(info);
        string executable = infoDict.TryGetValue("CFBundleExecutable", out var e) && e is string es ? es
            : throw new AppleToolException("AotAnywhere: Info.plist has no CFBundleExecutable.");
        string identifier = infoDict.TryGetValue("CFBundleIdentifier", out var id) && id is string ids ? ids
            : throw new AppleToolException("AotAnywhere: Info.plist has no CFBundleIdentifier.");

        string sigDir = Path.Combine(bundleDir, "_CodeSignature");
        Directory.CreateDirectory(sigDir);
        byte[] codeResources = CodeResources(bundleDir, executable);
        File.WriteAllBytes(Path.Combine(sigDir, "CodeResources"), codeResources);

        string exePath = Path.Combine(bundleDir, executable);
        var signed = MachoSigner.Sign(File.ReadAllBytes(exePath), new SignOptions(identifier, identity, entitlementsXml, info, codeResources));
        File.WriteAllBytes(exePath, signed);
    }

    public static byte[] CodeResources(string bundleDir, string executable)
    {
        var files = new Dictionary<string, object>(StringComparer.Ordinal);
        var files2 = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var full in Directory.EnumerateFiles(bundleDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(bundleDir, full).Replace('\\', '/');
            if (rel == executable || rel.StartsWith("_CodeSignature/", StringComparison.Ordinal))
                continue;
            if (new FileInfo(full).LinkTarget is not null)
                throw new AppleToolException($"AotAnywhere: symbolic links in bundles are not supported yet ('{rel}').");
            // Nested frameworks (signed beforehand, by the workload's own per-framework codesign
            // calls) are sealed file by file as resources, as codesign does for iOS bundles.
            if (rel.Split('/').SkipLast(1).Any(d => d.EndsWith(".appex", StringComparison.Ordinal)))
                throw new AppleToolException($"AotAnywhere: bundles with app extensions are not supported off-Mac yet ('{rel}').");

            byte[] content = File.ReadAllBytes(full);

            var rule = Match(Rules, rel);
            if (!rule.Omit)
            {
                byte[] sha1 = SHA1.HashData(content);
                files[rel] = rule.Optional
                    ? new Dictionary<string, object>(StringComparer.Ordinal) { ["hash"] = sha1, ["optional"] = true }
                    : sha1;
            }

            var rule2 = Match(Rules2, rel);
            if (!rule2.Omit)
            {
                var entry = new Dictionary<string, object>(StringComparer.Ordinal) { ["hash2"] = SHA256.HashData(content) };
                if (rule2.Optional)
                    entry["optional"] = true;
                files2[rel] = entry;
            }
        }

        var root = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["files"] = files,
            ["files2"] = files2,
            ["rules"] = RulesDict(Rules),
            ["rules2"] = RulesDict(Rules2),
        };
        return Plist.WriteXml(root);
    }

    // The highest-weight matching rule wins (ties: first listed); "^.*" (weight 0) matches everything.
    static (bool Omit, bool Optional) Match((string Pattern, bool Omit, bool Optional, double Weight)[] rules, string path)
    {
        var best = rules
            .Where(r => Regex.IsMatch(path, r.Pattern))
            .OrderByDescending(r => r.Weight)
            .First();
        return (best.Omit, best.Optional);
    }

    static Dictionary<string, object> RulesDict((string Pattern, bool Omit, bool Optional, double Weight)[] rules)
    {
        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            if (!r.Omit && !r.Optional && r.Weight == 0)
            {
                d[r.Pattern] = true;
                continue;
            }
            var v = new Dictionary<string, object>(StringComparer.Ordinal);
            if (r.Omit)
                v["omit"] = true;
            if (r.Optional)
                v["optional"] = true;
            v["weight"] = r.Weight;
            d[r.Pattern] = v;
        }
        return d;
    }
}
