using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Generates the Objective-C declarations the workload's generated code (registrar.mm,
/// main.mm) compiles against, from the binding assemblies' own metadata instead of
/// Apple's SDK headers.
///
/// The registrar only needs to know what each framework name *is*: <c>@interface</c>
/// for classes (with the superclass, so its own subclasses compile, and the adopted
/// protocols, so it can pass them where a protocol type is expected), <c>@protocol</c>
/// for protocols, and struct layouts for by-value parameters. All of that is in the
/// MIT-licensed bindings: <c>[Register("Name", true)]</c> wrapper classes and their
/// base types, <c>[Protocol(Name = ...)]</c> interfaces and the interfaces they extend,
/// and the binding structs' fields. Method declarations aren't needed: the registrar
/// only defines methods on its own classes and sends messages through
/// <c>objc_msgSend</c>.
///
/// Every framework header (<c>&lt;UIKit/UIKit.h&gt;</c>, ...) is a stub that imports
/// one generated file with every declaration, so frameworks can refer to each other's
/// types in any order.
/// </summary>
public static class ObjCShims
{
    public const string DeclarationsFile = "AotAnywhereObjC.h";

    static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    // Provided by objc4's headers (bundled with zig) or by the hand-written prelude.
    static readonly HashSet<string> Predeclared = ["NSObject", "Protocol"];

    // Foundation's protocols that, unlike the rest, don't extend <NSObject>. It changes the
    // property lists the compiler emits for classes adopting them.
    static readonly HashSet<string> RootProtocols = ["NSCoding", "NSCopying", "NSMutableCopying", "NSFastEnumeration", "NSLocking"];

    // The few framework methods the workload's own templates call directly (main.mm).
    static readonly Dictionary<string, string> KnownMethods = new(StringComparer.Ordinal)
    {
        ["NSAutoreleasePool"] = "- (void)drain;",
    };

    // C typedefs the bindings express as .NET primitives, so their names appear nowhere in
    // metadata. NSInteger/NSUInteger/BOOL come from objc4's <objc/NSObjCRuntime.h>.
    const string Prelude = """
        #pragma once
        #include <stdarg.h>
        #include <stdbool.h>
        #include <stddef.h>
        #include <stdint.h>
        #include <objc/objc.h>
        #include <objc/runtime.h>
        #include <objc/NSObject.h>
        #include <objc/NSObjCRuntime.h>
        typedef double CGFloat;
        typedef double NSTimeInterval;
        typedef struct _NSZone NSZone;
        #ifndef TRUE
        #define TRUE 1
        #endif
        #ifndef FALSE
        #define FALSE 0
        #endif
        """;

    // Structs that appear by value in UIKit-era method signatures, spelled the way the
    // frameworks declare them (from Apple's published API reference), so that the type
    // encodings the compiler writes into the app's method lists - which the runtime hands
    // out through method_getTypeEncoding / NSMethodSignature - match an Xcode build:
    // {CGRect={CGPoint=dd}{CGSize=dd}} and {_NSRange=QQ}, not the bindings' flattened
    // {CGRect=dddd} / {NSRange=qq}. Other binding structs fall back to the binding layout.
    static readonly (string Name, string Declaration)[] WellKnownStructs =
    [
        ("CGPoint", "struct CGPoint { CGFloat x; CGFloat y; }; typedef struct CGPoint CGPoint;"),
        ("CGSize", "struct CGSize { CGFloat width; CGFloat height; }; typedef struct CGSize CGSize;"),
        ("CGVector", "struct CGVector { CGFloat dx; CGFloat dy; }; typedef struct CGVector CGVector;"),
        ("CGRect", "struct CGRect { CGPoint origin; CGSize size; }; typedef struct CGRect CGRect;"),
        ("CGAffineTransform", "struct CGAffineTransform { CGFloat a, b, c, d; CGFloat tx, ty; }; typedef struct CGAffineTransform CGAffineTransform;"),
        ("NSRange", "typedef struct _NSRange { NSUInteger location; NSUInteger length; } NSRange;"),
        ("NSOperatingSystemVersion", "typedef struct { NSInteger majorVersion; NSInteger minorVersion; NSInteger patchVersion; } NSOperatingSystemVersion;"),
        ("UIEdgeInsets", "typedef struct UIEdgeInsets { CGFloat top, left, bottom, right; } UIEdgeInsets;"),
        ("NSDirectionalEdgeInsets", "typedef struct NSDirectionalEdgeInsets { CGFloat top, leading, bottom, trailing; } NSDirectionalEdgeInsets;"),
        ("UIOffset", "typedef struct UIOffset { CGFloat horizontal, vertical; } UIOffset;"),
        ("UIFloatRange", "typedef struct UIFloatRange { CGFloat minimum, maximum; } UIFloatRange;"),
        ("CATransform3D", "struct CATransform3D { CGFloat m11, m12, m13, m14; CGFloat m21, m22, m23, m24; CGFloat m31, m32, m33, m34; CGFloat m41, m42, m43, m44; }; typedef struct CATransform3D CATransform3D;"),
        ("CLLocationCoordinate2D", "typedef double CLLocationDegrees; typedef struct CLLocationCoordinate2D { CLLocationDegrees latitude; CLLocationDegrees longitude; } CLLocationCoordinate2D;"),
        ("MKCoordinateSpan", "typedef struct { CLLocationDegrees latitudeDelta; CLLocationDegrees longitudeDelta; } MKCoordinateSpan;"),
        ("MKCoordinateRegion", "typedef struct { CLLocationCoordinate2D center; MKCoordinateSpan span; } MKCoordinateRegion;"),
        ("MKMapPoint", "typedef struct { double x; double y; } MKMapPoint;"),
        ("MKMapSize", "typedef struct { double width; double height; } MKMapSize;"),
        ("MKMapRect", "typedef struct { MKMapPoint origin; MKMapSize size; } MKMapRect;"),
        ("CMTime", "typedef int64_t CMTimeValue; typedef int32_t CMTimeScale; typedef uint32_t CMTimeFlags; typedef int64_t CMTimeEpoch; typedef struct { CMTimeValue value; CMTimeScale timescale; CMTimeFlags flags; CMTimeEpoch epoch; } CMTime;"),
        ("CMTimeRange", "typedef struct { CMTime start; CMTime duration; } CMTimeRange;"),
    ];

    sealed class TypeInfo
    {
        public required string FullName;
        public required string Namespace;
        public string? BaseFullName;
        public List<string> Interfaces = [];
        public bool IsInterface, IsValueType, IsEnum, IsPublic;
        public string? RegisterName;
        public bool IsWrapper;
        public string? ProtocolName;
        public bool IsInformal;
        public string? NativeName;
        public List<(string Name, string Type)>? Fields; // instance fields, .NET type names
        public string? EnumUnderlying;
        public bool FromBindingAssembly;
    }

    public sealed record Result(int Classes, int Protocols, int Structs);

    /// <summary>
    /// Writes <see cref="DeclarationsFile"/> into <paramref name="outputDir"/>, plus a
    /// <c>&lt;Ns&gt;/&lt;Ns&gt;.h</c> stub for each binding namespace.
    /// </summary>
    public static Result Generate(IEnumerable<string> assemblies, string outputDir)
    {
        var types = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        foreach (var path in assemblies)
            Read(path, types);

        var protocols = new Dictionary<string, (string Name, List<string> Bases)>(StringComparer.Ordinal);
        // Informal protocols (categories on NSObject in the SDK) are declared by the registrar itself.
        foreach (var t in types.Values.Where(t => t.ProtocolName is not null && t.IsInterface && !t.IsInformal))
            protocols.TryAdd(t.ProtocolName!, (t.ProtocolName!, ProtocolsOf(t, types)));

        var classes = new Dictionary<string, (string Name, string? Super, List<string> Protocols, int Arity)>(StringComparer.Ordinal);
        foreach (var t in types.Values)
        {
            if (t.IsInterface || t.IsValueType || !t.IsWrapper || t.RegisterName is not { } name)
                continue;
            if (!Identifier.IsMatch(name) || Predeclared.Contains(name))
                continue;
            classes.TryAdd(name, (name, RegisteredBase(t, types), ProtocolsOf(t, types).Where(protocols.ContainsKey).Distinct().ToList(), GenericArity(t, types)));
        }

        var structs = new Dictionary<string, List<(string Name, string Type)>>(StringComparer.Ordinal);
        // Binding namespaces: those of the assemblies that bind anything. CoreGraphics has no
        // ObjC classes, only C types (CGRect, ...), so it counts by assembly, not by namespace.
        var namespaces = new HashSet<string>(types.Values.Where(t => t.FromBindingAssembly && IsBindingNamespace(t.Namespace)).Select(t => t.Namespace), StringComparer.Ordinal);
        foreach (var t in types.Values)
        {
            // Framework structs are public and prefixed (CGRect, NSRange); the bindings' internal
            // and lower-case ones (objc_super) mirror the ObjC runtime's own headers.
            if (!t.IsValueType || t.IsEnum || !t.IsPublic || t.Fields is not { Count: > 0 } || !namespaces.Contains(t.Namespace))
                continue;
            string name = StructName(t);
            var cFields = t.Fields.Select(f => (f.Name, Type: CType(f.Type, types))).ToList();
            if (Identifier.IsMatch(name) && char.IsUpper(name[0]) && !classes.ContainsKey(name) && !protocols.ContainsKey(name) && cFields.All(f => f.Type is not null))
                structs.TryAdd(name, cFields!);
        }

        var sb = new StringBuilder();
        sb.AppendLine("// Generated by AotAnywhere from the .NET binding assemblies' metadata ([Register],");
        sb.AppendLine("// [Protocol] and struct layouts); not derived from Apple's SDK headers.");
        sb.AppendLine(Prelude.ReplaceLineEndings("\n"));

        foreach (var (_, decl) in WellKnownStructs)
            sb.AppendLine(decl);

        // Structs, ordered so a struct's by-value fields are defined first.
        var structTypes = new Dictionary<string, string>(StringComparer.Ordinal); // .NET full name -> C name
        foreach (var t in types.Values)
            if (t.IsValueType && !t.IsEnum && structs.ContainsKey(StructName(t)))
                structTypes.TryAdd(t.FullName, StructName(t));
        // Every name the well-known declarations define (CMTimeScale, ...), not just the structs.
        var emittedStructs = new HashSet<string>(WellKnownStructs.SelectMany(w => TypedefName.Matches(w.Declaration).Select(m => m.Groups[1].Value)), StringComparer.Ordinal);
        foreach (var name in structs.Keys.Order(StringComparer.Ordinal))
            EmitStruct(name, structs, structTypes, emittedStructs, [], sb);

        foreach (var name in protocols.Keys.Order(StringComparer.Ordinal))
            sb.Append("@protocol ").Append(name).AppendLine(";");
        foreach (var name in classes.Keys.Order(StringComparer.Ordinal))
            sb.Append("@class ").Append(name).AppendLine(";");

        var emittedProtocols = new HashSet<string>(StringComparer.Ordinal) { "NSObject" };
        foreach (var name in protocols.Keys.Order(StringComparer.Ordinal))
            EmitProtocol(name, protocols, emittedProtocols, [], sb);

        var emittedClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in classes.Keys.Order(StringComparer.Ordinal))
            EmitClass(name, classes, emittedClasses, sb);

        Directory.CreateDirectory(outputDir);
        WriteIfChanged(Path.Combine(outputDir, DeclarationsFile), sb.ToString());
        foreach (var ns in namespaces)
            if (Identifier.IsMatch(ns))
                EnsureHeader(outputDir, $"{ns}/{ns}.h");
        // ObjCRuntime and CoreAnimation are binding namespaces whose framework is named differently.
        EnsureHeader(outputDir, "QuartzCore/QuartzCore.h");

        return new Result(classes.Count, protocols.Count, emittedStructs.Count);
    }

    /// <summary>
    /// Creates <c>&lt;framework&gt;/&lt;header&gt;.h</c> (if missing) as a stub importing every
    /// declaration. Used for the umbrella headers and for the non-umbrella headers the
    /// registrar sometimes imports (<c>&lt;CoreImage/CIFilterBuiltins.h&gt;</c>).
    /// </summary>
    public static void EnsureHeader(string shimDir, string header)
    {
        var path = Path.Combine(shimDir, header);
        if (File.Exists(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"#pragma once\n#import <{DeclarationsFile}>\n");
    }

    /// <summary>
    /// The framework-style headers (<c>&lt;Name/Header.h&gt;</c>, Name capitalised) a source file
    /// and the quoted headers next to it import, so that the shim directory can provide each.
    /// </summary>
    public static IEnumerable<string> FrameworkImports(string sourceFile)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([Path.GetFullPath(sourceFile)]);
        var result = new SortedSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var file))
        {
            if (!seen.Add(file) || !File.Exists(file))
                continue;
            foreach (Match m in IncludeLine.Matches(File.ReadAllText(file)))
            {
                string name = m.Groups["name"].Value;
                if (m.Groups["open"].Value == "\"")
                    pending.Enqueue(Path.Combine(Path.GetDirectoryName(file)!, name));
                else if (FrameworkHeader.IsMatch(name))
                    result.Add(name);
            }
        }
        return result;
    }

    static readonly Regex TypedefName = new(@"(\w+);", RegexOptions.CultureInvariant);
    static readonly Regex IncludeLine = new(@"^\s*#\s*(?:import|include)\s*(?<open>[<""])(?<name>[^>""]+)[>""]", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex FrameworkHeader = new(@"^[A-Z][A-Za-z0-9_]*/[A-Za-z0-9_+]+\.h$", RegexOptions.CultureInvariant);

    static void EmitStruct(string name, Dictionary<string, List<(string Name, string Type)>> structs, Dictionary<string, string> structTypes, HashSet<string> emitted, HashSet<string> visiting, StringBuilder sb)
    {
        if (emitted.Contains(name) || !visiting.Add(name))
            return;
        var fields = structs[name];
        var cFields = new List<string>();
        foreach (var (fname, ftype) in fields)
        {
            string ctype;
            if (ftype.StartsWith("struct:", StringComparison.Ordinal))
            {
                if (!structTypes.TryGetValue(ftype[7..], out var nested) || !structs.ContainsKey(nested))
                    return; // nested layout unknown: leave this struct out
                EmitStruct(nested, structs, structTypes, emitted, visiting, sb);
                if (!emitted.Contains(nested))
                    return;
                ctype = nested;
            }
            else
            {
                ctype = ftype;
            }
            cFields.Add($"{ctype} {fname};");
        }
        sb.Append("typedef struct ").Append(name).Append(" { ").AppendJoin(' ', cFields).Append(" } ").Append(name).AppendLine(";");
        emitted.Add(name);
    }

    static void EmitProtocol(string name, Dictionary<string, (string Name, List<string> Bases)> protocols, HashSet<string> emitted, HashSet<string> visiting, StringBuilder sb)
    {
        if (emitted.Contains(name) || !visiting.Add(name))
            return;
        var bases = protocols[name].Bases.Where(b => b != name && (b == "NSObject" || protocols.ContainsKey(b))).Distinct().ToList();
        foreach (var b in bases)
            if (b != "NSObject")
                EmitProtocol(b, protocols, emitted, visiting, sb);
        bases.RemoveAll(b => !emitted.Contains(b)); // drop inheritance cycles
        // The bindings' protocol interfaces don't restate NSObject, which nearly every
        // framework protocol extends; the registrar relies on it for -respondsToSelector: etc.
        if (bases.Count == 0 && name != "NSObject" && !RootProtocols.Contains(name))
            bases.Add("NSObject");
        sb.Append("@protocol ").Append(name);
        if (bases.Count > 0)
            sb.Append(" <").AppendJoin(", ", bases).Append('>');
        sb.AppendLine(" @end");
        emitted.Add(name);
    }

    static void EmitClass(string name, Dictionary<string, (string Name, string? Super, List<string> Protocols, int Arity)> classes, HashSet<string> emitted, StringBuilder sb)
    {
        if (!emitted.Add(name))
            return;
        var (_, super, protos, arity) = classes[name];
        if (super is not null && classes.ContainsKey(super))
            EmitClass(super, classes, emitted, sb);
        bool root = super is null || (!classes.ContainsKey(super) && !Predeclared.Contains(super));
        if (root)
            sb.Append("__attribute__((objc_root_class)) ");
        sb.Append("@interface ").Append(name);
        if (arity > 0)
            sb.Append('<').AppendJoin(", ", arity switch { 1 => ["ObjectType"], 2 => ["KeyType", "ObjectType"], _ => Enumerable.Range(0, arity).Select(i => "T" + i) }).Append('>');
        if (!root)
            sb.Append(" : ").Append(super);
        if (protos.Count > 0)
            sb.Append(" <").AppendJoin(", ", protos).Append('>');
        if (KnownMethods.TryGetValue(name, out var methods))
            sb.Append(' ').Append(methods);
        sb.AppendLine(" @end");
    }

    static bool IsBindingNamespace(string ns) =>
        ns.Length > 0 && Identifier.IsMatch(ns) && ns is not ("ObjCRuntime" or "Microsoft" or "System") && !ns.StartsWith('<');

    // The registrar spells a struct by its [NativeName], else its .NET name.
    static string StructName(TypeInfo t) => t.NativeName ?? t.FullName[(t.FullName.LastIndexOfAny(['.', '/']) + 1)..];

    // ObjC lightweight generics (NSSet<UIPress *> *): the bindings pair each generic
    // collection with a generic managed subclass, Foundation.NSSet`1 for NSSet.
    static int GenericArity(TypeInfo t, Dictionary<string, TypeInfo> types)
    {
        for (int arity = 1; arity <= 4; arity++)
            if (types.TryGetValue($"{t.FullName}`{arity}", out var g) && g.BaseFullName == t.FullName)
                return arity;
        return 0;
    }

    static string? RegisteredBase(TypeInfo t, Dictionary<string, TypeInfo> types)
    {
        for (var b = t.BaseFullName; b is not null && types.TryGetValue(b, out var bt); b = bt.BaseFullName)
            if (bt.IsWrapper && bt.RegisterName is { } n && Identifier.IsMatch(n))
                return n;
        return null;
    }

    // The ObjC protocols a binding type adopts (or, for a protocol interface, extends).
    // C# metadata lists every inherited interface; ObjC lists only the direct ones, so
    // drop any protocol another one in the list already extends.
    static List<string> ProtocolsOf(TypeInfo t, Dictionary<string, TypeInfo> types)
    {
        var direct = new List<TypeInfo>();
        foreach (var i in t.Interfaces)
            if (types.TryGetValue(i, out var it) && it.ProtocolName is { } p && Identifier.IsMatch(p))
                direct.Add(it);
        return direct
            .Where(p => !direct.Any(q => q != p && q.Interfaces.Contains(p.FullName)))
            .Select(p => p.ProtocolName!)
            .ToList();
    }

    static void Read(string path, Dictionary<string, TypeInfo> types)
    {
        using var pe = new PEReader(File.OpenRead(path));
        if (!pe.HasMetadata)
            return;
        var md = pe.GetMetadataReader();
        var names = new NameProvider(md);
        var added = new List<TypeInfo>();

        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);
            string fullName = FullName(md, h);
            var info = new TypeInfo
            {
                FullName = fullName,
                Namespace = Namespace(md, td),
                IsInterface = (td.Attributes & System.Reflection.TypeAttributes.Interface) != 0,
                IsPublic = IsVisible(md, td),
            };
            if (!td.BaseType.IsNil)
            {
                info.BaseFullName = names.TypeName(td.BaseType);
                info.IsValueType = info.BaseFullName is "System.ValueType";
                info.IsEnum = info.BaseFullName is "System.Enum";
            }
            foreach (var ih in td.GetInterfaceImplementations())
                info.Interfaces.Add(names.TypeName(md.GetInterfaceImplementation(ih).Interface));

            foreach (var ch in td.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(ch);
                string attr = AttributeName(md, ca);
                if (attr is not ("Foundation.RegisterAttribute" or "Foundation.ProtocolAttribute" or "ObjCRuntime.NativeNameAttribute"))
                    continue;
                CustomAttributeValue<string> value;
                try { value = ca.DecodeValue(names); }
                catch (BadImageFormatException) { continue; }
                switch (attr)
                {
                    case "Foundation.RegisterAttribute":
                        info.RegisterName = value.FixedArguments.Length > 0 ? value.FixedArguments[0].Value as string : null;
                        info.IsWrapper = value.FixedArguments.Length > 1 && value.FixedArguments[1].Value is true;
                        foreach (var na in value.NamedArguments)
                        {
                            if (na.Name == "Name" && na.Value is string n) info.RegisterName = n;
                            if (na.Name == "IsWrapper" && na.Value is bool w) info.IsWrapper = w;
                        }
                        break;
                    case "Foundation.ProtocolAttribute":
                        foreach (var na in value.NamedArguments)
                        {
                            if (na.Name == "Name" && na.Value is string n)
                                info.ProtocolName = n;
                            if (na.Name == "IsInformal" && na.Value is true)
                                info.IsInformal = true;
                        }
                        break;
                    case "ObjCRuntime.NativeNameAttribute":
                        info.NativeName = value.FixedArguments.Length > 0 ? value.FixedArguments[0].Value as string : null;
                        break;
                }
            }
            // [Protocol] interfaces without an explicit Name use the interface name minus its 'I'.
            if (info.IsInterface && info.ProtocolName is null && HasAttribute(md, td, "Foundation.ProtocolAttribute"))
            {
                string simple = fullName[(fullName.LastIndexOfAny(['.', '/']) + 1)..];
                info.ProtocolName = simple.StartsWith('I') ? simple[1..] : simple;
            }
            if (info.IsValueType && (td.Attributes & System.Reflection.TypeAttributes.ExplicitLayout) == 0)
                info.Fields = StructFields(md, td, names);
            if (info.IsEnum)
                info.EnumUnderlying = StructFields(md, td, names).FirstOrDefault().Item2;

            if (types.TryAdd(fullName, info))
                added.Add(info);
        }
        bool binding = added.Any(t => t.RegisterName is not null || t.ProtocolName is not null);
        foreach (var t in added)
            t.FromBindingAssembly = binding;
    }

    // A struct's instance fields (name, .NET type); mapped to C by CType.
    static List<(string, string)> StructFields(MetadataReader md, TypeDefinition td, NameProvider names)
    {
        var fields = new List<(string, string)>();
        foreach (var fh in td.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            if ((f.Attributes & System.Reflection.FieldAttributes.Static) != 0)
                continue;
            string type = f.DecodeSignature(names, null);
            string name = md.GetString(f.Name);
            // Backing fields of auto-properties: <Name>k__BackingField
            if (name.StartsWith('<'))
                name = name[1..name.IndexOf('>')];
            if (!Identifier.IsMatch(name))
                name = "f" + fields.Count;
            fields.Add((name, type));
        }
        return fields;
    }

    // The C spelling of a struct field's .NET type, or null if it has none.
    static string? CType(string type, Dictionary<string, TypeInfo> types) => type switch
    {
        "System.Runtime.InteropServices.NFloat" => "CGFloat",
        "System.Boolean" => "bool",
        "System.Double" => "double",
        "System.Single" => "float",
        "System.SByte" => "int8_t",
        "System.Byte" => "uint8_t",
        "System.Int16" => "int16_t",
        "System.UInt16" => "uint16_t",
        "System.Int32" => "int32_t",
        "System.UInt32" => "uint32_t",
        "System.Int64" => "int64_t",
        "System.UInt64" => "uint64_t",
        "System.IntPtr" => "intptr_t",
        "System.UIntPtr" => "uintptr_t",
        _ when type.EndsWith('*') => "void *",
        _ when type.StartsWith("struct:", StringComparison.Ordinal) && types.TryGetValue(type[7..], out var t) && t.IsEnum && t.EnumUnderlying is { } u => CType(u, types),
        _ when type.StartsWith("struct:", StringComparison.Ordinal) => type,
        _ => null,
    };

    static bool IsVisible(MetadataReader md, TypeDefinition td)
    {
        var v = td.Attributes & System.Reflection.TypeAttributes.VisibilityMask;
        if (v == System.Reflection.TypeAttributes.Public)
            return true;
        return v == System.Reflection.TypeAttributes.NestedPublic && IsVisible(md, md.GetTypeDefinition(td.GetDeclaringType()));
    }

    static bool HasAttribute(MetadataReader md, TypeDefinition td, string name) =>
        td.GetCustomAttributes().Any(ch => AttributeName(md, md.GetCustomAttribute(ch)) == name);

    static string AttributeName(MetadataReader md, CustomAttribute ca)
    {
        EntityHandle type = ca.Constructor.Kind switch
        {
            HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent,
            HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType(),
            _ => default,
        };
        return type.Kind switch
        {
            HandleKind.TypeReference => RefName(md, (TypeReferenceHandle)type),
            HandleKind.TypeDefinition => FullName(md, (TypeDefinitionHandle)type),
            _ => "",
        };
    }

    static string Namespace(MetadataReader md, TypeDefinition td)
    {
        while (td.IsNested)
            td = md.GetTypeDefinition(td.GetDeclaringType());
        return md.GetString(td.Namespace);
    }

    internal static string FullName(MetadataReader md, TypeDefinitionHandle h)
    {
        var td = md.GetTypeDefinition(h);
        string name = md.GetString(td.Name);
        if (td.IsNested)
            return FullName(md, td.GetDeclaringType()) + "/" + name;
        string ns = md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    internal static string RefName(MetadataReader md, TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        string name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return RefName(md, (TypeReferenceHandle)tr.ResolutionScope) + "/" + name;
        string ns = md.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return;
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Names types by their full .NET name (generic instantiations by their definition), which
    /// is all the generator compares; struct-typed fields come back as <c>struct:&lt;name&gt;</c>.
    /// </summary>
    sealed class NameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>, ICustomAttributeTypeProvider<string>
    {
        public string TypeName(EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => FullName(md, (TypeDefinitionHandle)h),
            HandleKind.TypeReference => RefName(md, (TypeReferenceHandle)h),
            HandleKind.TypeSpecification => Strip(md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(this, null)),
            _ => "",
        };

        static string Strip(string s) => s.StartsWith("struct:", StringComparison.Ordinal) ? s[7..] : s;

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.Object => "System.Object",
            _ => typeCode.ToString(),
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            (rawTypeKind == (byte)SignatureTypeKind.ValueType ? "struct:" : "") + FullName(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            string name = RefName(reader, handle);
            // NFloat, IntPtr etc. are primitives as far as layout goes.
            return rawTypeKind == (byte)SignatureTypeKind.ValueType && !name.StartsWith("System.", StringComparison.Ordinal) ? "struct:" + name : name;
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr*";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetSystemType() => "System.Type";
        public bool IsSystemType(string type) => type == "System.Type";
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    }
}
