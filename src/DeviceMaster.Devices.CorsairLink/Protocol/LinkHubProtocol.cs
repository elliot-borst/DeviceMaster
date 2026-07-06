namespace DeviceMaster.Devices.CorsairLink.Protocol;

/// <summary>
/// Byte-level constants of the Corsair iCUE LINK System Hub protocol.
/// Ported from EvanMulawski/FanControl.CorsairLink (MIT).
/// </summary>
public static class LinkHubProtocol
{
    /// <summary>Response/read packet size. Writes carry one extra leading HID report-id byte.</summary>
    public const int PacketSize = 512;

    public const int PacketSizeOut = PacketSize + 1;

    public const byte HandleId = 0x01;

    public static class Commands
    {
        public static ReadOnlySpan<byte> EnterSoftwareMode => new byte[] { 0x01, 0x03, 0x00, 0x02 };
        public static ReadOnlySpan<byte> EnterHardwareMode => new byte[] { 0x01, 0x03, 0x00, 0x01 };
        public static ReadOnlySpan<byte> ReadFirmwareVersion => new byte[] { 0x02, 0x13 };
        public static ReadOnlySpan<byte> OpenEndpoint => new byte[] { 0x0d, HandleId };
        public static ReadOnlySpan<byte> CloseEndpoint => new byte[] { 0x05, 0x01, HandleId };
        public static ReadOnlySpan<byte> Read => new byte[] { 0x08, HandleId };
        public static ReadOnlySpan<byte> Write => new byte[] { 0x06, HandleId };
    }

    public static class Endpoints
    {
        public static ReadOnlySpan<byte> GetSpeeds => new byte[] { 0x17 };
        public static ReadOnlySpan<byte> GetTemperatures => new byte[] { 0x21 };
        public static ReadOnlySpan<byte> SoftwareSpeedFixedPercent => new byte[] { 0x18 };
        public static ReadOnlySpan<byte> GetSubDevices => new byte[] { 0x36 };
    }

    public static class DataTypes
    {
        public static ReadOnlySpan<byte> Speeds => new byte[] { 0x25, 0x00 };
        public static ReadOnlySpan<byte> Temperatures => new byte[] { 0x10, 0x00 };
        public static ReadOnlySpan<byte> SoftwareSpeedFixedPercent => new byte[] { 0x07, 0x00 };
        public static ReadOnlySpan<byte> SubDevices => new byte[] { 0x21, 0x00 };
    }

    public static class ResponseStatus
    {
        public const byte Ok = 0x00;

        /// <summary>The hub rejects endpoint operations while in the wrong (hardware/software) mode.</summary>
        public const byte IncorrectModeError = 0x03;
    }
}
