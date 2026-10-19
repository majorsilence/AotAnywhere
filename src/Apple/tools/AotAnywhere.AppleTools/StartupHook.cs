// .NET startup hook (DOTNET_STARTUP_HOOKS), set for the build off a Mac: child dotnet processes
// the workload starts, such as the trimmer and its custom steps, get the native library
// redirect too (see AotAnywhere.AppleTools.NativeAliases). The type must be in the global namespace.
internal static class StartupHook
{
    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable(AotAnywhere.AppleTools.NativeAliases.ShimVariable) is { Length: > 0 } shim)
            AotAnywhere.AppleTools.NativeAliases.Install(shim);
    }
}
