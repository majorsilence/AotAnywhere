namespace AotAnywhere.AppleTools;

/// <summary>
/// Makes a link against AotAnywhere's stub sysroot bind each imported symbol to the library
/// ld64 would pick with the SDK.
///
/// A symbol is often exported by several libraries: the one implementing it, and others that
/// re-export it (CoreFoundation re-exports libobjc). ld64 binds to the implementing library when
/// it is on the link line, else to the first library on the line that re-exports it. It then
/// applies the implementing library's back-deployment directives for the deployment target:
/// <c>$ld$previous$</c> (on iOS 12.2-16.0 NSKeyValueObservation lived in libswiftFoundation, not
/// Foundation - so that is what an app targeting iOS 15 must bind to), <c>$ld$hide$</c>,
/// <c>$ld$add$</c> and <c>$ld$install_name$</c>. Autolinked libraries that end up binding nothing
/// are dropped (ld64 treats them as dead-strippable).
///
/// zig does none of this, so before each link this writes a private copy of the stubs on the
/// link line in which every symbol is exported by exactly the library ld64 would bind it to.
/// The sysroot's stubs (eng/generate-apple-ios-sysroot.cs) list a library's own symbols under
/// <c>exports:</c>, those it re-exports under <c>reexports:</c>, and keep the SDK's directives
/// for the symbols they contain.
/// </summary>
public static class StubLink
{
    /// <summary>The targets the stubs declare: device (arm64, arm64e) and simulator.</summary>
    public static readonly string[] Targets = ["arm64-ios", "arm64e-ios", "arm64-ios-simulator", "x86_64-ios-simulator"];

    const string Previous = "$ld$previous$", Hide = "$ld$hide$", Add = "$ld$add$", InstallName = "$ld$install_name$";

    /// <summary>
    /// The symbol a linker directive applies to: "" for library-wide ones (<c>$ld$install_name$</c>,
    /// a <c>$ld$previous$</c> without a symbol), null if <paramref name="symbol"/> is not a directive.
    /// </summary>
    public static string? LdDirectiveTarget(string symbol)
    {
        if (symbol.StartsWith(Previous, StringComparison.Ordinal))
            return ParsePrevious(symbol)?.Symbol;
        if (symbol.StartsWith(Hide, StringComparison.Ordinal) || symbol.StartsWith(Add, StringComparison.Ordinal))
            return ParseVersioned(symbol)?.Value;
        if (symbol.StartsWith(InstallName, StringComparison.Ordinal))
            return "";
        return null;
    }

    sealed record PreviousDirective(string InstallName, string? CompatibilityVersion, int Platform, Version Start, Version End, string Symbol);

    // $ld$previous$<install name>$<compatibility version>$<platform>$<start>$<end>$<symbol>$
    // (the symbol itself may contain '$', as Swift's do).
    static PreviousDirective? ParsePrevious(string s)
    {
        var parts = s[Previous.Length..].Split('$', 6);
        if (parts.Length < 6 || !int.TryParse(parts[2], out int platform) || !Version.TryParse(parts[3], out var start) || !Version.TryParse(parts[4], out var end))
            return null;
        string symbol = parts[5].EndsWith('$') ? parts[5][..^1] : parts[5];
        return new PreviousDirective(parts[0], parts[1].Length == 0 ? null : parts[1], platform, start, end, symbol);
    }

    // $ld$hide$os<version>$<symbol>, $ld$add$os<version>$<symbol>, $ld$install_name$os<version>$<path>
    static (Version Version, string Value)? ParseVersioned(string s)
    {
        int prefix = s.IndexOf('$', 4) + 1; // after "$ld$<kind>$"
        int end = s.IndexOf('$', prefix);
        if (prefix <= 0 || end < 0 || !s[prefix..end].StartsWith("os", StringComparison.Ordinal) || !Version.TryParse(s[(prefix + 2)..end], out var v))
            return null;
        return (v, s[(end + 1)..]);
    }

    sealed class Entry
    {
        public required List<string> Rels;
        public required string InstallName;
        public string? StubInstallName; // as the sysroot names it, before $ld$install_name$/$ld$previous$
        public string? CurrentVersion, CompatibilityVersion;
        public required Dictionary<string, string> Exports; // symbol -> list key (directives removed)
        public required HashSet<string> Reexported;
        public Dictionary<string, (string InstallName, string? Compat)> MovedTo = new(StringComparer.Ordinal);
        public bool Autolinked;
        public List<int> ArgIndices = [];
        public readonly Dictionary<string, string> Chosen = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes the per-link stubs for the sysroot libraries <paramref name="zigArgs"/> links into
    /// <paramref name="linkSysroot"/> and returns the arguments to link with.
    /// </summary>
    /// <param name="autolinkStart">Index in <paramref name="zigArgs"/> where the autolinked libraries begin.</param>
    /// <param name="referenced">The symbols the link's objects import; autolinked libraries supplying none are dropped.</param>
    /// <param name="weakOnly">Symbols only referenced weakly; a library binding nothing else is linked weakly (LC_LOAD_WEAK_DYLIB), as ld64 does.</param>
    public static List<string> Prepare(IReadOnlyList<string> zigArgs, string sysroot, string linkSysroot,
        int autolinkStart = int.MaxValue, IReadOnlySet<string>? referenced = null, IReadOnlySet<string>? weakOnly = null)
    {
        sysroot = Path.GetFullPath(sysroot).TrimEnd('/', '\\');
        string linkRoot = linkSysroot.TrimEnd('/', '\\');
        var (minOS, platform) = DeploymentTarget(zigArgs);

        // One entry per library: aliases (libm.tbd and libdl.tbd are libSystem) share an install name.
        var entries = new List<Entry>();
        foreach (var (rel, argIndex) in LinkedStubs(zigArgs, sysroot))
        {
            var docs = Tbd.Parse(File.ReadAllText(Path.Combine(sysroot, rel)));
            if (docs.Count == 0)
                continue;
            var lib = docs[0];
            var existing = entries.Find(e => e.StubInstallName == lib.InstallName);
            if (existing is not null)
            {
                existing.Rels.Add(rel);
                existing.ArgIndices.Add(argIndex);
                existing.Autolinked &= argIndex >= autolinkStart;
                continue;
            }
            var loaded = Load(rel, lib, minOS, platform, argIndex >= autolinkStart);
            loaded.ArgIndices.Add(argIndex);
            entries.Add(loaded);
        }

        // Library-wide $ld$previous$/$ld$install_name$ can give two stubs the same install name
        // for this OS version (libswift_Builtin_float was part of libswiftDarwin): one library.
        for (int i = 0; i < entries.Count; i++)
            for (int j = entries.Count - 1; j > i; j--)
            {
                if (entries[j].InstallName != entries[i].InstallName)
                    continue;
                var (a, b) = (entries[i], entries[j]);
                a.Rels.AddRange(b.Rels);
                a.ArgIndices.AddRange(b.ArgIndices);
                a.Autolinked &= b.Autolinked;
                foreach (var (symbol, kind) in b.Exports)
                    if (a.Exports.TryAdd(symbol, kind) && b.Reexported.Contains(symbol))
                        a.Reexported.Add(symbol);
                foreach (var (symbol, moved) in b.MovedTo)
                    a.MovedTo.TryAdd(symbol, moved);
                entries.RemoveAt(j);
            }

        // ld64's choice for every symbol: implementing libraries first, then re-exporters.
        var chosen = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (bool ownPass in new[] { true, false })
            foreach (var e in entries)
                foreach (var symbol in e.Exports.Keys)
                    if (ownPass != e.Reexported.Contains(symbol))
                        chosen.TryAdd(symbol, e);

        // Then the chosen library's $ld$previous$ for this deployment target moves it.
        var synthetic = new List<Entry>();
        foreach (var (symbol, e) in chosen)
        {
            var target = e;
            if (e.MovedTo.TryGetValue(symbol, out var moved))
            {
                target = entries.Concat(synthetic).FirstOrDefault(x => x.InstallName == moved.InstallName);
                if (target is null)
                {
                    target = new Entry
                    {
                        Rels = [$"aotanywhere-previous/lib{synthetic.Count}.tbd"],
                        InstallName = moved.InstallName,
                        // As ld64: the version of the library the directive is in.
                        CurrentVersion = e.CurrentVersion,
                        CompatibilityVersion = moved.Compat,
                        Exports = new(StringComparer.Ordinal),
                        Reexported = new(StringComparer.Ordinal),
                    };
                    synthetic.Add(target);
                }
            }
            target.Chosen[symbol] = e.Exports[symbol];
        }

        // Unused autolinked libraries, and $ld$previous$ libraries nothing referenced moved to.
        var drop = new HashSet<Entry>(entries.Where(e => e.Autolinked).Concat(synthetic)
            .Where(e => referenced is not null && !e.Chosen.Keys.Any(referenced.Contains)));
        foreach (var e in entries.Concat(synthetic).Where(e => !drop.Contains(e)))
        {
            var symbols = e.Chosen.GroupBy(c => c.Value, c => c.Key).ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.ToList(), StringComparer.Ordinal);
            string text = Tbd.Write(e.InstallName, Targets, symbols, e.CurrentVersion, e.CompatibilityVersion);
            foreach (var rel in e.Rels)
            {
                string path = Path.Combine(linkRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
        }
        if (Environment.GetEnvironmentVariable("AOTANYWHERE_APPLE_DEBUG") is "1" or "true")
            File.WriteAllLines(Path.Combine(linkRoot, "aotanywhere-link.txt"), entries.Concat(synthetic).Select(e =>
                $"{e.InstallName} autolinked={e.Autolinked} dropped={drop.Contains(e)} chosen={e.Chosen.Count} referenced=[{string.Join(' ', e.Chosen.Keys.Where(k => referenced?.Contains(k) == true).Take(8))}]"));
        string settings = Path.Combine(sysroot, "SDKSettings.json");
        if (File.Exists(settings))
            File.Copy(settings, Path.Combine(linkRoot, "SDKSettings.json"), overwrite: true);

        // Point the link at the per-link stubs, minus dropped autolinked libraries and second
        // mentions of a merged library, plus the libraries $ld$previous$ moved symbols to.
        var droppedArgs = new HashSet<int>();
        var weakArgs = new HashSet<int>();
        foreach (var e in entries)
        {
            droppedArgs.UnionWith(drop.Contains(e) ? e.ArgIndices : e.ArgIndices.Order().Skip(1));
            var used = e.Chosen.Keys.Where(k => referenced?.Contains(k) == true).ToList();
            if (weakOnly is not null && used.Count > 0 && used.All(weakOnly.Contains))
                weakArgs.Add(e.ArgIndices.Min());
        }
        var result = new List<string>();
        for (int i = 0; i < zigArgs.Count; i++)
        {
            if (droppedArgs.Contains(i))
            {
                if (zigArgs[i] is "-framework" or "-weak_framework")
                    i++;
                continue;
            }
            string arg = zigArgs[i].Replace(sysroot, linkRoot, StringComparison.Ordinal);
            if (weakArgs.Contains(i) && arg == "-framework")
                arg = "-weak_framework";
            else if (weakArgs.Contains(i) && arg.StartsWith("-l", StringComparison.Ordinal))
                arg = "-weak-l" + arg[2..];
            result.Add(arg);
        }
        result.AddRange(synthetic.Where(e => !drop.Contains(e)).Select(e => Path.Combine(linkRoot, e.Rels[0])));
        return result;
    }

    static Entry Load(string rel, TbdLibrary lib, Version minOS, int platform, bool autolinked)
    {
        var e = new Entry
        {
            Rels = [rel],
            InstallName = lib.InstallName,
            StubInstallName = lib.InstallName,
            CurrentVersion = lib.CurrentVersion,
            CompatibilityVersion = lib.CompatibilityVersion,
            Exports = new(StringComparer.Ordinal),
            Reexported = new(lib.ReexportedSymbols, StringComparer.Ordinal),
            Autolinked = autolinked,
        };
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (symbol, kind) in lib.Exports)
        {
            if (symbol.StartsWith(Previous, StringComparison.Ordinal))
            {
                if (ParsePrevious(symbol) is { } p && p.Platform == platform && p.Start <= minOS && minOS < p.End)
                {
                    if (p.Symbol.Length == 0)
                    {
                        e.InstallName = p.InstallName;
                        e.CompatibilityVersion = p.CompatibilityVersion ?? e.CompatibilityVersion;
                    }
                    else
                        e.MovedTo[p.Symbol] = (p.InstallName, p.CompatibilityVersion);
                }
            }
            else if (symbol.StartsWith(InstallName, StringComparison.Ordinal))
            {
                if (ParseVersioned(symbol) is { } v && SameVersion(v.Version, minOS))
                    e.InstallName = v.Value;
            }
            else if (symbol.StartsWith(Hide, StringComparison.Ordinal))
            {
                if (ParseVersioned(symbol) is { } v && SameVersion(v.Version, minOS))
                    hidden.Add(v.Value);
            }
            else if (symbol.StartsWith(Add, StringComparison.Ordinal))
            {
                if (ParseVersioned(symbol) is { } v && SameVersion(v.Version, minOS))
                    e.Exports.TryAdd(v.Value, "symbols");
            }
            else
                e.Exports.TryAdd(symbol, kind);
        }
        foreach (var h in hidden)
            e.Exports.Remove(h);
        return e;
    }

    static bool SameVersion(Version a, Version b) => a.Major == b.Major && Math.Max(a.Minor, 0) == Math.Max(b.Minor, 0);

    // --target=aarch64-ios.15.0[-simulator] -> (15.0, PLATFORM_IOS = 2 | PLATFORM_IOSSIMULATOR = 7)
    static (Version MinOS, int Platform) DeploymentTarget(IReadOnlyList<string> args)
    {
        string target = args.FirstOrDefault(a => a.StartsWith("--target=", StringComparison.Ordinal))
            ?? throw new AppleToolException("AotAnywhere: the link has no --target.");
        bool simulator = target.EndsWith("-simulator", StringComparison.Ordinal);
        string os = target[(target.IndexOf("-ios.", StringComparison.Ordinal) + 5)..];
        if (simulator)
            os = os[..^"-simulator".Length];
        return (Version.TryParse(os, out var v) ? v : new Version(0, 0), simulator ? 7 : 2);
    }

    /// <summary>The sysroot-relative stubs the link line names, in link order, with the argument naming each.</summary>
    public static List<(string Rel, int ArgIndex)> LinkedStubs(IReadOnlyList<string> args, string sysroot)
    {
        var libraryDirs = new List<string>();
        var frameworkDirs = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is "-L" or "-F" && i + 1 < args.Count)
                (args[i] == "-L" ? libraryDirs : frameworkDirs).Add(args[++i]);
            else if (args[i].StartsWith("-L", StringComparison.Ordinal) && args[i].Length > 2)
                libraryDirs.Add(args[i][2..]);
            else if (args[i].StartsWith("-F", StringComparison.Ordinal) && args[i].Length > 2)
                frameworkDirs.Add(args[i][2..]);
        }

        var result = new List<(string, int)>();
        void Add(string? path, int index)
        {
            if (path is null || !path.EndsWith(".tbd", StringComparison.Ordinal))
                return;
            string full = Path.GetFullPath(path);
            if (full.StartsWith(sysroot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                result.Add((Path.GetRelativePath(sysroot, full), index));
        }

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a is "-framework" or "-weak_framework" && i + 1 < args.Count)
            {
                string fw = args[i + 1];
                Add(frameworkDirs.Select(d => Path.Combine(d, fw + ".framework", fw + ".tbd")).FirstOrDefault(File.Exists), i);
                i++;
            }
            else if (a.StartsWith("-l", StringComparison.Ordinal) && a.Length > 2)
            {
                // The first library the search paths have, whatever its kind: a static archive
                // or dylib found first shadows the stub, as it would for the linker.
                Add(libraryDirs.SelectMany(d => new[] { ".tbd", ".dylib", ".a" }.Select(ext => Path.Combine(d, "lib" + a[2..] + ext))).FirstOrDefault(File.Exists), i);
            }
            else if (!a.StartsWith('-'))
                Add(a, i);
        }
        return result;
    }
}
