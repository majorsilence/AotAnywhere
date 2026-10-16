using System.Globalization;
using System.Text.Json;

namespace AotAnywhere.AppleTools;

/// <summary>
/// The actool stand-in: compiles <c>.xcassets</c> folders (app icons and PNG image sets) into
/// <c>Assets.car</c> (<see cref="CarWriter"/>). It also writes the loose icon files and the
/// partial Info.plist icon keys. The <c>Contents.json</c> layout is Xcode's documented asset
/// catalog format (Asset Catalog Format Reference). Anything else (colours, data, symbols,
/// vector images ...) is an error naming the item, so the build stops instead of shipping an app
/// without it.
/// </summary>
public static class AssetCatalog
{
    public sealed class CompileError(string message) : Exception(message);

    public sealed record Options(
        string OutputDirectory,
        string? AppIcon,
        IReadOnlyCollection<string> TargetDevices,
        string? PartialInfoPlist);

    // CoreUI idiom attribute values (car_format.h).
    static readonly Dictionary<string, ushort> Idioms = new(StringComparer.Ordinal)
    {
        ["universal"] = 0, ["iphone"] = 1, ["ipad"] = 2, ["tv"] = 3, ["car"] = 4, ["watch"] = 5, ["ios-marketing"] = 6,
    };

    const ushort ElementImage = 85, PartImage = 181, PartMultisize = 218, PartIcon = 220;

    /// <summary>The files written, the way actool lists them in its compilation results.</summary>
    public static List<string> Compile(IEnumerable<string> catalogs, Options options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var car = new CarWriter();
        var outputs = new List<string>();
        var identifiers = new HashSet<ushort>();
        Dictionary<string, object>? iconInfo = null;
        bool anyRendition = false;
        string? foundIcon = null;

        foreach (var catalog in catalogs)
        {
            foreach (var (dir, name) in Assets(catalog.TrimEnd('/', '\\'), ""))
            {
                string kind = Path.GetExtension(dir);
                switch (kind)
                {
                    case ".imageset":
                        anyRendition |= ImageSet(car, dir, name, NameIdentifier(name, identifiers), options.TargetDevices);
                        break;
                    case ".appiconset" when name == options.AppIcon:
                        foundIcon = name;
                        iconInfo = AppIconSet(car, dir, name, NameIdentifier(name, identifiers), options, outputs);
                        anyRendition = true;
                        break;
                    case ".appiconset":
                        break; // only the selected icon is compiled, as actool does without --include-all-app-icons
                    default:
                        throw new CompileError($"{dir}: '{kind}' assets are not supported (only app icons and PNG/JPEG image sets are)");
                }
            }
        }
        if (options.AppIcon is not null && foundIcon is null)
            throw new CompileError($"None of the input catalogs contained a matching app icon set named \"{options.AppIcon}\".");

        if (anyRendition)
        {
            string carPath = Path.Combine(options.OutputDirectory, "Assets.car");
            File.WriteAllBytes(carPath, car.Write());
            outputs.Insert(0, carPath);
        }
        if (options.PartialInfoPlist is not null)
        {
            File.WriteAllBytes(options.PartialInfoPlist, Plist.WriteXml(iconInfo ?? new Dictionary<string, object>(StringComparer.Ordinal)));
            outputs.Insert(0, options.PartialInfoPlist);
        }
        return outputs;
    }

    // Asset folders under a catalog, with their names. Plain folders group assets; one marked
    // "provides-namespace" prefixes its children's names with "<folder>/".
    static IEnumerable<(string Dir, string Name)> Assets(string dir, string prefix)
    {
        foreach (var child in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            string leaf = Path.GetFileName(child);
            if (Path.GetExtension(leaf).Length > 1)
            {
                yield return (child, prefix + Path.GetFileNameWithoutExtension(leaf));
                continue;
            }
            bool ns = false;
            string contents = Path.Combine(child, "Contents.json");
            if (File.Exists(contents))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(contents));
                ns = doc.RootElement.TryGetProperty("properties", out var p) && p.TryGetProperty("provides-namespace", out var v) && v.ValueKind == JsonValueKind.True;
            }
            foreach (var asset in Assets(child, ns ? prefix + leaf + "/" : prefix))
                yield return asset;
        }
    }

    // A stable 16-bit identifier for an asset name (CoreUI keys renditions by it, not the name).
    static ushort NameIdentifier(string name, HashSet<ushort> used)
    {
        uint hash = 2166136261;
        foreach (char c in name)
            hash = (hash ^ c) * 16777619;
        ushort id = (ushort)(hash ^ (hash >> 16));
        while (id == 0 || !used.Add(id))
            id++;
        return id;
    }

    sealed record Entry(string? Idiom, string? Size, double Scale, string? File, string? Platform);

    static List<Entry> Images(string dir)
    {
        string path = Path.Combine(dir, "Contents.json");
        if (!File.Exists(path))
            throw new CompileError($"{dir}: missing Contents.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<Entry>();
        if (!doc.RootElement.TryGetProperty("images", out var images))
            return list;
        foreach (var e in images.EnumerateArray())
        {
            string? S(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (e.TryGetProperty("appearances", out _))
                throw new CompileError($"{dir}: dark/tinted appearance variants are not supported");
            string? scale = S("scale");
            list.Add(new Entry(S("idiom"), S("size"), scale is null ? 0 : double.Parse(scale.TrimEnd('x'), CultureInfo.InvariantCulture), S("filename"), S("platform")));
        }
        return list;
    }

    static bool Targeted(string? idiom, IReadOnlyCollection<string> devices) =>
        idiom is null or "universal" or "ios-marketing" || devices.Count == 0 || devices.Contains(idiom);

    static ushort IdiomValue(string dir, string? idiom) =>
        Idioms.TryGetValue(idiom ?? "universal", out var v) ? v : throw new CompileError($"{dir}: unknown idiom '{idiom}'");

    static Png Load(string dir, string file)
    {
        string path = Path.Combine(dir, file);
        if (!Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new CompileError($"{path}: only PNG images are supported");
        try
        {
            return Png.Decode(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is AppleToolException or InvalidDataException or EndOfStreamException)
        {
            throw new CompileError($"{path}: {e.Message}");
        }
    }

    static bool ImageSet(CarWriter car, string dir, string name, ushort id, IReadOnlyCollection<string> devices)
    {
        bool any = false;
        foreach (var e in Images(dir))
        {
            if (e.File is null || !Targeted(e.Idiom, devices))
                continue;
            car.AddRendition(new CarWriter.Rendition(new()
            {
                [CarWriter.Scale] = (ushort)e.Scale,
                [CarWriter.Idiom] = IdiomValue(dir, e.Idiom),
                [CarWriter.Identifier] = id,
                [CarWriter.Element] = ElementImage,
                [CarWriter.Part] = PartImage,
            }, e.File, Load(dir, e.File), e.Scale == 0 ? 1 : e.Scale));
            any = true;
        }
        if (any)
            car.AddFacet(name, new() { [CarWriter.Element] = ElementImage, [CarWriter.Part] = PartImage, [CarWriter.Identifier] = id });
        return any;
    }

    // iOS app icons: every slot goes into the catalog (keyed by idiom, scale and point size), the
    // 60pt@2x iPhone and 76pt@2x iPad files are also copied loose, and the partial Info.plist
    // names them (CFBundleIconFiles) and the catalog icon (CFBundleIconName).
    static Dictionary<string, object> AppIconSet(CarWriter car, string dir, string name, ushort id, Options options, List<string> outputs)
    {
        var entries = Images(dir).Where(e => e.File is not null).ToList();
        // Xcode 14's single-size format: one 1024pt universal image, scaled to every slot.
        if (entries.Count == 1 && entries[0].Idiom == "universal" && entries[0].Size == "1024x1024")
        {
            string file = entries[0].File!;
            entries = [
                new("iphone", "60x60", 2, file, null), new("iphone", "60x60", 3, file, null),
                new("ipad", "76x76", 2, file, null), new("ipad", "83.5x83.5", 2, file, null),
                new("ios-marketing", "1024x1024", 1, file, null),
            ];
        }

        var sizes = entries.Select(e => PointSize(dir, e.Size)).Distinct().Order().ToList();
        var loaded = new Dictionary<string, Png>(StringComparer.Ordinal);
        var loose = new Dictionary<string, (Png Image, string Source, int Pixels)>(StringComparer.Ordinal);
        var idiomSizes = new List<(ushort Idiom, double Points)>();
        foreach (var e in entries)
        {
            if (!Targeted(e.Idiom, options.TargetDevices))
                continue;
            if (e.Scale == 0)
                throw new CompileError($"{dir}: icon '{e.File}' has no scale");
            double points = PointSize(dir, e.Size);
            int pixels = (int)Math.Round(points * e.Scale);
            if (!loaded.TryGetValue(e.File!, out var source))
                loaded[e.File!] = source = Load(dir, e.File!);
            var image = source.Width == pixels && source.Height == pixels ? source : source.Resize(pixels, pixels);
            string fileName = $"{name}{SizeName(points)}@{e.Scale:0}x.png";
            idiomSizes.Add((IdiomValue(dir, e.Idiom), points));
            car.AddRendition(new CarWriter.Rendition(new()
            {
                [CarWriter.Scale] = (ushort)e.Scale,
                [CarWriter.Idiom] = IdiomValue(dir, e.Idiom),
                [CarWriter.Dimension2] = (ushort)(sizes.IndexOf(points) + 1),
                [CarWriter.Identifier] = id,
                [CarWriter.Element] = ElementImage,
                [CarWriter.Part] = PartIcon,
            }, fileName, image, e.Scale));

            if (e.Scale == 2 && (e.Idiom, points) is ("iphone", 60) or ("ipad", 76))
                loose[$"{name}{SizeName(points)}@2x{(e.Idiom == "ipad" ? "~ipad" : "")}.png"] = (image, Path.Combine(dir, e.File!), pixels);
        }
        car.AddFacet(name, new() { [CarWriter.Element] = ElementImage, [CarWriter.Part] = PartIcon, [CarWriter.Identifier] = id });
        // Per idiom, the multisize image set listing its sizes; without it iOS ignores the catalog
        // icon and falls back to the loose files.
        foreach (var group in idiomSizes.GroupBy(s => s.Idiom))
            car.AddRendition(new CarWriter.Rendition(new()
            {
                [CarWriter.Scale] = 1,
                [CarWriter.Idiom] = group.Key,
                [CarWriter.Identifier] = id,
                [CarWriter.Element] = ElementImage,
                [CarWriter.Part] = PartMultisize,
            }, name, null, 1, group.Select(s => ((int)s.Points, (int)s.Points, sizes.IndexOf(s.Points) + 1)).Distinct().OrderBy(s => s.Item3).ToList()));

        foreach (var (file, (image, source, pixels)) in loose.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            string dest = Path.Combine(options.OutputDirectory, file);
            if (image.Width == pixels && Png.ReadSize(File.ReadAllBytes(source)) == (pixels, pixels))
                File.Copy(source, dest, overwrite: true);
            else
                File.WriteAllBytes(dest, image.Encode());
            outputs.Add(dest);
        }

        var info = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> Primary(params string[] files) => new(StringComparer.Ordinal)
        {
            ["CFBundlePrimaryIcon"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["CFBundleIconFiles"] = files.Select(f => (object)f).ToList(),
                ["CFBundleIconName"] = name,
            },
        };
        bool phone = loose.Keys.Any(k => !k.EndsWith("~ipad.png", StringComparison.Ordinal));
        bool pad = loose.Keys.Any(k => k.EndsWith("~ipad.png", StringComparison.Ordinal));
        if (options.TargetDevices.Count == 0 || options.TargetDevices.Contains("iphone"))
            info["CFBundleIcons"] = Primary(phone ? [$"{name}60x60"] : []);
        if (options.TargetDevices.Contains("ipad"))
            info["CFBundleIcons~ipad"] = Primary([.. phone ? new[] { $"{name}60x60" } : [], .. pad ? new[] { $"{name}76x76" } : []]);
        return info;
    }

    static double PointSize(string dir, string? size)
    {
        var parts = size?.Split('x');
        if (parts is not [var w, var h] || w != h || !double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var points))
            throw new CompileError($"{dir}: icon size '{size}' is not supported");
        return points;
    }

    static string SizeName(double points)
    {
        string p = points.ToString(CultureInfo.InvariantCulture);
        return $"{p}x{p}";
    }
}
