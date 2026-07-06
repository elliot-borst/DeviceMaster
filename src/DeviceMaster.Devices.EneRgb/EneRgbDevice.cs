using System.Text;

namespace DeviceMaster.Devices.EneRgb;

/// <summary>
/// One ENE RGB controller (Aura-compatible) behind an SMBus/I2C transport — the chip used on
/// RGB DRAM (Klevv/GSkill/Geil/…) and ASUS GPUs. Ported from OpenRGB's ENESMBusController.
///
/// ENE registers are 16-bit and tunnelled over SMBus: command 0x00 sets the register pointer
/// (word, high byte on the wire first), 0x81 reads a data byte, 0x01 writes one, 0x03 writes
/// a block (max 3 bytes). Colors are stored per LED as R,B,G — not RGB.
///
/// Safety: this class only ever addresses the ENE controller the caller confirmed via
/// <see cref="Fingerprint"/> — SPD EEPROM addresses (0x50–0x57) are never written.
/// </summary>
public sealed class EneRgbDevice
{
    // register map (OpenRGB ENESMBusController.h)
    private const ushort RegDeviceName = 0x1000;
    private const ushort RegMicronCheck = 0x1030;
    private const ushort RegConfigTable = 0x1C00;
    private const ushort RegDirectSelect = 0x8020;
    private const ushort RegMode = 0x8021;
    private const ushort RegSpeed = 0x8022;
    private const ushort RegDirection = 0x8023;
    private const ushort RegApply = 0x80A0;
    public const ushort RegSlotIndex = 0x80F8;
    public const ushort RegI2cAddress = 0x80F9;

    private const byte ApplyValue = 0x01;
    private const byte SaveValue = 0xAA;
    private const byte ModeStatic = 0x01;

    private readonly ISmBus _bus;
    private readonly byte _address;

    public EneRgbDevice(ISmBus bus, byte address)
    {
        _bus = bus;
        _address = address;
    }

    public byte Address => _address;
    public string BusName => _bus.Name;
    public string Version { get; private set; } = "?";
    public int LedCount { get; private set; }

    /// <summary>
    /// Read-only ENE fingerprint: plain SMBus registers 0xA0..0xAF must read back 0x00..0x0F.
    /// Run this before ANY write to an address — including the 0x77 remap — so devices that
    /// merely answer a probe are never written to. (Stricter than the reference, deliberately.)
    /// </summary>
    public static bool Fingerprint(ISmBus bus, byte address)
    {
        for (byte i = 0; i < 16; i++)
        {
            if (bus.ReadByteData(address, (byte)(0xA0 + i)) != i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads identity + config. Returns false for Micron-firmware chips (incompatible per the
    /// reference) or when the config table is unreadable.
    /// </summary>
    public bool Initialize()
    {
        if (ReadString(RegMicronCheck, 6) == "Micron")
        {
            return false;
        }

        Version = ReadString(RegDeviceName, 16);

        // register generation + LED-count offset by firmware family (OpenRGB constructor):
        // DRAM v1 ("DIMM_LED-0102") -> colors at 0x8000/0x8010, count at config[0x02]
        // DRAM v2 ("AUDA0-...")     -> colors at 0x8100/0x8160, count at config[0x02]
        // ASUS GPU ("AUMA0-E6K5-11xx") -> v2 registers, count at config[0x03]
        var v2 = Version.StartsWith("AUDA", StringComparison.Ordinal)
            || Version.StartsWith("AUMA", StringComparison.Ordinal);
        _directBase = v2 ? (ushort)0x8100 : (ushort)0x8000;
        _effectBase = v2 ? (ushort)0x8160 : (ushort)0x8010;
        var countOffset = Version.StartsWith("AUMA0-E6K5-11", StringComparison.Ordinal) ? 0x03 : 0x02;

        var count = ReadRegister((ushort)(RegConfigTable + countOffset));
        if (count <= 0)
        {
            return false;
        }

        LedCount = Math.Min(count, 120);
        return true;
    }

    private ushort _directBase = 0x8000;
    private ushort _effectBase = 0x8010;

    /// <summary>
    /// Saves the currently applied configuration to the controller's flash so it survives a
    /// power cycle. Slow and endurance-limited — call once a color has settled, not per change.
    /// </summary>
    public void Persist() => WriteRegister(RegApply, SaveValue);

    /// <summary>
    /// Static color on every LED through the effect engine, then apply — and optionally save
    /// to the controller's flash so it survives a power cycle (once per color change only;
    /// flash has limited write endurance).
    /// </summary>
    public void ApplyStaticColor(byte r, byte g, byte b, bool persist)
    {
        WriteRegister(RegDirectSelect, 0x00);
        WriteRegister(RegMode, ModeStatic);
        WriteRegister(RegSpeed, 0x02);
        WriteRegister(RegDirection, 0x00);
        WriteRegister(RegApply, ApplyValue);

        for (var led = 0; led < LedCount; led++)
        {
            // on-wire per-LED order is R, B, G
            WriteRegisterBlock((ushort)(_effectBase + 3 * led), [r, b, g]);
        }

        WriteRegister(RegApply, ApplyValue);
        if (persist)
        {
            WriteRegister(RegApply, SaveValue);
        }
    }

    /// <summary>Remaps the controller listening at this address to a new SMBus address (RAM enumeration).</summary>
    public void RemapAddress(byte slot, byte newAddress)
    {
        WriteRegister(RegSlotIndex, slot);
        WriteRegister(RegI2cAddress, (byte)(newAddress << 1));
    }

    private string ReadString(ushort register, int length)
    {
        var buffer = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var value = ReadRegister((ushort)(register + i));
            if (value < 0)
            {
                return "";
            }

            buffer[i] = (byte)value;
        }

        var text = Encoding.ASCII.GetString(buffer);
        var zero = text.IndexOf('\0');
        return zero >= 0 ? text[..zero] : text;
    }

    // ---- ENE two-byte register addressing over SMBus ----

    private int ReadRegister(ushort register)
    {
        if (_bus.WriteWordData(_address, 0x00, Swap(register)) < 0)
        {
            return -1;
        }

        return _bus.ReadByteData(_address, 0x81);
    }

    private void WriteRegister(ushort register, byte value)
    {
        Check(_bus.WriteWordData(_address, 0x00, Swap(register)), register);
        Check(_bus.WriteByteData(_address, 0x01, value), register);
    }

    private void WriteRegisterBlock(ushort register, byte[] data)
    {
        Check(_bus.WriteWordData(_address, 0x00, Swap(register)), register);
        if (_bus.WriteBlockData(_address, 0x03, data) < 0)
        {
            foreach (var value in data)
            {
                Check(_bus.WriteByteData(_address, 0x01, value), register);
            }
        }
    }

    /// <summary>Register pointer goes over the wire high byte first (reference byte-swaps the word).</summary>
    private static ushort Swap(ushort register) => (ushort)((register << 8) | (register >> 8));

    private void Check(int result, ushort register)
    {
        if (result < 0)
        {
            throw new IOException($"SMBus write to ENE 0x{_address:X2} reg 0x{register:X4} failed on {_bus.Name}.");
        }
    }
}
