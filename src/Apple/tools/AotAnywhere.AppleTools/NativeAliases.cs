using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Off a Mac, points the workload's P/Invokes into macOS's C library - <c>[DllImport
/// ("/usr/lib/libc.dylib")]</c>, <c>"/usr/lib/libSystem.dylib"</c>, and on Windows <c>"libc"</c> -
/// at a stand-in: the task and trimmer code that makes them (realpath, stat, copyfile, ...) also
/// runs off a Mac. The library is loaded when .NET's own lookup fails, through every assembly
/// load context's <see cref="AssemblyLoadContext.ResolvingUnmanagedDll"/>.
/// <list type="bullet">
/// <item>In MSBuild, the setup task calls <see cref="Install"/> (for the workload's tasks).</item>
/// <item>In child dotnet processes (the trimmer), <c>StartupHook</c> does, through
/// <c>DOTNET_STARTUP_HOOKS</c>.</item>
/// </list>
/// </summary>
public static class NativeAliases
{
    /// <summary>The environment variable naming the stand-in library.</summary>
    public const string ShimVariable = "AOTANYWHERE_DARWIN_SHIM";

    static readonly HashSet<AssemblyLoadContext> attached = [];
    static string? shim;
    static IntPtr handle;

    /// <summary>The library names redirected.</summary>
    public static bool IsDarwinLibrary(string name) => name is "/usr/lib/libc.dylib" or "/usr/lib/libSystem.dylib" or "/usr/lib/libSystem.B.dylib"
        || (OperatingSystem.IsWindows() && name is "libc" or "libSystem");

    /// <summary>Redirects the macOS C library to <paramref name="library"/> in every load context of this process.</summary>
    public static void Install(string library)
    {
        lock (attached)
        {
            if (shim is not null)
                return;
            shim = library;
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Attach(AssemblyLoadContext.GetLoadContext(e.LoadedAssembly));
            foreach (var context in AssemblyLoadContext.All)
                Attach(context);
        }
    }

    static void Attach(AssemblyLoadContext? context)
    {
        if (context is null)
            return;
        lock (attached)
        {
            if (attached.Add(context))
                context.ResolvingUnmanagedDll += Resolve;
        }
    }

    static IntPtr Resolve(Assembly assembly, string name)
    {
        if (shim is null || !IsDarwinLibrary(name))
            return IntPtr.Zero;
        lock (attached)
        {
            if (handle == IntPtr.Zero)
                handle = NativeLibrary.Load(shim);
            return handle;
        }
    }
}
