using System.Buffers.Binary;

namespace DeviceMaster.Devices.LianLi;

/// <summary>A wireless device parsed from the RX dongle's 42-byte GetDev record.</summary>
public sealed record Slv3Device
{
    public required byte[] Mac { get; init; }
    public required byte[] MasterMac { get; init; }
    public required byte Channel { get; init; }
    public required byte RxType { get; init; }
    public required byte DeviceType { get; init; }
    public required byte FanCount { get; init; }
    public required byte[] FanTypes { get; init; }
    public required ushort[] FanRpms { get; init; }
    public required byte[] CurrentPwm { get; init; }
    public required byte CmdSeq { get; init; }
    public required byte ListIndex { get; init; }
    public byte? CoolantTempC { get; init; }
    public required byte[] EffectIndex { get; init; }

    public string MacText => Convert.ToHexString(Mac).ToLowerInvariant();

    public bool IsBoundTo(byte[] masterMac) => Mac.Length == 6 && MasterMac.AsSpan().SequenceEqual(masterMac);

    /// <summary>SL V3 fan-type bytes are 20–26 (LED models 20–23, LCD models 24–26).</summary>
    public bool IsSlv3Family => FanTypes.Any(t => t is >= 20 and <= 26);
}

/// <summary>
/// Pure packet builders/parsers for the Lian Li SL V3 wireless ecosystem, ported from
/// sgtaziz/lian-li-linux and cross-checked against phstudy/uni-wireless-sync (both MIT).
/// Control goes out through the TX dongle (0416:8040); telemetry comes from the RX (0416:8041).
/// No I/O here — fully unit-testable.
/// </summary>
public static class Slv3Protocol
{
    public const byte UsbCmdSendRf = 0x10;
    public const byte UsbCmdGetMac = 0x11;
    public const byte RfSelect = 0x12;
    public const byte RfPwmCmd = 0x10;
    public const byte RfMasterClock = 0x14;

    public const int UsbPacketSize = 64;
    public const int RfDataSize = 240;
    public const int RfChunkSize = 60;
    public const int RfChunks = RfDataSize / RfChunkSize;

    /// <summary>SL V3 firmware treats nonzero PWM below 14% as invalid — raise to the floor.</summary>
    public const int MinimumDutyPercent = 14;

    /// <summary>PWM byte value that puts a fan into hardware motherboard-sync mode.</summary>
    public const byte PwmMotherboardSync = 6;

    /// <summary>Master-channel probe order: 8 first, then even channels, then odd (reference behaviour).</summary>
    public static IEnumerable<byte> MasterChannelProbeOrder()
    {
        yield return 8;
        for (byte ch = 2; ch <= 38; ch += 2)
        {
            if (ch != 8) yield return ch;
        }

        for (byte ch = 1; ch <= 39; ch += 2)
        {
            yield return ch;
        }
    }

    /// <summary>TX command: query the dongle's master MAC on a given RF channel.</summary>
    public static byte[] BuildGetMasterMac(byte channel)
    {
        var packet = new byte[UsbPacketSize];
        packet[0] = UsbCmdGetMac;
        packet[1] = channel;
        return packet;
    }

    /// <summary>Parses the GetMac response: [0]=0x11, [1..7]=MAC (nonzero when valid), [11..13]=fw version BE.</summary>
    public static bool TryParseMasterMac(ReadOnlySpan<byte> response, int length, out byte[] mac, out ushort firmwareVersion)
    {
        mac = [];
        firmwareVersion = 0;
        if (length < 7 || response[0] != UsbCmdGetMac)
        {
            return false;
        }

        var candidate = response.Slice(1, 6).ToArray();
        if (candidate.All(b => b == 0))
        {
            return false;
        }

        mac = candidate;
        if (length >= 13)
        {
            firmwareVersion = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(11, 2));
        }

        return true;
    }

    /// <summary>
    /// RX command: request the device list. NOTE: dongle firmware 16 (SLV3 V1.6) ignores
    /// page count 1 entirely and only answers from 2 upward — callers should retry with
    /// increasing page counts (verified live 2026-07-06).
    /// </summary>
    public static byte[] BuildGetDeviceList(byte pageCount = 1)
    {
        var packet = new byte[UsbPacketSize];
        packet[0] = UsbCmdSendRf;
        packet[1] = pageCount;
        return packet;
    }

    /// <summary>
    /// 240-byte RF PWM payload: [0]=0x12, [1]=0x10, [2-7]=device MAC, [8-13]=master MAC,
    /// [14]=rx type, [15]=master channel, [16]=sequence index, [17-20]=4 PWM bytes.
    /// </summary>
    public static byte[] BuildPwmRfData(
        ReadOnlySpan<byte> deviceMac, ReadOnlySpan<byte> masterMac,
        byte rxType, byte masterChannel, byte sequenceIndex, ReadOnlySpan<byte> pwm)
    {
        if (deviceMac.Length != 6 || masterMac.Length != 6 || pwm.Length != 4)
        {
            throw new ArgumentException("deviceMac/masterMac must be 6 bytes and pwm 4 bytes.");
        }

        var data = new byte[RfDataSize];
        data[0] = RfSelect;
        data[1] = RfPwmCmd;
        deviceMac.CopyTo(data.AsSpan(2));
        masterMac.CopyTo(data.AsSpan(8));
        data[14] = rxType;
        data[15] = masterChannel;
        data[16] = sequenceIndex;
        pwm.CopyTo(data.AsSpan(17));
        return data;
    }

    /// <summary>
    /// 240-byte "master clock" heartbeat ([0]=0x12, [1]=0x14, [8-13]=master MAC). L-Connect sends
    /// this once per second; without it fan firmware drifts into an autonomous fallback.
    /// </summary>
    public static byte[] BuildMasterClockRfData(ReadOnlySpan<byte> masterMac)
    {
        var data = new byte[RfDataSize];
        data[0] = RfSelect;
        data[1] = RfMasterClock;
        masterMac.CopyTo(data.AsSpan(8));
        return data;
    }

    /// <summary>
    /// Splits a 240-byte RF payload into 4× 64-byte USB packets:
    /// [0]=0x10, [1]=chunk index, [2]=RF channel, [3]=rx type, [4..]=60-byte chunk.
    /// </summary>
    public static byte[][] ChunkRfData(ReadOnlySpan<byte> rfData, byte channel, byte rxType)
    {
        if (rfData.Length != RfDataSize)
        {
            throw new ArgumentException($"RF payload must be {RfDataSize} bytes.");
        }

        var packets = new byte[RfChunks][];
        for (byte i = 0; i < RfChunks; i++)
        {
            var packet = new byte[UsbPacketSize];
            packet[0] = UsbCmdSendRf;
            packet[1] = i;
            packet[2] = channel;
            packet[3] = rxType;
            rfData.Slice(i * RfChunkSize, RfChunkSize).CopyTo(packet.AsSpan(4));
            packets[i] = packet;
        }

        return packets;
    }

    /// <summary>
    /// Enforce firmware PWM rules: slots beyond the fan count are zeroed (except the AIO pump
    /// slot 3) and nonzero values below the 14% floor are raised to it.
    /// </summary>
    public static byte[] ApplyPwmConstraints(ReadOnlySpan<byte> pwm, byte fanCount, bool isAio)
    {
        var minPwm = (byte)(MinimumDutyPercent / 100.0 * 255.0);
        var result = pwm.ToArray();

        for (var i = 0; i < result.Length; i++)
        {
            var isPumpSlot = i == 3 && isAio;
            if (i >= fanCount && !isPumpSlot)
            {
                result[i] = 0;
                continue;
            }

            if (result[i] > 0 && result[i] < minPwm)
            {
                result[i] = minPwm;
            }
        }

        return result;
    }

    /// <summary>Converts a 0-100 duty percentage to the 0-255 PWM byte the firmware expects.</summary>
    public static byte DutyPercentToPwm(int dutyPercent) =>
        (byte)Math.Clamp((int)Math.Round(dutyPercent * 255.0 / 100.0), 0, 255);

    /// <summary>
    /// Parses the RX GetDev response: [0]=0x10, [1]=device count, [2,3]=motherboard PWM
    /// (high bit of [2] set = unavailable), then 42-byte device records from offset 4.
    /// Records with a bad validation marker ([41] != 0x1C) or the master itself (type 0xFF)
    /// are skipped. Returns the devices and the mobo PWM (0-255, null when unavailable).
    /// </summary>
    public static (IReadOnlyList<Slv3Device> Devices, int? MotherboardPwm) ParseDeviceList(
        ReadOnlySpan<byte> response, int length)
    {
        if (length < 4 || response[0] != UsbCmdSendRf)
        {
            return ([], null);
        }

        int? moboPwm = null;
        var indicator = response[2];
        if (indicator >> 7 == 0)
        {
            int offTime = indicator & 0x7F;
            int onTime = response[3];
            if (offTime + onTime > 0)
            {
                moboPwm = Math.Min(255 * onTime / (offTime + onTime), 255);
            }
        }

        var count = response[1];
        var devices = new List<Slv3Device>();
        if (count == 0 || count > 12)
        {
            return (devices, moboPwm);
        }

        var offset = 4;
        for (byte index = 0; index < count; index++, offset += 42)
        {
            if (offset + 42 > length)
            {
                break;
            }

            if (ParseDeviceRecord(response.Slice(offset, 42), index) is { } device)
            {
                devices.Add(device);
            }
        }

        return (devices, moboPwm);
    }

    /// <summary>
    /// 42-byte record: [0-5]=MAC, [6-11]=master MAC, [12]=channel, [13]=rx type,
    /// [14-17]=system time, [18]=device type, [19]=fan count, [20-23]=effect index,
    /// [24-27]=fan types (byte 27 doubles as coolant °C on AIOs), [28-35]=4× RPM u16 BE,
    /// [36-39]=current PWM, [40]=cmd sequence, [41]=0x1C marker.
    /// </summary>
    public static Slv3Device? ParseDeviceRecord(ReadOnlySpan<byte> record, byte listIndex)
    {
        if (record.Length < 42 || record[41] != 0x1C)
        {
            return null;
        }

        var deviceType = record[18];
        if (deviceType == 0xFF)
        {
            return null; // the master/dongle itself
        }

        var isAio = deviceType is 10 or 11;
        return new Slv3Device
        {
            Mac = record[..6].ToArray(),
            MasterMac = record.Slice(6, 6).ToArray(),
            Channel = record[12],
            RxType = record[13],
            DeviceType = deviceType,
            FanCount = Math.Min(record[19], (byte)4),
            EffectIndex = record.Slice(20, 4).ToArray(),
            FanTypes = record.Slice(24, 4).ToArray(),
            CoolantTempC = isAio && record[27] > 0 ? record[27] : null,
            FanRpms =
            [
                BinaryPrimitives.ReadUInt16BigEndian(record.Slice(28, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(record.Slice(30, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(record.Slice(32, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(record.Slice(34, 2)),
            ],
            CurrentPwm = record.Slice(36, 4).ToArray(),
            CmdSeq = record[40],
            ListIndex = listIndex,
        };
    }
}
