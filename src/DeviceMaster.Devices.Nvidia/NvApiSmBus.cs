using DeviceMaster.Devices.EneRgb;

namespace DeviceMaster.Devices.Nvidia;

/// <summary>
/// SMBus-style transactions over a GPU's I2C bus via NvAPI (port of OpenRGB's
/// i2c_smbus_nvapi). Board-partner RGB controllers sit on GPU I2C port 1.
/// </summary>
public sealed class NvApiSmBus(NvApi.NvGpu gpu) : ISmBus
{
    public string Name { get; } = $"NvAPI I2C ({gpu.Name})";

    public int ReadByte(byte address)
    {
        var data = new byte[1];
        var result = NvApi.I2cTransfer(gpu.Handle, write: false, address, register: null, data);
        return result < 0 ? result : data[0];
    }

    public int ReadByteData(byte address, byte command)
    {
        var data = new byte[1];
        var result = NvApi.I2cTransfer(gpu.Handle, write: false, address, [command], data);
        return result < 0 ? result : data[0];
    }

    public int WriteByteData(byte address, byte command, byte value) =>
        NvApi.I2cTransfer(gpu.Handle, write: true, address, [command], [value]);

    public int WriteWordData(byte address, byte command, ushort value) =>
        NvApi.I2cTransfer(gpu.Handle, write: true, address, [command], [(byte)(value & 0xFF), (byte)(value >> 8)]);

    public int WriteBlockData(byte address, byte command, byte[] data) =>
        // SMBus block protocol: the byte count travels first (the PIIX4 hardware adds this
        // itself; on the NvAPI raw-I2C path it must be part of the payload)
        NvApi.I2cTransfer(gpu.Handle, write: true, address, [command], [(byte)data.Length, .. data]);
}
