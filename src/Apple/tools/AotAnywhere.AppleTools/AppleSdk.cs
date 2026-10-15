namespace AotAnywhere.AppleTools;

/// <summary>
/// Answers xcrun's SDK queries (<c>--sdk iphoneos --show-sdk-path</c> and friends) from an Xcode
/// developer directory laid out like the real one:
/// <c>&lt;dev&gt;/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS&lt;ver&gt;.sdk</c>.
/// The workload passes that directory to every tool it launches as <c>DEVELOPER_DIR</c>.
/// </summary>
public static class AppleSdk
{
    public static string PlatformName(string sdkName)
    {
        // "iphoneos", "iphoneos26.5", "iphonesimulator", ... (case-insensitive, like xcrun)
        string s = sdkName.ToLowerInvariant();
        if (s.StartsWith("iphonesimulator", StringComparison.Ordinal))
            return "iPhoneSimulator";
        if (s.StartsWith("iphoneos", StringComparison.Ordinal))
            return "iPhoneOS";
        throw new AppleToolException($"AotAnywhere: SDK '{sdkName}' is not available off-Mac (only iphoneos and iphonesimulator are).");
    }

    public static string PlatformPath(string developerDir, string sdkName) =>
        Path.Combine(developerDir, "Platforms", PlatformName(sdkName) + ".platform");

    /// <summary>Path of the newest <c>&lt;Platform&gt;&lt;version&gt;.sdk</c> under the platform (or the one named by a versioned sdkName).</summary>
    public static string SdkPath(string developerDir, string sdkName)
    {
        string platform = PlatformName(sdkName);
        string sdks = Path.Combine(PlatformPath(developerDir, sdkName), "Developer", "SDKs");
        if (!Directory.Exists(sdks))
            throw new AppleToolException($"AotAnywhere: no {platform} SDKs found under '{sdks}'. Import an Xcode .xip first (see docs/ios-targets.md).");

        string requested = sdkName.Substring(platform.Length);
        var candidates = Directory.GetDirectories(sdks, platform + "*.sdk")
            .Select(p => (Path: p, Version: ParseVersion(Path.GetFileNameWithoutExtension(p).Substring(platform.Length))))
            .Where(c => c.Version is not null)
            .ToList();

        if (requested.Length > 0)
            candidates = candidates.Where(c => c.Version == ParseVersion(requested)).ToList();

        if (candidates.Count == 0)
            throw new AppleToolException($"AotAnywhere: SDK '{sdkName}' was not found under '{sdks}'.");
        return candidates.OrderByDescending(c => c.Version).First().Path;
    }

    // "" -> null (the unversioned "iPhoneOS.sdk" symlink is skipped in favour of the versioned directory)
    static Version? ParseVersion(string s) =>
        Version.TryParse(s.Contains('.') ? s : s + ".0", out var v) ? v : null;
}
