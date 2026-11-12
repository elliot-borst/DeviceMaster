using System.Runtime.InteropServices;

namespace DeviceMaster.Devices.LianLi;

/// <summary>
/// Millisecond-accurate waits for RF pacing. <c>Thread.Sleep(2)</c> lasts ~15.6 ms at the default
/// Windows timer resolution, and Windows 11 ignores <c>timeBeginPeriod</c> while the app's window
/// is hidden or covered — which made SL V3 rainbow uploads take ~10 s with the window in view
/// and ~50 s without (#28). A high-resolution waitable timer is accurate either way. Not thread-safe:
/// one instance per sending thread. Falls back to <c>Thread.Sleep</c> where unavailable.
/// </summary>
public sealed class PreciseDelay : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    private IntPtr _timer;

    public PreciseDelay()
    {
        if (OperatingSystem.IsWindows())
        {
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        }
    }

    /// <summary>True when the high-resolution timer is in use (Windows 10 1803+).</summary>
    public bool IsHighResolution => _timer != IntPtr.Zero;

    public void Wait(double milliseconds)
    {
        if (_timer != IntPtr.Zero)
        {
            var due = -(long)(milliseconds * 10_000); // relative, 100 ns units
            if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(_timer, Infinite);
                return;
            }
        }

        Thread.Sleep((int)Math.Ceiling(milliseconds));
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero)
        {
            CloseHandle(_timer);
            _timer = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
