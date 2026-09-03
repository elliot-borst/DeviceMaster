using System.Runtime.InteropServices;

namespace DeviceMaster.App.Headless;

/// <summary>
/// SIGTERM/SIGINT trap via libc sigaction. The net9.0-windows reference pack does not expose the
/// POSIX-only <c>PosixSignalRegistration</c> API, but this executable only ever runs on Linux,
/// so a direct libc call is the right tool. The handler flips a flag; the 1 Hz loop and the main
/// thread both observe it (stop latency &lt;= 1 s). No libc calls happen inside the handler itself.
/// </summary>
internal static class SignalWatcher
{
    private const int SigInt = 2;
    private const int SigTerm = 15;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SignalHandler(int signum);

    // full Linux x86_64 sigaction (32 bytes); zeroed so sa_flags/sa_mask are sane
    [StructLayout(LayoutKind.Sequential)]
    private struct SigAction
    {
        public IntPtr Handler;   // sa_handler
        public IntPtr Mask;      // sa_mask
        public IntPtr Flags;     // sa_flags
        public IntPtr Restorer;  // sa_restorer
    }

    [DllImport("libc.so.6", EntryPoint = "sigaction")]
    private static extern int SysSigAction(int signum, ref SigAction action, IntPtr oldAction);

    public static volatile bool StopRequested;

    private static GCHandle _handlerHandle;

    public static void Install()
    {
        SignalHandler handler = OnSignal; // explicit delegate type — no method-group conversion ambiguity
        _handlerHandle = GCHandle.Alloc(handler);
        var pointer = Marshal.GetFunctionPointerForDelegate(handler);

        var term = new SigAction { Handler = pointer };
        SysSigAction(SigTerm, ref term, IntPtr.Zero);

        var intr = new SigAction { Handler = pointer };
        SysSigAction(SigInt, ref intr, IntPtr.Zero);
    }

    private static void OnSignal(int signum)
    {
        StopRequested = true;
    }
}
