/*
 * Windows stand-in for the #!/bin/sh tool wrappers (StuDev.AotAnywhere.Apple.targets).
 *
 * The iOS workload and the ILCompiler targets launch Xcode tools by bare name (xcrun, clang,
 * codesign, ...). On Windows, Process.Start and cmd.exe only find executables, so each tool
 * name is a copy of this launcher. It runs
 *
 *     "<dotnet host>" "<AotAnywhere.AppleTools.dll>" <own name> <original arguments>
 *
 * reading the two paths from aotanywhere-tool.cfg next to itself (UTF-8, one per line), and
 * passing its own command line on verbatim, so the caller's quoting reaches the tool unchanged.
 * Compiled with zig cc at build time.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>
#include <wchar.h>

static void fail(const char *message)
{
    DWORD written;
    HANDLE err = GetStdHandle(STD_ERROR_HANDLE);
    WriteFile(err, "aotanywhere-tool: ", 18, &written, NULL);
    WriteFile(err, message, (DWORD)strlen(message), &written, NULL);
    WriteFile(err, "\r\n", 2, &written, NULL);
    ExitProcess(1);
}

/* Copies the next line of s into out (without CR/LF); returns the rest, or NULL at the end. */
static wchar_t *next_line(wchar_t *s, wchar_t *out, size_t cap)
{
    size_t n = 0;
    while (*s && *s != L'\r' && *s != L'\n') {
        if (n + 1 < cap)
            out[n++] = *s;
        s++;
    }
    out[n] = 0;
    while (*s == L'\r' || *s == L'\n')
        s++;
    return s;
}

int main(void)
{
    static wchar_t self[32768], cfg[32768], text[65536], host[32768], dll[32768], name[512];
    static wchar_t command[65536];

    DWORD len = GetModuleFileNameW(NULL, self, 32768);
    if (len == 0 || len >= 32768)
        fail("cannot determine the launcher's own path");

    /* Tool name: the file name without ".exe". */
    wchar_t *base = wcsrchr(self, L'\\');
    base = base ? base + 1 : self;
    wcsncpy(name, base, 511);
    size_t n = wcslen(name);
    if (n > 4 && _wcsicmp(name + n - 4, L".exe") == 0)
        name[n - 4] = 0;

    /* aotanywhere-tool.cfg beside the executable. */
    wcscpy(cfg, self);
    wcscpy(cfg + (base - self), L"aotanywhere-tool.cfg");
    HANDLE file = CreateFileW(cfg, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE)
        fail("aotanywhere-tool.cfg not found next to the launcher");
    static char raw[65536];
    DWORD got = 0;
    if (!ReadFile(file, raw, sizeof raw - 1, &got, NULL))
        fail("cannot read aotanywhere-tool.cfg");
    CloseHandle(file);
    char *start = raw;
    if (got >= 3 && (unsigned char)raw[0] == 0xEF && (unsigned char)raw[1] == 0xBB && (unsigned char)raw[2] == 0xBF) {
        start += 3; /* UTF-8 BOM */
        got -= 3;
    }
    int wide = MultiByteToWideChar(CP_UTF8, 0, start, (int)got, text, 65535);
    text[wide] = 0;
    wchar_t *rest = next_line(text, host, 32768);
    next_line(rest, dll, 32768);
    if (!host[0] || !dll[0])
        fail("aotanywhere-tool.cfg must hold the dotnet host and the tool DLL paths");

    /* Our own arguments: the command line after argv[0], untouched. */
    wchar_t *args = GetCommandLineW();
    if (*args == L'"') {
        args++;
        while (*args && *args != L'"')
            args++;
        if (*args)
            args++;
    } else {
        while (*args && *args != L' ' && *args != L'\t')
            args++;
    }
    while (*args == L' ' || *args == L'\t')
        args++;

    if (wcslen(host) + wcslen(dll) + wcslen(name) + wcslen(args) + 16 >= 65536)
        fail("command line too long");
    wcscpy(command, L"\"");
    wcscat(command, host);
    wcscat(command, L"\" \"");
    wcscat(command, dll);
    wcscat(command, L"\" ");
    wcscat(command, name);
    if (*args) {
        wcscat(command, L" ");
        wcscat(command, args);
    }

    STARTUPINFOW si;
    ZeroMemory(&si, sizeof si);
    si.cb = sizeof si;
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    PROCESS_INFORMATION pi;
    if (!CreateProcessW(NULL, command, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi))
        fail("cannot start the dotnet host named in aotanywhere-tool.cfg");
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return (int)code;
}
