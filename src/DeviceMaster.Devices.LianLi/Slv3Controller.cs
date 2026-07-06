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
    // Poll timeouts stay SHORT: polling holds the same gate as the 800 ms keepalive (the
    // dongle firmware cannot take concurrent TX+RX host I/O), and SL V3 fans revert to
    // firmware defaults when the keepalive pauses much beyond a second.
    private const int MacProbeReadTimeoutMs = 200;
    private const int DeviceListReadTimeoutMs = 400;
    private const int DeviceListProbeTimeoutMs = 250;

    /// <summary>Silent polls tolerated before the recovery ladder kicks in (RX reset, then RF engine).</summary>
    private const int FailedPollsBeforeRecovery = 3;

    /// <summary>A device absent this long from otherwise-healthy telemetry is treated as removed.</summary>
    private const long RememberedDeviceTtlMs = 10 * 60_000;

    private readonly WinUsbDevice _tx;
    private readonly WinUsbDevice _rx;
    private readonly Action<string>? _trace;
    private readonly Action<string>? _log;

    // TX pipe serialization: the keepalive thread and the control tick (recovery, colors)
    // may both write the TX dongle — 64-byte protocol chunks must never interleave.
    private readonly object _txGate = new();

    // protects the remembered-device map and the Devices snapshot swap
    private readonly object _deviceGate = new();
    private readonly Dictionary<string, (Slv3Device Device, long LastSeenTick)> _known = [];
    private int _consecutiveFailedPolls;

    private Slv3Controller(WinUsbDevice tx, WinUsbDevice rx, Action<string>? trace, Action<string>? log)
    {
        _tx = tx;
        _rx = rx;
        _trace = trace;
        _log = log;
    }

    public byte[] MasterMac { get; private set; } = new byte[6];
    public byte MasterChannel { get; private set; } = 8;
    public ushort MasterFirmware { get; private set; }

    /// <summary>
    /// Every wireless device seen this session (freshest record per MAC). Devices missing from
    /// a poll stay listed — the RX telemetry path drops out routinely while the TX command path
    /// keeps working, so keepalive/PWM/RGB must keep flowing to them.
    /// </summary>
    public IReadOnlyList<Slv3Device> Devices { get; private set; } = [];

    public int? MotherboardPwm { get; private set; }

    public string MasterMacText => Convert.ToHexString(MasterMac).ToLowerInvariant();

    /// <summary>Milliseconds since this device last appeared in RX telemetry (long.MaxValue if never).</summary>
    public long LastSeenAgeMs(Slv3Device device)
    {
        lock (_deviceGate)
        {
            return _known.TryGetValue(device.MacText, out var entry)
                ? Environment.TickCount64 - entry.LastSeenTick
                : long.MaxValue;
        }
    }

    public static Slv3Controller Open(Action<string>? trace = null, Action<string>? log = null)
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
            RememberDongleParents(txInfo.Path, rxInfo.Path);
            trace?.Invoke($"TX pipes: in=0x{tx.InPipe:X2} out=0x{tx.OutPipe:X2}; RX pipes: in=0x{rx.InPipe:X2} out=0x{rx.OutPipe:X2}");
            var controller = new Slv3Controller(tx, rx, trace, log);
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
        lock (_txGate)
        {
            _tx.Write(command);
        }

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

        var prep = new byte[Slv3Protocol.UsbPacketSize];
        prep[0] = Slv3Protocol.UsbCmdSendRf;
        prep[1] = 0x00;
        prep[2] = MasterChannel;
        prep[3] = 0xFF;

        lock (_txGate)
        {
            _tx.Write(videoStart);
            Thread.Sleep(2);
            _tx.Write(prep);
            Thread.Sleep(2);
        }

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
    private int _recoveryStage; // 0 = nothing tried yet, 1 = RX reset done, 2 = RF engine restart done

    /// <summary>
    /// Set once the in-band recovery ladder (RX reset, then RF engine restart) has run and
    /// telemetry is still silent. The owner should dispose this controller and reopen the
    /// dongles — escalating to a USB device-node restart if reopening alone doesn't help.
    /// </summary>
    public bool NeedsReopen { get; private set; }

    /// <summary>
    /// Polls the RX dongle for the current device list (RPMs, PWM, fan counts) and merges it
    /// into the remembered-device registry. A silent RX never shrinks the device list; after
    /// <see cref="FailedPollsBeforeRecovery"/> consecutive silent polls the recovery ladder
    /// runs (RX reset → RF engine restart → <see cref="NeedsReopen"/>).
    /// </summary>
    public IReadOnlyList<Slv3Device> PollDevices()
    {
        if (TryReadDeviceList() is { } fresh)
        {
            if (_recoveryStage != 0)
            {
                _log?.Invoke("SL V3 telemetry recovered");
            }

            _consecutiveFailedPolls = 0;
            _recoveryStage = 0;
            MergeDevices(fresh);
            return Devices;
        }

        if (++_consecutiveFailedPolls >= FailedPollsBeforeRecovery && !NeedsReopen)
        {
            _consecutiveFailedPolls = 0;
            _workingPageCount = null; // re-probe page counts after any recovery action
            try
            {
                switch (_recoveryStage)
                {
                    case 0:
                        _log?.Invoke("SL V3 telemetry silent — resetting the RX dongle");
                        ResetRx();
                        SendRxBringUp();
                        _recoveryStage = 1;
                        break;
                    case 1:
                        _log?.Invoke("SL V3 telemetry still silent after an RX reset — restarting the RF engine");
                        ActivateRfEngine();
                        SendRxBringUp();
                        _recoveryStage = 2;
                        break;
                    default:
                        _log?.Invoke("SL V3 telemetry still silent after in-band recovery — dongles need a reopen/restart");
                        NeedsReopen = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"SL V3 recovery step failed: {ex.Message} — dongles need a reopen/restart");
                NeedsReopen = true;
            }
        }

        return Devices;
    }

    /// <summary>One GetDev round; null when the RX doesn't produce a valid response.</summary>
    private IReadOnlyList<Slv3Device>? TryReadDeviceList()
    {
        if (_workingPageCount is { } cached)
        {
            return ReadDeviceListOnce(cached, DeviceListReadTimeoutMs);
        }

        // fw 16 ignores page count 1 entirely (verified live) — probe it last, briefly
        foreach (var page in new byte[] { 2, 3, 4, 1 })
        {
            if (ReadDeviceListOnce(page, DeviceListProbeTimeoutMs) is { } list)
            {
                return list;
            }
        }

        return null;
    }

    private IReadOnlyList<Slv3Device>? ReadDeviceListOnce(byte pageCount, int readTimeoutMs)
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
            lastResult = _rx.Read(chunk, readTimeoutMs);
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

        if (total < 4 || response[0] != Slv3Protocol.UsbCmdSendRf)
        {
            return null;
        }

        _workingPageCount = pageCount;
        var (devices, moboPwm) = Slv3Protocol.ParseDeviceList(response, total);
        MotherboardPwm = moboPwm;
        return devices;
    }

    private void MergeDevices(IReadOnlyList<Slv3Device> fresh)
    {
        var now = Environment.TickCount64;
        lock (_deviceGate)
        {
            foreach (var device in fresh)
            {
                if (!_known.ContainsKey(device.MacText))
                {
                    _log?.Invoke($"SL V3 group {device.MacText[..4]} appeared ({device.FanCount} fans, rx type {device.RxType})");
                }

                _known[device.MacText] = (device, now);
            }

            // telemetry is healthy right now, so a long-absent device is genuinely gone
            foreach (var mac in _known
                .Where(kv => now - kv.Value.LastSeenTick > RememberedDeviceTtlMs)
                .Select(kv => kv.Key).ToList())
            {
                _log?.Invoke($"SL V3 group {mac[..4]} absent from telemetry for 10 min — dropping it");
                _known.Remove(mac);
            }

            Devices = _known.Values
                .Select(v => v.Device)
                .OrderBy(d => d.MacText, StringComparer.Ordinal)
                .ToList();
        }
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

    /// <summary>
    /// Sends a one-frame static color to a fan group (all LEDs the same). The firmware stores
    /// and loops the effect, so this is sent once per color change, not per frame.
    /// </summary>
    public void ApplyStaticColor(Slv3Device device, byte r, byte g, byte b, ReadOnlySpan<byte> effectIndex)
    {
        var ledCount = (byte)Math.Clamp(device.FanCount * Slv3Protocol.LedsPerFan, Slv3Protocol.LedsPerFan, 255);
        var raw = new byte[ledCount * 3];
        for (var i = 0; i < ledCount; i++)
        {
            raw[i * 3] = r;
            raw[i * 3 + 1] = g;
            raw[i * 3 + 2] = b;
        }

        var compressed = TinyUz.Compress(raw);
        foreach (var payload in Slv3Protocol.BuildRgbRfPayloads(
            device.Mac, MasterMac, effectIndex, compressed, ledCount, totalFrames: 1, intervalMs: 5000))
        {
            SendRfChunks(payload, device.Channel, device.RxType);
            Thread.Sleep(2);
        }

        _trace?.Invoke($"RGB #{r:X2}{g:X2}{b:X2} -> {device.MacText} ({ledCount} LEDs, {compressed.Length}B compressed)");
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

    // where each dongle was last seen plugged in (its parent USB hub), captured at every
    // successful open — needed to bring back a dongle that has fallen OFF the bus entirely
    private static string? _lastTxParentId;
    private static string? _lastRxParentId;

    private static void RememberDongleParents(string txPath, string rxPath)
    {
        try
        {
            if (Core.Discovery.UsbDeviceRestarter.InstanceIdFromInterfacePath(txPath) is { } txId)
            {
                _lastTxParentId = Core.Discovery.UsbDeviceRestarter.TryGetParentInstanceId(txId) ?? _lastTxParentId;
            }

            if (Core.Discovery.UsbDeviceRestarter.InstanceIdFromInterfacePath(rxPath) is { } rxId)
            {
                _lastRxParentId = Core.Discovery.UsbDeviceRestarter.TryGetParentInstanceId(rxId) ?? _lastRxParentId;
            }
        }
        catch
        {
            // topology memory is best-effort; recovery falls back to sibling lookup
        }
    }

    /// <summary>
    /// The software equivalent of unplugging and reseating both dongles (requires elevation).
    /// Present dongles get their USB device node restarted. A dongle MISSING from the bus
    /// (brownout/glitch on marginal cabling — seen live 2026-07-06) can only come back when
    /// its port re-enumerates, so its last-known parent hub (or a present sibling's parent —
    /// the pair shares a hub) is restarted instead. Root hubs are never touched: cycling one
    /// would drop every device on that controller. Returns the number of restarts performed.
    /// </summary>
    public static int RestartDongleDevices(Action<string>? log = null)
    {
        var candidates = WinUsbDevice.Enumerate(WinUsbDevice.Slv3InterfaceGuid)
            .Where(c => c.UsbId.Pid is TxPid or RxPid && KnownDeviceRegistry.IsWriteAllowed(c.UsbId))
            .ToList();

        var restarted = 0;
        foreach (var (path, usbId) in candidates)
        {
            if (Core.Discovery.UsbDeviceRestarter.InstanceIdFromInterfacePath(path) is not { } instanceId)
            {
                log?.Invoke($"SL V3 dongle {usbId}: could not derive a device instance id from its path — skipped");
                continue;
            }

            log?.Invoke($"SL V3 dongle {usbId}: restarting its USB device node (software replug)");
            Core.Discovery.UsbDeviceRestarter.Restart(instanceId);
            restarted++;
        }

        // vanished dongles: cycle the hub whose port they were last seen on
        var parentsToCycle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!candidates.Any(c => c.UsbId.Pid == TxPid) && _lastTxParentId is { } txParent)
        {
            parentsToCycle.Add(txParent);
        }

        if (!candidates.Any(c => c.UsbId.Pid == RxPid) && _lastRxParentId is { } rxParent)
        {
            parentsToCycle.Add(rxParent);
        }

        if (candidates.Count < 2 && parentsToCycle.Count == 0)
        {
            // no remembered topology (fresh process) — assume the pair shares a hub and use a
            // present sibling's parent
            foreach (var (path, _) in candidates)
            {
                if (Core.Discovery.UsbDeviceRestarter.InstanceIdFromInterfacePath(path) is { } id
                    && Core.Discovery.UsbDeviceRestarter.TryGetParentInstanceId(id) is { } parent)
                {
                    parentsToCycle.Add(parent);
                    break;
                }
            }
        }

        foreach (var parent in parentsToCycle)
        {
            if (parent.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"SL V3 dongle is missing and its last port was on a ROOT hub ({parent}) — "
                    + "not cycling it (would drop every device on that controller); physical replug required");
                continue;
            }

            log?.Invoke($"SL V3 dongle missing from the USB bus — restarting its parent hub ({parent}) to re-enumerate the port");
            try
            {
                Core.Discovery.UsbDeviceRestarter.Restart(parent, settleMs: 3000);
                restarted++;
            }
            catch (Exception ex)
            {
                log?.Invoke($"parent hub restart failed: {ex.Message}");
            }
        }

        return restarted;
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
        lock (_txGate)
        {
            foreach (var packet in Slv3Protocol.ChunkRfData(rfData, channel, rxType))
            {
                _tx.Write(packet);
                Thread.Sleep(2);
            }
        }
    }

    public void Dispose()
    {
        _tx.Dispose();
        _rx.Dispose();
    }
}
