using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Safety;

namespace DeviceMaster.Devices.LianLi;

/// <summary>
/// Session to the SL V3 wireless ecosystem: TX dongle (all control output) + RX dongle
/// (telemetry). Ported from sgtaziz/lian-li-linux (MIT).
/// Safety model: SL V3 fan firmware reverts to its own default speed when PWM traffic stops
/// for more than ~1 s, so the fans fail safe on crash by design — but that also means fixed
/// duties must be re-sent continuously (<see cref="SendKeepalive"/> once per second).
/// </summary>
public sealed class Slv3Controller : IDisposable
{
    private const ushort TxPid = 0x8040;
    private const ushort RxPid = 0x8041;
    private const int MacProbeReadTimeoutMs = 200;
    private const int DeviceListReadTimeoutMs = 1000;

    private readonly WinUsbDevice _tx;
    private readonly WinUsbDevice _rx;
    private readonly Action<string>? _trace;

    private Slv3Controller(WinUsbDevice tx, WinUsbDevice rx, Action<string>? trace)
    {
        _tx = tx;
        _rx = rx;
        _trace = trace;
    }

    public byte[] MasterMac { get; private set; } = new byte[6];
    public byte MasterChannel { get; private set; } = 8;
    public ushort MasterFirmware { get; private set; }
    public IReadOnlyList<Slv3Device> Devices { get; private set; } = [];
    public int? MotherboardPwm { get; private set; }

    public string MasterMacText => Convert.ToHexString(MasterMac).ToLowerInvariant();

    public static Slv3Controller Open(Action<string>? trace = null)
    {
        var candidates = WinUsbDevice.Enumerate(WinUsbDevice.Slv3InterfaceGuid);

        var txInfo = candidates.FirstOrDefault(c => c.UsbId.Pid == TxPid);
        var rxInfo = candidates.FirstOrDefault(c => c.UsbId.Pid == RxPid);
        if (txInfo.Path is null || rxInfo.Path is null)
        {
            throw new InvalidOperationException(
                $"SL V3 dongles not found (TX: {(txInfo.Path is null ? "missing" : "ok")}, "
                + $"RX: {(rxInfo.Path is null ? "missing" : "ok")}; {candidates.Count} device(s) under the SLV3 GUID).");
        }

        foreach (var info in new[] { txInfo, rxInfo })
        {
            if (!KnownDeviceRegistry.IsWriteAllowed(info.UsbId))
            {
                throw new InvalidOperationException($"Device {info.UsbId} is not registry-approved for writes.");
            }
        }

        var tx = WinUsbDevice.Open(txInfo.Path, txInfo.UsbId);
        WinUsbDevice? rx = null;
        try
        {
            rx = WinUsbDevice.Open(rxInfo.Path, rxInfo.UsbId);
            trace?.Invoke($"TX pipes: in=0x{tx.InPipe:X2} out=0x{tx.OutPipe:X2}; RX pipes: in=0x{rx.InPipe:X2} out=0x{rx.OutPipe:X2}");
            var controller = new Slv3Controller(tx, rx, trace);
            controller.FindMaster();
            controller.ResetTx();
            controller.SendRxBringUp();
            return controller;
        }
        catch
        {
            tx.Dispose();
            rx?.Dispose();
            throw;
        }
    }

    /// <summary>Probes RF channels (8, evens, odds) on the TX dongle until it reports its MAC.</summary>
    private void FindMaster()
    {
        var response = new byte[Slv3Protocol.UsbPacketSize];
        foreach (var channel in Slv3Protocol.MasterChannelProbeOrder())
        {
            _tx.FlushInput();
            _tx.Write(Slv3Protocol.BuildGetMasterMac(channel));
            var length = _tx.Read(response, MacProbeReadTimeoutMs);
            if (length <= 0)
            {
                continue;
            }

            if (Slv3Protocol.TryParseMasterMac(response, length, out var mac, out var firmware))
            {
                MasterMac = mac;
                MasterChannel = channel;
                MasterFirmware = firmware;
                _trace?.Invoke($"TX master MAC {MasterMacText}, channel {channel}, fw {firmware}");
                return;
            }
        }

        throw new InvalidOperationException("TX dongle did not report a master MAC on any RF channel (1-39).");
    }

    /// <summary>
    /// TX reset (0x11 0x08) — the reference sends this before starting its poll loop; it kicks
    /// the dongle pair into actively polling the RF network so the RX has device data.
    /// </summary>
    public void ResetTx()
    {
        var command = new byte[Slv3Protocol.UsbPacketSize];
        command[0] = 0x11;
        command[1] = 0x08;
        _tx.Write(command);
        _trace?.Invoke("TX reset sent");
        Thread.Sleep(500);
    }

    /// <summary>
    /// Full reference recovery: TX reset, then VIDEO_START (0x11 0x01) plus a prep packet —
    /// activates the dongle's RF engine (reference ensure_video_mode/soft_reset path).
    /// </summary>
    public void ActivateRfEngine()
    {
        ResetTx();
        var videoStart = new byte[Slv3Protocol.UsbPacketSize];
        videoStart[0] = 0x11;
        videoStart[1] = 0x01;
        _tx.Write(videoStart);
        Thread.Sleep(2);

        var prep = new byte[Slv3Protocol.UsbPacketSize];
        prep[0] = Slv3Protocol.UsbCmdSendRf;
        prep[1] = 0x00;
        prep[2] = MasterChannel;
        prep[3] = 0xFF;
        _tx.Write(prep);
        Thread.Sleep(2);
        _trace?.Invoke("RF engine activation sent (VIDEO_START + prep)");
    }

    /// <summary>
    /// RX reset (0x15, "USB_ResetAnother") — the reference's recovery step when the RX repeatedly
    /// returns nothing. Reads the ack (up to 2 s), then settles for 500 ms.
    /// </summary>
    public void ResetRx()
    {
        var command = new byte[Slv3Protocol.UsbPacketSize];
        command[0] = 0x15;
        _rx.Write(command);
        var response = new byte[64];
        var length = _rx.Read(response, 2000);
        _trace?.Invoke($"RX reset ack: {(length > 0 ? Convert.ToHexString(response.AsSpan(0, Math.Min(length, 8))) : "(none)")}");
        Thread.Sleep(500);
    }

    /// <summary>
    /// RX bring-up sequence the reference daemon sends once after connect
    /// (queries 0x34/0x37 with replies, then 0x30): without it the RX can stay silent.
    /// </summary>
    public void SendRxBringUp()
    {
        var response = new byte[64];
        foreach (var (tail, expectReply) in new[] { ((byte)0x34, true), ((byte)0x37, true), ((byte)0x30, false) })
        {
            var command = new byte[Slv3Protocol.UsbPacketSize];
            command[0] = Slv3Protocol.UsbCmdSendRf;
            command[1] = 0x01;
            command[2] = 0x04;
            command[3] = tail;
            _rx.Write(command);
            Thread.Sleep(2);
            if (expectReply)
            {
                var length = _rx.Read(response, 500);
                _trace?.Invoke($"RX bring-up 0x{tail:X2} reply: "
                    + (length > 0 ? Convert.ToHexString(response.AsSpan(0, Math.Min(length, 8))) : "(none)"));
            }
        }
    }

    private byte? _workingPageCount;

    /// <summary>
    /// Polls the RX dongle for the current device list (RPMs, PWM, fan counts). Firmware 16
    /// ignores page count 1, so on first contact we probe 1→4 and cache what works.
    /// </summary>
    public IReadOnlyList<Slv3Device> PollDevices()
    {
        if (_workingPageCount is { } cached)
        {
            return PollDevicesWithPageCount(cached);
        }

        for (byte page = 1; page <= 4; page++)
        {
            var result = PollDevicesWithPageCount(page);
            if (_workingPageCount is not null)
            {
                return result;
            }
        }

        return Devices;
    }

    private IReadOnlyList<Slv3Device> PollDevicesWithPageCount(byte pageCount)
    {
        _rx.FlushInput();
        _rx.Write(Slv3Protocol.BuildGetDeviceList(pageCount));

        // The RX may deliver the response across several transfers; accumulate until a
        // short/empty read (reference behaviour in uni-wireless-sync).
        var response = new byte[2048];
        var total = 0;
        var lastResult = 0;
        while (total < response.Length)
        {
            var chunk = new byte[512];
            lastResult = _rx.Read(chunk, DeviceListReadTimeoutMs);
            if (lastResult <= 0)
            {
                break;
            }

            Array.Copy(chunk, 0, response, total, Math.Min(lastResult, response.Length - total));
            total += lastResult;
            if (lastResult < chunk.Length)
            {
                break;
            }
        }

        _trace?.Invoke($"GetDev(pages={pageCount}) read {total} byte(s) ({(lastResult == -1 ? "timeout" : "zlp/end")})"
            + (total > 0 ? $", head={Convert.ToHexString(response.AsSpan(0, Math.Min(total, 12)))}" : ""));

        var length = total;
        if (length < 4 || response[0] != Slv3Protocol.UsbCmdSendRf)
        {
            return Devices; // keep last known list on timeout — the RX answers intermittently
        }

        _workingPageCount = pageCount;
        var (devices, moboPwm) = Slv3Protocol.ParseDeviceList(response, length);
        MotherboardPwm = moboPwm;
        if (devices.Count > 0)
        {
            Devices = devices;
        }

        return Devices;
    }

    /// <summary>
    /// Sends one PWM command to a device (4 chunked USB packets). Duty is safety-clamped and
    /// firmware-constrained. Must be repeated at least once per second to hold the speed.
    /// </summary>
    public byte SetFanDuty(Slv3Device device, int dutyPercent)
    {
        var clamped = SafetyGuard.ClampFanDuty(dutyPercent);
        var pwmValue = Slv3Protocol.DutyPercentToPwm(clamped);
        var pwm = Slv3Protocol.ApplyPwmConstraints(
            [pwmValue, pwmValue, pwmValue, pwmValue],
            device.FanCount,
            isAio: device.DeviceType is 10 or 11);

        var sequenceIndex = (byte)(Devices
            .Where(d => d.IsBoundTo(MasterMac) && d.DeviceType != 0xFF)
            .ToList()
            .FindIndex(d => d.Mac.AsSpan().SequenceEqual(device.Mac)) + 1);
        if (sequenceIndex == 0)
        {
            sequenceIndex = 1;
        }

        var rfData = Slv3Protocol.BuildPwmRfData(device.Mac, MasterMac, device.RxType, MasterChannel, sequenceIndex, pwm);
        SendRfChunks(rfData, device.Channel, device.RxType);
        _trace?.Invoke($"PWM {pwm[0]}/255 -> {device.MacText} (fans={device.FanCount}, rx={device.RxType}, ch={device.Channel})");
        return pwm[0];
    }

    /// <summary>1 Hz heartbeat; without it fan firmware drifts into autonomous fallback with RPM spikes.</summary>
    public void SendMasterClock() =>
        SendRfChunks(Slv3Protocol.BuildMasterClockRfData(MasterMac), MasterChannel, 0xFF);

    /// <summary>One keepalive round: heartbeat plus a PWM refresh for every bound device.</summary>
    public void SendKeepalive(int dutyPercent)
    {
        SendMasterClock();
        foreach (var device in Devices.Where(d => d.IsBoundTo(MasterMac)))
        {
            SetFanDuty(device, dutyPercent);
        }
    }

    /// <summary>Diagnostic: writes a raw 64-byte command to the TX or RX and returns the reply.</summary>
    public (int Length, byte[] Data) ProbeRaw(bool toTx, byte[] head, int readTimeoutMs = 800)
    {
        var device = toTx ? _tx : _rx;
        var command = new byte[Slv3Protocol.UsbPacketSize];
        head.CopyTo(command, 0);
        device.FlushInput();
        device.Write(command);
        var response = new byte[512];
        var length = device.Read(response, readTimeoutMs);
        return (length, response);
    }

    private void SendRfChunks(byte[] rfData, byte channel, byte rxType)
    {
        foreach (var packet in Slv3Protocol.ChunkRfData(rfData, channel, rxType))
        {
            _tx.Write(packet);
            Thread.Sleep(2);
        }
    }

    public void Dispose()
    {
        _tx.Dispose();
        _rx.Dispose();
    }
}
