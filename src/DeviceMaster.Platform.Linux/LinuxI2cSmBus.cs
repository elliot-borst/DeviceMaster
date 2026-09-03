using DeviceMaster.Devices.EneRgb;
using DeviceMaster.Platform.Linux;

namespace DeviceMaster.Platform.Linux;

/// <summary>
/// <see cref="ISmBus"/> over a raw /dev/i2c-N node (i2c-dev, I2C_RDWR only — the nvidia driver
/// rejects the SMBus ioctls). Wire encodings follow the SMBus semantics the ENE logic expects,
/// delivered as raw i2c messages with repeated starts, exactly as OpenRGB's i2c_smbus_linux
/// fallback does. Returns negative values on failure (the ISmBus contract).
/// </summary>
public sealed class LinuxI2cSmBus : ISmBus, IDisposable
{
    private readonly int _fd;

    public string Name { get; }

    public LinuxI2cSmBus(string devicePath)
    {
        _fd = I2cDev.OpenDevice(devicePath);
        if (_fd < 0)
        {
            throw new IOException($"Could not open {devicePath} (run with --privileged so the container sees /dev/i2c-*).");
        }

        Name = devicePath;
    }

    public int ReadByte(byte address)
    {
        var buffer = new byte[1];
        if (I2cDev.Run(_fd, [new I2cDev.Message((ushort)(address << 1), Write: false, buffer)]) < 0)
        {
            return -1;
        }

        return buffer[0];
    }

    public int ReadByteData(byte address, byte command)
    {
        var buffer = new byte[1];
        if (I2cDev.Run(_fd,
        [
            new I2cDev.Message((ushort)(address << 1), Write: true, [command]),
            new I2cDev.Message((ushort)(address << 1), Write: false, buffer),
        ]) < 0)
        {
            return -1;
        }

        return buffer[0];
    }

    public int WriteByteData(byte address, byte command, byte value)
    {
        return I2cDev.Run(_fd, [new I2cDev.Message((ushort)(address << 1), Write: true, [command, value])]);
    }

    public int WriteWordData(byte address, byte command, ushort value)
    {
        // SMBus word data is little-endian on the wire (low byte first)
        return I2cDev.Run(_fd,
            [new I2cDev.Message((ushort)(address << 1), Write: true, [command, (byte)value, (byte)(value >> 8)])]);
    }

    public int WriteBlockData(byte address, byte command, byte[] data)
    {
        var payload = new byte[data.Length + 1];
        payload[0] = command;
        Buffer.BlockCopy(data, 0, payload, 1, data.Length);
        return I2cDev.Run(_fd, [new I2cDev.Message((ushort)(address << 1), Write: true, payload)]);
    }

    public void Dispose() => I2cDev.CloseDevice(_fd);
}
