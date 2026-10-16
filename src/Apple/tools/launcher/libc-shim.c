/*
 * Windows stand-in for the "libc" the iOS workload's trimmer steps (dotnet-linker) P/Invoke.
 *
 * Workload versions before dotnet/macios added Windows guards to PathUtils.ResolveSymbolicLinks
 * and Target.GetRealPath call realpath() (and strerror() for its error message) through
 * [DllImport ("libc")] even on Windows. Built as libc.dll into the tool wrapper directory, which is
 * first on PATH for the build, so the trimmer process finds it. Windows has no symbolic links
 * to resolve here, so realpath() is the full path, as the newer guards do (Path.GetFullPath).
 * Compiled with zig cc at build time.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <string.h>

/* char *realpath(const char *path, char *resolved). Two callers, told apart by `resolved`:
 *  - Application.realpath (string, IntPtr.Zero) -> string: no buffer; the result is returned as an
 *    ANSI string allocated with CoTaskMemAlloc, which is what the .NET marshaller frees a returned
 *    string with on Windows;
 *  - PathUtils.realpath (string, buffer) -> IntPtr: a 4097-byte buffer, read back with
 *    Marshal.PtrToStringAuto - UTF-16 on Windows - so it is filled with a UTF-16 string.
 * On failure: NULL, and the error (2 = no such file) in GetLastError, which
 * [DllImport (SetLastError = true)] reads on Windows. */
__declspec(dllexport) char *realpath(const char *path, char *resolved)
{
    char full[4097];
    if (path == NULL) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return NULL;
    }
    DWORD n = GetFullPathNameA(path, sizeof full, full, NULL);
    if (n == 0 || n >= sizeof full)
        return NULL; /* GetLastError is already set */
    if (GetFileAttributesA(full) == INVALID_FILE_ATTRIBUTES) {
        SetLastError(ERROR_FILE_NOT_FOUND);
        return NULL;
    }
    if (resolved != NULL) {
        int chars = 4097 / sizeof(WCHAR);
        if (MultiByteToWideChar(CP_ACP, 0, full, -1, (WCHAR *)resolved, chars) == 0)
            return NULL;
        return resolved;
    }
    char *out = (char *)CoTaskMemAlloc(n + 1);
    if (out == NULL) {
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return NULL;
    }
    memcpy(out, full, n + 1);
    return out;
}

/* char *strerror(int errnum): only used to word a warning. */
__declspec(dllexport) char *strerror(int errnum)
{
    static char message[512];
    if (!FormatMessageA(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, (DWORD)errnum, 0, message, sizeof message, NULL))
        strcpy(message, "unknown error");
    return message;
}
