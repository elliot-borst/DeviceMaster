using System.Runtime.InteropServices;

namespace DeviceMaster.Sensors.Linux;

/// <summary>
/// GPU fan RPM straight from the NVIDIA driver's NVML library (nvidia-smi only reports percent).
/// Read-only: init, device handle, fan count and <c>nvmlDeviceGetFanSpeedRPM</c>; nothing is set.
/// Null when the library, the GPU or the call (older drivers) is missing.
/// </summary>
public sealed class NvmlFans : IDisposable
{
    private const string Lib = "libnvidia-ml.so.1";
    private readonly IntPtr _device;
    private readonly uint _fans;
    private bool _disposed;

    private NvmlFans(IntPtr device, uint fans)
    {
        _device = device;
        _fans = fans;
    }

    /// <summary>The first GPU's fans, or null when NVML or a fan isn't there.</summary>
    public static NvmlFans? TryOpen()
    {
        try
        {
            if (nvmlInit_v2() != 0)
            {
                return null;
            }

            if (nvmlDeviceGetHandleByIndex_v2(0, out var device) != 0 || nvmlDeviceGetNumFans(device, out var fans) != 0 || fans == 0)
            {
                nvmlShutdown();
                return null;
            }

            return new NvmlFans(device, fans);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Average RPM across the card's fans (0 while its idle fan stop is on), or null.</summary>
    public int? ReadRpm()
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            var total = 0L;
            for (uint i = 0; i < _fans; i++)
            {
                var info = new FanSpeedInfo { Version = (uint)Marshal.SizeOf<FanSpeedInfo>() | (1u << 24), Fan = i };
                if (nvmlDeviceGetFanSpeedRPM(_device, ref info) != 0)
                {
                    return null;
                }

                total += info.Speed;
            }

            return (int)(total / _fans);
        }
        catch (EntryPointNotFoundException)
        {
            return null; // driver older than the RPM call
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            nvmlShutdown();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FanSpeedInfo
    {
        public uint Version;
        public uint Fan;
        public uint Speed;
    }

    [DllImport(Lib)] private static extern int nvmlInit_v2();
    [DllImport(Lib)] private static extern int nvmlShutdown();
    [DllImport(Lib)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Lib)] private static extern int nvmlDeviceGetNumFans(IntPtr device, out uint fans);
    [DllImport(Lib)] private static extern int nvmlDeviceGetFanSpeedRPM(IntPtr device, ref FanSpeedInfo info);
}
