using AotAnywhere.AppleTools;

namespace AotAnywhere.MSBuild.Tests;

// The actool stand-in. Validated on the simulator: with MAUI's generated app icon compiled by our
// actool, SpringBoard shows the catalog icon (a recoloured catalog-only icon replaces the loose
// files), and assetutil reads every rendition. These pin the structure iOS depends on.
public class AssetCatalogTests
{
    static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aotanywhere-ac-" + Guid.NewGuid().ToString("N"))).FullName;

    static byte[] SolidPng(int size, byte r, byte g, byte b, byte a = 255)
    {
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a;
        }
        return Png.FromRgba(size, size, rgba).Encode();
    }

    // An app icon set like the one MAUI's resizetizer generates.
    static string IconCatalog(string root)
    {
        string set = Directory.CreateDirectory(Path.Combine(root, "Assets.xcassets", "appicon.appiconset")).FullName;
        var slots = new (string Idiom, string Size, int Scale)[]
        {
            ("iphone", "20x20", 2), ("iphone", "60x60", 2), ("iphone", "60x60", 3),
            ("ipad", "20x20", 2), ("ipad", "76x76", 2), ("ipad", "83.5x83.5", 2), ("ios-marketing", "1024x1024", 1),
        };
        var images = new List<string>();
        foreach (var (idiom, size, scale) in slots)
        {
            double points = double.Parse(size.Split('x')[0], System.Globalization.CultureInfo.InvariantCulture);
            string file = $"icon{size}@{scale}x{idiom}.png";
            File.WriteAllBytes(Path.Combine(set, file), SolidPng((int)Math.Round(points * scale), 0x51, 0x2b, 0xd4));
            images.Add($$"""{ "idiom": "{{idiom}}", "size": "{{size}}", "scale": "{{scale}}x", "filename": "{{file}}" }""");
        }
        File.WriteAllText(Path.Combine(set, "Contents.json"), $$"""{ "images": [ {{string.Join(",", images)}} ], "info": { "version": 1, "author": "xcode" } }""");
        return Path.Combine(root, "Assets.xcassets");
    }

    static AssetCatalog.Options Options(string dir) =>
        new(Path.Combine(dir, "bundle"), "appicon", ["iphone", "ipad"], Path.Combine(dir, "partial-info.plist"));

    [Test]
    public async Task AppIconWritesTheCatalogTheLooseIconsAndThePartialInfoPlist()
    {
        var dir = Temp();
        var outputs = AssetCatalog.Compile([IconCatalog(dir)], Options(dir));

        await Assert.That(outputs.Select(o => Path.GetFileName(o)!)).IsEquivalentTo(
            new[] { "partial-info.plist", "Assets.car", "appicon60x60@2x.png", "appicon76x76@2x~ipad.png" });

        var info = Plist.ReadDictionary(File.ReadAllBytes(Path.Combine(dir, "partial-info.plist")));
        var phone = (Dictionary<string, object>)((Dictionary<string, object>)info["CFBundleIcons"])["CFBundlePrimaryIcon"];
        await Assert.That(phone["CFBundleIconName"]).IsEqualTo("appicon");
        await Assert.That(((List<object>)phone["CFBundleIconFiles"]).Cast<string>()).IsEquivalentTo(new[] { "appicon60x60" });
        var pad = (Dictionary<string, object>)((Dictionary<string, object>)info["CFBundleIcons~ipad"])["CFBundlePrimaryIcon"];
        await Assert.That(((List<object>)pad["CFBundleIconFiles"]).Cast<string>()).IsEquivalentTo(new[] { "appicon60x60", "appicon76x76" });
    }

    [Test]
    public async Task EveryIconSlotIsARenditionAndEachIdiomHasAMultisizeSet()
    {
        var dir = Temp();
        AssetCatalog.Compile([IconCatalog(dir)], Options(dir));
        var car = File.ReadAllBytes(Path.Combine(dir, "bundle", "Assets.car"));

        var facet = CarWriter.ReadFacets(car)["appicon"];
        await Assert.That(facet[CarWriter.Part]).IsEqualTo((ushort)220);

        var renditions = CarWriter.ReadRenditions(car);
        var bitmaps = renditions.Where(r => r.Attributes[CarWriter.Part] == 220).ToList();
        await Assert.That(bitmaps.Count).IsEqualTo(7);
        await Assert.That(bitmaps.All(r => r.Attributes[CarWriter.Identifier] == facet[CarWriter.Identifier])).IsTrue();
        var ipad167 = bitmaps.Single(r => r.Width == 167);
        await Assert.That(ipad167.Attributes[CarWriter.Idiom]).IsEqualTo((ushort)2);
        await Assert.That(ipad167.Name).IsEqualTo("appicon83.5x83.5@2x.png");

        // Without these iOS ignores the catalog icon: one per idiom, listing point sizes and their indexes.
        var sets = renditions.Where(r => r.Layout == 1010).ToList();
        await Assert.That(sets.Select(r => (int)r.Attributes[CarWriter.Idiom]).Order()).IsEquivalentTo(new[] { 1, 2, 6 });
        var padSet = sets.Single(r => r.Attributes[CarWriter.Idiom] == 2).Value;
        int count = BitConverter.ToInt32(padSet, padSet.Length - 12 * 3 - 4);
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(BitConverter.ToInt32(padSet, padSet.Length - 12)).IsEqualTo(83); // 83.5pt, truncated
        await Assert.That(BitConverter.ToInt32(padSet, padSet.Length - 4)).IsEqualTo(ipad167.Attributes[CarWriter.Dimension2]);
    }

    [Test]
    public async Task ASingleSizeIconIsScaledToEverySlot()
    {
        var dir = Temp();
        string set = Directory.CreateDirectory(Path.Combine(dir, "A.xcassets", "AppIcon.appiconset")).FullName;
        File.WriteAllBytes(Path.Combine(set, "icon.png"), SolidPng(1024, 200, 10, 10));
        File.WriteAllText(Path.Combine(set, "Contents.json"), """{ "images": [ { "idiom": "universal", "platform": "ios", "size": "1024x1024", "filename": "icon.png" } ] }""");

        AssetCatalog.Compile([Path.Combine(dir, "A.xcassets")], Options(dir) with { AppIcon = "AppIcon" });

        var loose = Png.Decode(File.ReadAllBytes(Path.Combine(dir, "bundle", "AppIcon60x60@2x.png")));
        await Assert.That((loose.Width, loose.Height)).IsEqualTo((120, 120));
        await Assert.That(loose.Rgba[0..4]).IsEquivalentTo(new byte[] { 200, 10, 10, 255 });
        var sizes = CarWriter.ReadRenditions(File.ReadAllBytes(Path.Combine(dir, "bundle", "Assets.car"))).Where(r => r.Layout != 1010).Select(r => r.Width).Order();
        await Assert.That(sizes).IsEquivalentTo(new[] { 120, 152, 167, 180, 1024 });
    }

    [Test]
    public async Task ImageSetsAreNamedRenditions()
    {
        var dir = Temp();
        string set = Directory.CreateDirectory(Path.Combine(dir, "A.xcassets", "Icons", "logo.imageset")).FullName;
        File.WriteAllText(Path.Combine(dir, "A.xcassets", "Icons", "Contents.json"), """{ "properties": { "provides-namespace": true } }""");
        File.WriteAllBytes(Path.Combine(set, "logo@2x.png"), SolidPng(8, 0, 0, 0, 128));
        File.WriteAllText(Path.Combine(set, "Contents.json"), """{ "images": [ { "idiom": "universal", "scale": "2x", "filename": "logo@2x.png" } ] }""");

        var outputs = AssetCatalog.Compile([Path.Combine(dir, "A.xcassets")], Options(dir) with { AppIcon = null });

        await Assert.That(outputs.Select(o => Path.GetFileName(o)!)).IsEquivalentTo(new[] { "partial-info.plist", "Assets.car" });
        var car = File.ReadAllBytes(Path.Combine(dir, "bundle", "Assets.car"));
        await Assert.That(CarWriter.ReadFacets(car).Keys).IsEquivalentTo(new[] { "Icons/logo" });
        var r = CarWriter.ReadRenditions(car).Single();
        await Assert.That((r.Attributes[CarWriter.Scale], r.Attributes[CarWriter.Part], r.Width)).IsEqualTo(((ushort)2, (ushort)181, 8));
    }

    // test/ios/IBTest's catalog: checked at run time by the app's Assets self-test.
    [Test]
    public async Task ColorDataVectorAndDarkVariantRenditions()
    {
        var dir = Temp();
        string catalog = Path.Combine(Harness.RepoRoot, "test", "ios", "IBTest", "Assets.xcassets");
        AssetCatalog.Compile([catalog], Options(dir) with { AppIcon = null });
        var car = File.ReadAllBytes(Path.Combine(dir, "bundle", "Assets.car"));
        await Assert.That(CarWriter.ReadFacets(car).Keys).IsEquivalentTo(new[] { "Brand", "Config", "Glyph", "Logo", "Photo" });
        var renditions = CarWriter.ReadRenditions(car);
        var facets = CarWriter.ReadFacets(car);
        List<(Dictionary<ushort, ushort> Attributes, int Layout, int Width, int Height, string Name, byte[] Value)> Of(string name) =>
            renditions.Where(r => r.Attributes[CarWriter.Element] == facets[name][CarWriter.Element]
                && r.Attributes.GetValueOrDefault(CarWriter.Identifier) == facets[name].GetValueOrDefault(CarWriter.Identifier)).ToList();

        var brand = Of("Brand");
        await Assert.That(brand.Select(r => r.Layout).Distinct()).IsEquivalentTo(new[] { 1009 });
        await Assert.That(brand.Select(r => r.Attributes.GetValueOrDefault(CarWriter.Appearance))).IsEquivalentTo(new ushort[] { 0, CarWriter.AppearanceDark });
        await Assert.That(Of("Config").Single().Layout).IsEqualTo(1000);
        // Vectors keep their source, plus bitmaps at each scale for UIKit.
        foreach (var (name, points) in new[] { ("Logo", 32), ("Glyph", 24) })
        {
            var set = Of(name);
            await Assert.That(set.Count(r => r.Layout == 9)).IsEqualTo(1);
            await Assert.That(set.Where(r => r.Layout != 9).Select(r => r.Width)).IsEquivalentTo(new[] { points, points * 2, points * 3 });
        }
        await Assert.That(Of("Photo").Select(r => r.Attributes.GetValueOrDefault(CarWriter.Appearance)).Distinct().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task VectorImagesRasterize()
    {
        static (byte R, byte G, byte B, byte A) Pixel(Png png, int x, int y)
        {
            int i = (y * png.Width + x) * 4;
            return (png.Rgba[i], png.Rgba[i + 1], png.Rgba[i + 2], png.Rgba[i + 3]);
        }
        var svg = VectorImage.Load("""<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect x="0" y="0" width="5" height="10" fill="#ff0000"/></svg>"""u8.ToArray(), ".svg");
        var png = VectorImage.Render(svg, 2);
        await Assert.That(png.Width).IsEqualTo(20);
        await Assert.That(Pixel(png, 2, 10)).IsEqualTo(((byte)255, (byte)0, (byte)0, (byte)255));
        await Assert.That(Pixel(png, 17, 10).A).IsEqualTo((byte)0);

        // A one-page PDF filling its right half blue (PDF's origin is bottom-left).
        string content = "0 0 1 rg 5 0 5 10 re f";
        string pdf = $"%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n" +
            $"3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R >> endobj\n" +
            $"4 0 obj << /Length {content.Length} >> stream\n{content}\nendstream endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n";
        png = VectorImage.Render(VectorImage.Load(System.Text.Encoding.ASCII.GetBytes(pdf), ".pdf"), 1);
        await Assert.That(Pixel(png, 8, 5)).IsEqualTo(((byte)0, (byte)0, (byte)255, (byte)255));
        await Assert.That(Pixel(png, 1, 5).A).IsEqualTo((byte)0);

        await Assert.That(() => VectorImage.Load("""<svg xmlns="http://www.w3.org/2000/svg" width="4" height="4"><linearGradient id="g"/></svg>"""u8.ToArray(), ".svg"))
            .Throws<Exception>();
    }

    [Test]
    public async Task UnsupportedAssetsAndMissingIconsAreErrors()
    {
        var dir = Temp();
        string accent = Directory.CreateDirectory(Path.Combine(dir, "A.xcassets", "AccentColor.colorset")).FullName;
        File.WriteAllText(Path.Combine(accent, "Contents.json"), """{ "colors": [ { "idiom": "universal", "color": { "platform": "ios", "reference": "systemBlueColor" } } ] }""");
        await Assert.That(() => AssetCatalog.Compile([Path.Combine(dir, "A.xcassets")], Options(dir) with { AppIcon = null }))
            .Throws<AssetCatalog.CompileError>();

        var dir2 = Temp();
        Directory.CreateDirectory(Path.Combine(dir2, "B.xcassets"));
        await Assert.That(() => AssetCatalog.Compile([Path.Combine(dir2, "B.xcassets")], Options(dir2)))
            .Throws<AssetCatalog.CompileError>();
    }

    [Test]
    public async Task PngDecoderHandlesPaletteGrayAndTransparency()
    {
        // 2x1 palette image, 2-bit depth, second entry transparent via tRNS; built by hand.
        byte[] raw = [0, 0b0001_0000]; // filter none; indexes 0, 1
        var idat = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionLevel.Optimal, true))
            z.Write(raw);
        var png = BuildPng(2, 1, depth: 2, colorType: 3,
            ("PLTE", [255, 0, 0, 0, 0, 255]), ("tRNS", [255, 0]), ("IDAT", idat.ToArray()));

        var image = Png.Decode(png);

        await Assert.That(image.Rgba).IsEquivalentTo(new byte[] { 255, 0, 0, 255, 0, 0, 255, 0 });
        await Assert.That(image.Opaque).IsFalse();
        // Premultiplied BGRA zeroes the transparent pixel's colour.
        await Assert.That(image.ToPremultipliedBgra()).IsEquivalentTo(new byte[] { 0, 0, 255, 255, 0, 0, 0, 0 });
    }

    [Test]
    public async Task PngRoundTripsThroughTheEncoder()
    {
        var rgba = Enumerable.Range(0, 3 * 2 * 4).Select(i => (byte)(i * 10)).ToArray();
        var decoded = Png.Decode(Png.FromRgba(3, 2, rgba).Encode());
        await Assert.That(decoded.Rgba).IsEquivalentTo(rgba);
    }

    static byte[] BuildPng(int w, int h, byte depth, byte colorType, params (string Type, byte[] Body)[] chunks)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = depth;
        ihdr[9] = colorType;
        foreach (var (type, body) in new[] { ("IHDR", ihdr) }.Concat(chunks).Append(("IEND", Array.Empty<byte>())))
        {
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
            ms.Write(len);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
            ms.Write(body);
            ms.Write(new byte[4]); // CRC isn't checked by the decoder
        }
        return ms.ToArray();
    }
}
