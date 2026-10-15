using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AotAnywhere.AppleTools;

/// <summary>
/// A native symbol a binding assembly can make the app import, with the library the binding
/// names for it (an install path such as <c>/System/Library/Frameworks/UIKit.framework/UIKit</c>,
/// or null when it only implies one).
/// </summary>
public sealed record BindingSymbol(string Symbol, string? Library);

/// <summary>
/// The Apple symbols a platform binding assembly (Microsoft.iOS.dll) can reference from the
/// app: P/Invoke entry points (NativeAOT binds them directly, so they become link-time
/// imports), <c>[Field]</c> globals, and the class/metaclass symbols of every wrapper class (the
/// generated registrar subclasses and refers to them). Read from the MIT-licensed bindings'
/// metadata only.
/// </summary>
public static class BindingSymbols
{
    public static List<BindingSymbol> Read(string assemblyPath)
    {
        using var pe = new PEReader(File.OpenRead(assemblyPath));
        var md = pe.GetMetadataReader();
        var provider = new AttributeTypes();
        var result = new List<BindingSymbol>();

        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);

            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var import = m.GetImport();
                if (import.Module.IsNil)
                    continue;
                string library = md.GetString(md.GetModuleReference(import.Module).Name);
                string entry = md.GetString(import.Name);
                if (entry.Length == 0)
                    entry = md.GetString(m.Name);
                if (LibraryPath(library) is { } path)
                    result.Add(new BindingSymbol("_" + entry, path));
            }

            var fieldAttributes = td.GetFields().SelectMany(fh => md.GetFieldDefinition(fh).GetCustomAttributes())
                .Concat(td.GetProperties().SelectMany(ph => md.GetPropertyDefinition(ph).GetCustomAttributes()));
            foreach (var ch in fieldAttributes)
            {
                var ca = md.GetCustomAttribute(ch);
                if (AttributeName(md, ca) != "Foundation.FieldAttribute")
                    continue;
                var value = ca.DecodeValue(provider);
                if (value.FixedArguments.Length == 0 || value.FixedArguments[0].Value is not string symbol)
                    continue;
                string? library = value.FixedArguments.Length > 1 ? value.FixedArguments[1].Value as string : null;
                // [Field("X")] without a library: the binding's own framework, resolved by the generator.
                if (library is null || LibraryPath(library) is { } path)
                    result.Add(new BindingSymbol("_" + symbol, library is null ? null : LibraryPath(library)));
            }

            foreach (var ch in td.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(ch);
                if (AttributeName(md, ca) != "Foundation.RegisterAttribute")
                    continue;
                var value = ca.DecodeValue(provider);
                if (value.FixedArguments is [{ Value: string name }, { Value: true }, ..])
                {
                    string? library = FrameworkPath(NamespaceOf(md, td));
                    result.Add(new BindingSymbol("_OBJC_CLASS_$_" + name, library));
                    result.Add(new BindingSymbol("_OBJC_METACLASS_$_" + name, library));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The install path a binding's library name refers to: paths as given, framework names
    /// (<c>[Field("X", "UIKit")]</c>) as the framework binary, and null for the app itself
    /// (<c>__Internal</c>) or names that aren't Apple libraries.
    /// </summary>
    public static string? LibraryPath(string library)
    {
        if (library.StartsWith('/'))
            return library;
        if (library is "__Internal" or "" || library.Contains('.') || library.Contains('/'))
            return null;
        return FrameworkPath(library);
    }

    // The binding namespaces whose framework is named differently.
    static string? FrameworkPath(string ns)
    {
        if (ns.Length == 0)
            return null;
        string framework = ns switch
        {
            "CoreAnimation" => "QuartzCore",
            "ObjCRuntime" => "",
            _ => ns,
        };
        return framework.Length == 0 ? null : $"/System/Library/Frameworks/{framework}.framework/{framework}";
    }

    static string NamespaceOf(MetadataReader md, TypeDefinition td)
    {
        while (td.IsNested)
            td = md.GetTypeDefinition(td.GetDeclaringType());
        return md.GetString(td.Namespace);
    }

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
            HandleKind.TypeReference => ObjCShims.RefName(md, (TypeReferenceHandle)type),
            HandleKind.TypeDefinition => ObjCShims.FullName(md, (TypeDefinitionHandle)type),
            _ => "",
        };
    }

    sealed class AttributeTypes : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => ObjCShims.FullName(reader, handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => ObjCShims.RefName(reader, handle);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
