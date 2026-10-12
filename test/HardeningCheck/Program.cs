using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Run natively on Windows by windows-hardening.yml.
//
//   HardeningCheck.exe            prints the live process mitigation state;
//                                 exits 0 only if the loader enabled CFG
//   HardeningCheck.exe --violate  makes an indirect call to the middle of a
//                                 function; with CFG enforced the process is
//                                 killed with STATUS_STACK_BUFFER_OVERRUN
//                                 (0xC0000409, FAST_FAIL_GUARD_ICALL_CHECK_FAILURE)
unsafe
{
    if (args is ["--violate"])
    {
        delegate* unmanaged<int> valid = &Target.Seven;
        if (valid() != 7)
            return 2;
        // Not a function start, so not in the CFG function table.
        var bogus = (delegate* unmanaged<int>)((byte*)valid + 1);
        Console.WriteLine("calling invalid target");
        Console.Out.Flush();
        bogus();
        Console.WriteLine("invalid indirect call was NOT stopped");
        return 3;
    }

    var cfg = Query(ProcessControlFlowGuardPolicy);
    var shadowStack = Query(ProcessUserShadowStackPolicy);
    var cfgEnabled = (cfg & 1) != 0;
    Console.WriteLine($"arch={RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine($"cfg.flags=0x{cfg:X8} EnableControlFlowGuard={cfgEnabled}");
    Console.WriteLine($"shadowstack.flags=0x{shadowStack:X8} EnableUserShadowStack={(shadowStack & 1) != 0}");
    return cfgEnabled ? 0 : 1;
}

static uint Query(int policy)
{
    uint flags = 0;
    unsafe
    {
        if (GetProcessMitigationPolicy(GetCurrentProcess(), policy, &flags, sizeof(uint)) == 0)
            return 0xFFFFFFFF;
    }
    return flags;
}

[DllImport("kernel32")]
static extern nint GetCurrentProcess();

[DllImport("kernel32")]
static extern unsafe int GetProcessMitigationPolicy(nint process, int policy, void* buffer, nuint length);

partial class Program
{
    const int ProcessControlFlowGuardPolicy = 7;
    const int ProcessUserShadowStackPolicy = 13;
}

static class Target
{
    [UnmanagedCallersOnly]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Seven() => 7;
}
