using System.Runtime.InteropServices;
using DeviceMaster.Devices.EneRgb;

namespace DeviceMaster.Platform.Linux;

/// <summary>
/// <see cref="ISmBus"/> over a /dev/i2c-N node using the kernel's SMBus ioctls (I2C_SLAVE +
/// I2C_SMBUS). This is the transport for chipset SMBus controllers (AMD PIIX4, Intel i801)
/// whose drivers are SMBus-only and reject the raw I2C_RDWR transfers <see cref="LinuxI2cSmBus"/>
/// uses for the GPU. Uses plain I2C_SLAVE — never I2C_SLAVE_FORCE — so the kernel refuses any
/// address a driver has bound (e.g. the SPD EEPROMs at 0x50–0x57 under ee1004/spd5118).
/// Returns negative values on failure (the ISmBus contract).
/// </summary>
public sealed class LinuxSmBus : ISmBus, IDisposable
{
    private const nuint I2cSlave = 0x0703;
    private const nuint I2cFuncs = 0x0705;
    private const nuint I2cSmbus = 0x0720;
    private const int O_RDWR = 0x0002;

    private const byte SmbusWrite = 0;
    private const byte SmbusRead = 1;
    private const uint SizeByte = 1;
    private const uint SizeByteData = 2;
    private const uint SizeWordData = 3;
    private const uint SizeBlockData = 5;

    private const ulong FuncSmbusWriteBlockData = 0x02000000;

    /// <summary>union i2c_smbus_data: I2C_SMBUS_BLOCK_MAX (32) + 2 bytes.</summary>
    private const int DataSize = 34;
    public const int BlockMax = 32;

    // mirrors struct i2c_smbus_ioctl_data: __u8 read_write; __u8 command; __u32 size; union i2c_smbus_data *data
    [StructLayout(LayoutKind.Sequential)]
    private struct SmbusIoctlData
    {
        public byte ReadWrite;
        public byte Command;
        public uint Size;
        public IntPtr Data;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, nuint request, IntPtr arg);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlFuncs(int fd, nuint request, out ulong funcs);

    private readonly int _fd;
    private readonly bool _blockWrites;
    private int _slave = -1;

    public string Name { get; }

    public LinuxSmBus(string devicePath, string? adapterName = null)
    {
        _fd = Open(devicePath, O_RDWR);
        if (_fd < 0)
        {
            throw new IOException($"Could not open {devicePath} (needs root / a privileged container).");
        }

        _blockWrites = IoctlFuncs(_fd, I2cFuncs, out var funcs) == 0 && (funcs & FuncSmbusWriteBlockData) != 0;
        Name = adapterName is { Length: > 0 } ? $"{devicePath} ({adapterName})" : devicePath;
    }

    public int ReadByte(byte address) => Transfer(address, SmbusRead, 0, SizeByte, null, out var data) < 0 ? -1 : data[0];

    public int ReadByteData(byte address, byte command) =>
        Transfer(address, SmbusRead, command, SizeByteData, null, out var data) < 0 ? -1 : data[0];

    public int WriteByteData(byte address, byte command, byte value) =>
        Transfer(address, SmbusWrite, command, SizeByteData, [value], out _);

    public int WriteWordData(byte address, byte command, ushort value) =>
        // union word is host-endian (little on x64/arm64); the kernel/adapter puts the low byte first on the wire
        Transfer(address, SmbusWrite, command, SizeWordData, [(byte)value, (byte)(value >> 8)], out _);

    public int WriteBlockData(byte address, byte command, byte[] data)
    {
        if (!_blockWrites || data.Length is 0 or > BlockMax)
        {
            return -1; // callers fall back to byte writes
        }

        var payload = new byte[data.Length + 1];
        payload[0] = (byte)data.Length; // block[0] = count
        Buffer.BlockCopy(data, 0, payload, 1, data.Length);
        return Transfer(address, SmbusWrite, command, SizeBlockData, payload, out _);
    }

    private int Transfer(byte address, byte readWrite, byte command, uint size, byte[]? payload, out byte[] result)
    {
        result = new byte[DataSize];
        if (!SelectSlave(address))
        {
            return -1;
        }

        var dataPtr = Marshal.AllocHGlobal(DataSize);
        var argPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SmbusIoctlData>());
        try
        {
            Marshal.Copy(new byte[DataSize], 0, dataPtr, DataSize);
            if (payload is not null)
            {
                Marshal.Copy(payload, 0, dataPtr, payload.Length);
            }

            Marshal.StructureToPtr(new SmbusIoctlData { ReadWrite = readWrite, Command = command, Size = size, Data = dataPtr }, argPtr, false);
            if (Ioctl(_fd, I2cSmbus, argPtr) < 0)
            {
                return -1;
            }

            Marshal.Copy(dataPtr, result, 0, DataSize);
            return 0;
        }
        catch
        {
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
            Marshal.FreeHGlobal(argPtr);
        }
    }

    private bool SelectSlave(byte address)
    {
        if (_slave == address)
        {
            return true;
        }

        if (address > 0x7F || Ioctl(_fd, I2cSlave, (IntPtr)address) < 0)
        {
            _slave = -1; // EBUSY: a kernel driver owns this address — leave it alone
            return false;
        }

        _slave = address;
        return true;
    }

    public void Dispose() => Close(_fd);

    /// <summary>
    /// Chipset SMBus adapters (the buses DIMMs sit on), found by sysfs adapter name:
    /// "SMBus PIIX4 adapter port N" (AMD FCH) and "SMBus I801 adapter" (Intel PCH).
    /// </summary>
    public static IReadOnlyList<(string DevicePath, string Name)> ChipsetAdapters(string sysfsRoot = "/sys/bus/i2c/devices")
    {
        var adapters = new List<(string, string)>();
        if (!Directory.Exists(sysfsRoot))
        {
            return adapters;
        }

        foreach (var dir in Directory.EnumerateDirectories(sysfsRoot, "i2c-*").Order(StringComparer.Ordinal))
        {
            string name;
            try
            {
                name = File.ReadAllText(Path.Combine(dir, "name")).Trim();
            }
            catch
            {
                continue;
            }

            if (IsChipsetSmbus(name))
            {
                adapters.Add(($"/dev/{Path.GetFileName(dir)}", name));
            }
        }

        return adapters;
    }

    /// <summary>True for AMD FCH (PIIX4) and Intel PCH (I801) SMBus adapter names.</summary>
    public static bool IsChipsetSmbus(string adapterName) =>
        adapterName.StartsWith("SMBus PIIX4 adapter", StringComparison.Ordinal)
        || adapterName.StartsWith("SMBus I801 adapter", StringComparison.Ordinal);
}
