namespace DeviceMaster.Devices.EneRgb;

/// <summary>
/// Minimal SMBus transaction surface the ENE controller logic needs. Implementations:
/// the AMD FCH SMBus via RAMSPDToolkit (RAM sticks) and NVIDIA GPU I2C via NvAPI
/// (ASUS GPU RGB). Methods return negative values on failure, mirroring the Linux
/// i2c conventions the reference implementations use.
/// </summary>
public interface ISmBus
{
    /// <summary>Human-readable bus name for logs ("AMD SMBus #0", "NvAPI I2C RTX 5090").</summary>
    string Name { get; }

    /// <summary>Receive-byte probe (SMBus "read byte"); ≥ 0 when a device answers at <paramref name="address"/>.</summary>
    int ReadByte(byte address);

    /// <summary>SMBus "read byte data": one command byte, one data byte back.</summary>
    int ReadByteData(byte address, byte command);

    /// <summary>SMBus "write byte data".</summary>
    int WriteByteData(byte address, byte command, byte value);

    /// <summary>SMBus "write word data" (little-endian on the wire: low byte first).</summary>
    int WriteWordData(byte address, byte command, ushort value);

    /// <summary>SMBus "write block data". May be unsupported (return &lt; 0) — callers fall back to bytes.</summary>
    int WriteBlockData(byte address, byte command, byte[] data);
}
