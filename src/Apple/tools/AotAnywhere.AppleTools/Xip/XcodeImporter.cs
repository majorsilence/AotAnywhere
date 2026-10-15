namespace AotAnywhere.AppleTools.Xip;

/// <summary>
/// Builds the minimal Xcode folder an off-Mac iOS build needs, from an Xcode .xip the user
/// downloaded from Apple (or from an installed Xcode.app): the version plists the workload checks,
/// the iPhoneOS / iPhoneSimulator platform SDKs (headers and .tbd stubs), and the toolchain's iOS
/// Swift libraries. A few GB instead of Xcode's 40+; no Apple executables are kept.
/// </summary>
public static class XcodeImporter
{
    static readonly string[] Platforms = ["iPhoneOS", "iPhoneSimulator"];

    static readonly string[] ExactFiles =
    [
        "Contents/Info.plist",
        "Contents/version.plist",
        .. Platforms.SelectMany(p => new[] { $"Contents/Developer/Platforms/{p}.platform/Info.plist", $"Contents/Developer/Platforms/{p}.platform/version.plist" }),
    ];

    static readonly string[] Prefixes =
    [
        .. Platforms.SelectMany(p => new[] { $"Contents/Developer/Platforms/{p}.platform/Developer/SDKs/", $"Contents/Developer/Platforms/{p}.platform/Developer/usr/lib/" }),
        "Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/lib/swift/iphoneos/",
        "Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/lib/swift/iphonesimulator/",
    ];

    /// <summary>Whether the app-relative path (forward slashes) is kept.</summary>
    public static bool Wanted(string rel) =>
        ExactFiles.Contains(rel) || Prefixes.Any(p => rel.StartsWith(p, StringComparison.Ordinal));

    // Whether a directory can hold kept content (it lies on the way to, or inside, a kept prefix).
    static bool MayContain(string dirRel) =>
        ExactFiles.Any(f => f.StartsWith(dirRel + "/", StringComparison.Ordinal))
        || Prefixes.Any(p => p.StartsWith(dirRel + "/", StringComparison.Ordinal) || (dirRel + "/").StartsWith(p, StringComparison.Ordinal));

    public static void ImportXip(string xipPath, string outputApp, Action<string>? log = null)
    {
        using var archive = File.OpenRead(xipPath);
        var content = Xar.Find(archive, "Content");
        using var stream = Xar.Open(archive, content);
        var cpio = new Cpio(new BufferedStream(new ChunkStream(Pbzx.ReadChunks(stream, Math.Max(2, Environment.ProcessorCount))), 1 << 20));

        Stage(outputApp, staging =>
        {
            var writer = new Writer(staging);
            string? appPrefix = null;
            long files = 0;
            cpio.ReadAll((entry, data) =>
            {
                string name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                // Every path lives under the single top-level "Xcode*.app/".
                int slash = name.IndexOf('/');
                if (slash < 0)
                    return;
                appPrefix ??= name[..(slash + 1)];
                if (!name.StartsWith(appPrefix, StringComparison.Ordinal))
                    return;
                string rel = name[appPrefix.Length..];
                if (!Wanted(rel))
                    return;

                switch (entry.Type)
                {
                    case Cpio.Kind.Directory:
                        writer.Directory(rel);
                        break;
                    case Cpio.Kind.Symlink:
                        using (var r = new StreamReader(data))
                            writer.Symlink(rel, r.ReadToEnd());
                        break;
                    case Cpio.Kind.File:
                        writer.File(rel, data, entry.Size, entry.Links > 1 ? (entry.Device, entry.Inode) : null);
                        if (++files % 5000 == 0)
                            log?.Invoke($"  {files} files...");
                        break;
                }
            });
            if (appPrefix is null)
                throw new XipFormatException("the archive contains no .app");
            writer.Finish();
            log?.Invoke($"  {files} files extracted from {appPrefix.TrimEnd('/')}");
        });
    }

    public static void ImportDirectory(string xcodeApp, string outputApp, Action<string>? log = null)
    {
        if (!File.Exists(Path.Combine(xcodeApp, "Contents", "version.plist")))
            throw new AppleToolException($"AotAnywhere: '{xcodeApp}' does not look like an Xcode.app (no Contents/version.plist).");

        Stage(outputApp, staging =>
        {
            var writer = new Writer(staging);
            long files = 0;
            void Visit(string rel)
            {
                string full = Path.Combine(xcodeApp, rel);
                if (new FileInfo(full).LinkTarget is { } target)
                {
                    if (Wanted(rel))
                        writer.Symlink(rel, target);
                }
                else if (Directory.Exists(full))
                {
                    if (Wanted(rel + "/"))
                        writer.Directory(rel);
                    foreach (var child in Directory.EnumerateFileSystemEntries(full))
                    {
                        string childRel = rel + "/" + Path.GetFileName(child);
                        if (Wanted(childRel) || MayContain(childRel))
                            Visit(childRel);
                    }
                }
                else if (Wanted(rel))
                {
                    using var s = File.OpenRead(full);
                    writer.File(rel, s, s.Length, null);
                    files++;
                }
            }

            Visit("Contents");
            writer.Finish();
            log?.Invoke($"  {files} files copied");
        });
    }

    // Extract into a sibling staging directory, then swap it into place, so an interrupted
    // import never leaves a half-populated Xcode where builds would find it.
    static void Stage(string outputApp, Action<string> fill)
    {
        string full = Path.GetFullPath(outputApp);
        string staging = full + ".importing";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        fill(staging);
        AddPlaceholders(staging);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
        Directory.Move(staging, full);
    }

    // The workload only checks that these exist: dotnet-linker recognizes an Xcode.app by its
    // Contents/MacOS/Xcode file (tools/common/Driver.cs), and DetectSdkLocations requires
    // Contents/Developer/usr/bin. Empty stand-ins; no Apple executables are copied.
    static void AddPlaceholders(string app)
    {
        Directory.CreateDirectory(Path.Combine(app, "Contents", "Developer", "usr", "bin"));
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "Xcode"), []);
    }

    sealed class Writer(string root)
    {
        readonly Dictionary<(long, long), string> _hardLinks = new();
        readonly List<(string Rel, string Target)> _deferredLinks = new();

        string Full(string rel)
        {
            string full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new XipFormatException($"entry '{rel}' escapes the output directory");
            return full;
        }

        public void Directory(string rel) => System.IO.Directory.CreateDirectory(Full(rel));

        public void File(string rel, Stream data, long size, (long, long)? linkKey)
        {
            string full = Full(rel);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            // odc stores a hard-linked file's data once; later links have size 0.
            if (linkKey is { } key && size == 0 && _hardLinks.TryGetValue(key, out var first))
            {
                System.IO.File.Copy(first, full, overwrite: true);
                return;
            }
            using (var o = System.IO.File.Create(full))
                data.CopyTo(o);
            if (linkKey is { } k)
                _hardLinks.TryAdd(k, full);
        }

        public void Symlink(string rel, string target)
        {
            string full = Full(rel);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (OperatingSystem.IsWindows())
            {
                // Creating symlinks needs elevation or Developer Mode on Windows; materialize
                // them as copies once everything they can point at has been extracted.
                _deferredLinks.Add((rel, target));
                return;
            }
            if (System.IO.File.Exists(full) || System.IO.Directory.Exists(full))
                System.IO.File.Delete(full);
            System.IO.File.CreateSymbolicLink(full, target);
        }

        public void Finish()
        {
            // Resolve chains (a link to a link) by repeating until nothing changes.
            bool progress = true;
            while (_deferredLinks.Count > 0 && progress)
            {
                progress = false;
                foreach (var link in _deferredLinks.ToList())
                {
                    string full = Full(link.Rel);
                    string source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, link.Target));
                    if (System.IO.File.Exists(source))
                        System.IO.File.Copy(source, full, overwrite: true);
                    else if (System.IO.Directory.Exists(source))
                        CopyDirectory(source, full);
                    else
                        continue;
                    _deferredLinks.Remove(link);
                    progress = true;
                }
            }
            // Links to content outside the allowlist are dropped.
        }

        static void CopyDirectory(string from, string to)
        {
            System.IO.Directory.CreateDirectory(to);
            foreach (var f in System.IO.Directory.EnumerateFiles(from))
                System.IO.File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
            foreach (var d in System.IO.Directory.EnumerateDirectories(from))
                CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
