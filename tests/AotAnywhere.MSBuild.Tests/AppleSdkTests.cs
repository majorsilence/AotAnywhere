using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

public class AppleSdkTests
{
    static string MakeDeveloperDir(params string[] sdks)
    {
        string dev = Path.Combine(Path.GetTempPath(), "aotanywhere-sdk-" + Guid.NewGuid().ToString("N"));
        foreach (var sdk in sdks)
        {
            string platform = sdk.StartsWith("iPhoneSimulator") ? "iPhoneSimulator" : "iPhoneOS";
            Directory.CreateDirectory(Path.Combine(dev, "Platforms", platform + ".platform", "Developer", "SDKs", sdk));
        }
        return dev;
    }

    [Test]
    public async Task PicksTheNewestVersionedSdk()
    {
        string dev = MakeDeveloperDir("iPhoneOS26.5.sdk", "iPhoneOS27.0.sdk", "iPhoneOS.sdk", "iPhoneSimulator27.0.sdk");

        await Assert.That(Path.GetFileName(AppleSdk.SdkPath(dev, "iphoneos"))).IsEqualTo("iPhoneOS27.0.sdk");
        await Assert.That(Path.GetFileName(AppleSdk.SdkPath(dev, "iphonesimulator"))).IsEqualTo("iPhoneSimulator27.0.sdk");
    }

    [Test]
    public async Task HonoursAVersionedSdkName()
    {
        string dev = MakeDeveloperDir("iPhoneOS26.5.sdk", "iPhoneOS27.0.sdk");

        await Assert.That(Path.GetFileName(AppleSdk.SdkPath(dev, "iphoneos26.5"))).IsEqualTo("iPhoneOS26.5.sdk");
    }

    [Test]
    public async Task MissingPlatformIsAnError()
    {
        string dev = MakeDeveloperDir("iPhoneOS27.0.sdk");

        await Assert.That(() => AppleSdk.SdkPath(dev, "iphonesimulator")).Throws<AppleToolException>();
    }

    [Test]
    [Arguments("macosx")]
    [Arguments("appletvos")]
    public async Task NonIosSdksAreRejected(string sdk)
    {
        await Assert.That(() => AppleSdk.PlatformName(sdk)).Throws<AppleToolException>();
    }
}
