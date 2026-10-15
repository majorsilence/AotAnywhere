using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// The stand-in Xcode the Apple package generates when no Xcode is configured. Validated end to
// end: hello-world, MAUI and Avalonia build against it with nothing of Xcode read, and run in the
// simulator. These pin what the workload reads from it.
public class SyntheticXcodeTests
{
    static string Generate()
    {
        var app = Path.Combine(Path.GetTempPath(), "aotanywhere-synthetic-" + Guid.NewGuid().ToString("N"), "Xcode.app");
        SyntheticXcode.Write(app, "27.0", "27.0");
        return app;
    }

    static Dictionary<string, object> Read(string path) => Plist.ReadDictionary(File.ReadAllBytes(path));

    [Test]
    public async Task XcodeVersionPlistsCarryTheRequestedVersion()
    {
        var app = Generate();
        await Assert.That(Read(Path.Combine(app, "Contents", "version.plist"))["CFBundleShortVersionString"]).IsEqualTo("27.0");
        var info = Read(Path.Combine(app, "Contents", "Info.plist"));
        await Assert.That(info["CFBundleShortVersionString"]).IsEqualTo("27.0");
        await Assert.That(info["DTXcode"]).IsEqualTo("2700");
        // The workload's Xcode locator requires these to exist.
        await Assert.That(File.Exists(Path.Combine(app, "Contents", "MacOS", "Xcode"))).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(app, "Contents", "Developer", "usr", "bin"))).IsTrue();
    }

    [Test]
    [Arguments("iphoneos", "iPhoneOS")]
    [Arguments("iphonesimulator", "iPhoneSimulator")]
    public async Task EachPlatformHasAnSdkXcrunFinds(string sdkName, string platform)
    {
        var app = Generate();
        string developer = Path.Combine(app, "Contents", "Developer");

        string sdk = AppleSdk.SdkPath(developer, sdkName);

        await Assert.That(Path.GetFileName(sdk)).IsEqualTo(platform + "27.0.sdk");
        var settings = Read(Path.Combine(sdk, "SDKSettings.plist"));
        await Assert.That(settings["Version"]).IsEqualTo("27.0");
        await Assert.That(settings["CanonicalName"]).IsEqualTo(sdkName + "27.0");
        // The app's DT* keys come from the platform's AdditionalInfo.
        var additional = (Dictionary<string, object>)Read(Path.Combine(AppleSdk.PlatformPath(developer, sdkName), "Info.plist"))["AdditionalInfo"];
        await Assert.That(additional["DTPlatformVersion"]).IsEqualTo("27.0");
        await Assert.That(additional["DTPlatformName"]).IsEqualTo(sdkName);
    }

    [Test]
    public async Task EntitlementsTemplateIsTheApplicationIdentifierAndItsKeychainGroup()
    {
        var app = Generate();
        var template = Read(Path.Combine(AppleSdk.SdkPath(Path.Combine(app, "Contents", "Developer"), "iphoneos"), "Entitlements.plist"));

        await Assert.That(template["application-identifier"]).IsEqualTo("$(AppIdentifierPrefix)$(CFBundleIdentifier)");
        await Assert.That((List<object>)template["keychain-access-groups"]).IsEquivalentTo(new object[] { "$(AppIdentifierPrefix)$(CFBundleIdentifier)" });
    }

    [Test]
    public async Task ItIsMarkedAsGenerated()
    {
        var app = Generate();
        await Assert.That(File.Exists(Path.Combine(app, SyntheticXcode.Marker))).IsTrue();
    }
}
