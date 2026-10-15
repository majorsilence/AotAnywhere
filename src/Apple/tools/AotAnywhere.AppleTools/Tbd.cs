using System.Text;
using System.Text.RegularExpressions;

namespace AotAnywhere.AppleTools;

/// <summary>One library in a text-based stub (.tbd, version 4): its install name and exports.</summary>
public sealed class TbdLibrary
{
    public required string InstallName { get; init; }
    public string? CurrentVersion { get; init; }
    public string? CompatibilityVersion { get; init; }

    /// <summary>Exported symbol (full Mach-O name, e.g. <c>_OBJC_CLASS_$_UIView</c>) -> list key it came from.</summary>
    public Dictionary<string, string> Exports { get; } = new(StringComparer.Ordinal);

    /// <summary>The subset of <see cref="Exports"/> listed under <c>reexports:</c> - provided by another library this one re-exports.</summary>
    public HashSet<string> ReexportedSymbols { get; } = new(StringComparer.Ordinal);

    /// <summary>Install names of the libraries this one re-exports (their exports are its exports too).</summary>
    public List<string> ReexportedLibraries { get; } = [];
}

/// <summary>
/// Reads and writes the YAML (version 4) text-based stubs that Apple's linkers - and zig's -
/// link against in place of real dylibs. Reading is used only to attribute symbols to libraries
/// (by the maintainer-run stub generators); writing produces AotAnywhere's own minimal stubs.
/// </summary>
public static class Tbd
{
    /// <summary>The export list keys, in the order tbd-v4 writes them.</summary>
    public static readonly string[] ListKeys = ["symbols", "objc-classes", "objc-eh-types", "objc-ivars", "weak-symbols", "thread-local-symbols"];

    /// <summary>
    /// Every library document in a .tbd file. The first is the library itself; the others are
    /// re-exported libraries Apple inlines (UIKitCore inside UIKit, libsystem_* inside libSystem).
    /// Only <c>exports:</c> and <c>reexports:</c> count - <c>undefineds:</c> are imports.
    /// </summary>
    public static List<TbdLibrary> Parse(string text)
    {
        var result = new List<TbdLibrary>();
        foreach (var doc in Regex.Split(text.ReplaceLineEndings("\n"), @"^--- !tapi-tbd[^\n]*\n", RegexOptions.Multiline))
        {
            var installName = Scalar(doc, "install-name");
            if (installName is null)
                continue;
            var lib = new TbdLibrary
            {
                InstallName = installName,
                CurrentVersion = Scalar(doc, "current-version"),
                CompatibilityVersion = Scalar(doc, "compatibility-version"),
            };
            foreach (var (section, body) in TopLevelSections(doc))
            {
                if (section == "reexported-libraries")
                {
                    foreach (var (_, items) in Lists(body, "libraries"))
                        lib.ReexportedLibraries.AddRange(items.Where(i => !lib.ReexportedLibraries.Contains(i)));
                    continue;
                }
                if (section is not ("exports" or "reexports"))
                    continue;
                foreach (var (key, items) in Lists(body))
                    foreach (var item in items)
                        foreach (var symbol in Expand(key, item))
                        {
                            lib.Exports.TryAdd(symbol, key);
                            if (section == "reexports")
                                lib.ReexportedSymbols.Add(symbol);
                        }
            }
            result.Add(lib);
        }
        return result;
    }

    // objc-classes entries name the class; the linker sees its class and metaclass symbols.
    static IEnumerable<string> Expand(string key, string item) => key switch
    {
        "objc-classes" => ["_OBJC_CLASS_$_" + item, "_OBJC_METACLASS_$_" + item],
        "objc-eh-types" => ["_OBJC_EHTYPE_$_" + item],
        "objc-ivars" => ["_OBJC_IVAR_$_" + item],
        _ => [item],
    };

    static string? Scalar(string doc, string key)
    {
        var m = Regex.Match(doc, $@"^{Regex.Escape(key)}:\s*(.+?)\s*$", RegexOptions.Multiline);
        return m.Success ? Unquote(m.Groups[1].Value) : null;
    }

    static IEnumerable<(string Section, string Body)> TopLevelSections(string doc)
    {
        var matches = Regex.Matches(doc, @"^([a-z][a-z0-9-]*):", RegexOptions.Multiline);
        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index + matches[i].Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : doc.Length;
            yield return (matches[i].Groups[1].Value, doc[start..end]);
        }
    }

    // "  symbols: [ a, 'b$c', ... ]" flow lists inside a section, possibly over several lines.
    static IEnumerable<(string Key, List<string> Items)> Lists(string body, params string[] keys)
    {
        if (keys.Length == 0)
            keys = ListKeys;
        foreach (Match m in Regex.Matches(body, @"^\s+(?:-\s+)?([a-z-]+):\s*\[", RegexOptions.Multiline))
        {
            string key = m.Groups[1].Value;
            if (!keys.Contains(key))
                continue;
            var items = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            for (int i = m.Index + m.Length; i < body.Length; i++)
            {
                char c = body[i];
                if (quoted)
                {
                    if (c == '\'' && i + 1 < body.Length && body[i + 1] == '\'')
                    {
                        current.Append('\'');
                        i++;
                    }
                    else if (c == '\'')
                        quoted = false;
                    else
                        current.Append(c);
                    continue;
                }
                if (c == '\'')
                    quoted = true;
                else if (c is ',' or ']')
                {
                    if (current.ToString().Trim() is { Length: > 0 } item)
                        items.Add(item);
                    current.Clear();
                    if (c == ']')
                        break;
                }
                else if (!char.IsWhiteSpace(c))
                    current.Append(c);
            }
            yield return (key, items);
        }
    }

    static string Unquote(string s) => s.Length >= 2 && s[0] == '\'' && s[^1] == '\'' ? s[1..^1].Replace("''", "'") : s;

    /// <summary>
    /// A minimal tbd-v4 stub: <paramref name="symbols"/> are full Mach-O names keyed by the list
    /// they belong in (<c>objc-classes</c> etc. are written back in their short form).
    /// </summary>
    /// <param name="reexported">Symbols this library provides on behalf of a library it re-exports, by list key.</param>
    public static string Write(string installName, IReadOnlyList<string> targets, IReadOnlyDictionary<string, IReadOnlyCollection<string>> symbols,
        string? currentVersion = null, string? compatibilityVersion = null, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? reexported = null)
    {
        string targetList = "[ " + string.Join(", ", targets) + " ]";
        var sb = new StringBuilder();
        sb.Append("--- !tapi-tbd\n");
        sb.Append("tbd-version:     4\n");
        sb.Append("targets:         ").Append(targetList).Append('\n');
        sb.Append("install-name:    ").Append(Quote(installName)).Append('\n');
        if (currentVersion is not null)
            sb.Append("current-version: ").Append(ThreePartVersion(currentVersion)).Append('\n');
        if (compatibilityVersion is not null)
            sb.Append("compatibility-version: ").Append(ThreePartVersion(compatibilityVersion)).Append('\n');

        AppendSection(sb, "exports", targetList, symbols);
        if (reexported is not null)
            AppendSection(sb, "reexports", targetList, reexported);
        sb.Append("...\n");
        return sb.ToString();
    }

    // zig misreads two-part versions ("2710.120" becomes 2710.12.0 in LC_LOAD_DYLIB); three
    // parts read as written.
    static string ThreePartVersion(string version) =>
        version.Count(c => c == '.') == 1 && version.Split('.').All(p => p.Length > 0 && p.All(char.IsAsciiDigit)) ? version + ".0" : version;

    static void AppendSection(StringBuilder sb, string section, string targetList, IReadOnlyDictionary<string, IReadOnlyCollection<string>> symbols)
    {
        var lists = new List<string>();
        foreach (var key in ListKeys)
        {
            if (!symbols.TryGetValue(key, out var names) || names.Count == 0)
                continue;
            var items = key switch
            {
                "objc-classes" => names.Select(n => StripPrefix(n, "_OBJC_CLASS_$_", "_OBJC_METACLASS_$_")),
                "objc-eh-types" => names.Select(n => StripPrefix(n, "_OBJC_EHTYPE_$_")),
                "objc-ivars" => names.Select(n => StripPrefix(n, "_OBJC_IVAR_$_")),
                _ => names,
            };
            lists.Add(FlowList(key, items.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        }
        if (lists.Count == 0)
            return;
        sb.Append(section).Append(":\n");
        sb.Append("  - targets:         ").Append(targetList).Append('\n');
        foreach (var l in lists)
            sb.Append(l).Append('\n');
    }

    static string StripPrefix(string name, params string[] prefixes)
    {
        foreach (var p in prefixes)
            if (name.StartsWith(p, StringComparison.Ordinal))
                return name[p.Length..];
        return name;
    }

    static readonly Regex Plain = new("^[A-Za-z0-9_.]+$", RegexOptions.CultureInvariant);

    static string Quote(string s) => Plain.IsMatch(s) ? s : "'" + s.Replace("'", "''") + "'";

    static string FlowList(string key, IEnumerable<string> items)
    {
        var lines = new List<string>();
        var line = new StringBuilder($"    {key}:".PadRight(22) + "[ ");
        var list = items.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string token = Quote(list[i]) + (i < list.Count - 1 ? "," : " ]");
            if (line.Length + token.Length > 100 && line.ToString().Trim().Length > key.Length + 3)
            {
                lines.Add(line.ToString().TrimEnd());
                line = new StringBuilder(new string(' ', 24));
            }
            line.Append(token).Append(' ');
        }
        lines.Add(line.ToString().TrimEnd());
        return string.Join('\n', lines);
    }
}
