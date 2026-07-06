using System.Runtime.InteropServices;
using System.Text;

namespace DeviceMaster.Devices.Nvidia;

/// <summary>
/// Minimal NvAPI binding for GPU identification and the GPU I2C bus (where board-partner RGB
/// controllers live). nvapi64.dll exports a single symbol — nvapi_QueryInterface — and every
/// real function is fetched by its 32-bit interface id (ids ported from OpenRGB's nvapi.cpp).
/// User-mode only: goes through the signed NVIDIA driver, no elevation required.
/// </summary>
public static class NvApi
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGetPciIdentifiers = 0x2DDFB66E;
    private const uint IdGetFullName = 0xCEEE8E9F;
    private const uint IdI2cWriteEx = 0x283AC65A;
    private const uint IdI2cReadEx = 0x4D7B0709;

    private const int MaxPhysicalGpus = 64;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    private delegate int InitializeDelegate();
    private delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] handles, out int count);
    private delegate int GetPciIdentifiersDelegate(IntPtr handle, out uint deviceId, out uint subSystemId, out uint revisionId, out uint extDeviceId);
    private delegate int GetFullNameDelegate(IntPtr handle, [Out] byte[] name);
    private delegate int I2cTransferDelegate(IntPtr handle, ref NvI2cInfoV3 info, out uint unknown);

    private static T? Get<T>(uint id) where T : Delegate
    {
        var pointer = QueryInterface(id);
        return pointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static bool _initialized;

    public static bool TryInitialize()
    {
        if (_initialized)
        {
            return true;
        }

        try
        {
            var initialize = Get<InitializeDelegate>(IdInitialize);
            _initialized = initialize is not null && initialize() == 0;
        }
        catch (DllNotFoundException)
        {
            // no NVIDIA driver installed
        }

        return _initialized;
    }

    public sealed record NvGpu(IntPtr Handle, string Name, ushort Vendor, ushort Device, ushort SubVendor, ushort SubDevice);

    /// <summary>Enumerates physical NVIDIA GPUs with their PCI identity (board partner = SubVendor).</summary>
    public static IReadOnlyList<NvGpu> EnumerateGpus()
    {
        if (!TryInitialize())
        {
            return [];
        }

        var enumerate = Get<EnumPhysicalGpusDelegate>(IdEnumPhysicalGpus);
        var identify = Get<GetPciIdentifiersDelegate>(IdGetPciIdentifiers);
        var fullName = Get<GetFullNameDelegate>(IdGetFullName);
        if (enumerate is null || identify is null)
        {
            return [];
        }

        var handles = new IntPtr[MaxPhysicalGpus];
        if (enumerate(handles, out var count) != 0)
        {
            return [];
        }

        var gpus = new List<NvGpu>();
        for (var i = 0; i < count; i++)
        {
            if (identify(handles[i], out var deviceId, out var subSystemId, out _, out _) != 0)
            {
                continue;
            }

            var name = "NVIDIA GPU";
            if (fullName is not null)
            {
                var buffer = new byte[64];
                if (fullName(handles[i], buffer) == 0)
                {
                    name = Encoding.ASCII.GetString(buffer).TrimEnd('\0');
                }
            }

            gpus.Add(new NvGpu(
                handles[i],
                name,
                Vendor: (ushort)(deviceId & 0xFFFF),
                Device: (ushort)(deviceId >> 16),
                SubVendor: (ushort)(subSystemId & 0xFFFF),
                SubDevice: (ushort)(subSystemId >> 16)));
        }

        return gpus;
    }

    /// <summary>NV_I2C_INFO_V3 — layout must match nvapi.h exactly (x64 natural alignment, 64 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NvI2cInfoV3
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte DevAddress;      // 7-bit address << 1
        public IntPtr RegAddress;
        public uint RegAddrSize;
        public IntPtr Data;
        public uint Size;
        public uint Speed;           // deprecated — 0xFFFF
        public uint SpeedKhz;        // NVAPI_I2C_SPEED_DEFAULT = 0xFFFF
        public byte PortId;          // RGB controllers hang off GPU I2C port 1
        public uint IsPortIdSet;

        public static uint MakeVersion() => (3u << 16) | (uint)Marshal.SizeOf<NvI2cInfoV3>();
    }

    internal static int I2cTransfer(IntPtr gpuHandle, bool write, byte devAddress7Bit, byte[]? register, byte[] data)
    {
        var transfer = Get<I2cTransferDelegate>(write ? IdI2cWriteEx : IdI2cReadEx);
        if (transfer is null)
        {
            return -1;
        }

        var regHandle = register is { Length: > 0 } ? GCHandle.Alloc(register, GCHandleType.Pinned) : default;
        var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var info = new NvI2cInfoV3
            {
                Version = NvI2cInfoV3.MakeVersion(),
                DisplayMask = 0,
                IsDdcPort = 0,
                DevAddress = (byte)(devAddress7Bit << 1),
                RegAddress = register is { Length: > 0 } ? regHandle.AddrOfPinnedObject() : IntPtr.Zero,
                RegAddrSize = (uint)(register?.Length ?? 0),
                Data = dataHandle.AddrOfPinnedObject(),
                Size = (uint)data.Length,
                Speed = 0xFFFF,          // deprecated field, reference sets 0xFFFF
                SpeedKhz = 0,            // NVAPI_I2C_SPEED_DEFAULT (first enum member)
                PortId = 1,
                IsPortIdSet = 1,
            };

            var status = transfer(gpuHandle, ref info, out _);
            return status == 0 ? 0 : -status;
        }
        finally
        {
            if (regHandle.IsAllocated)
            {
                regHandle.Free();
            }

            dataHandle.Free();
        }
    }
}
