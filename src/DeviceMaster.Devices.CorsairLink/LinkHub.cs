using DeviceMaster.Core.Devices;
using DeviceMaster.Devices.CorsairLink.Protocol;
using HidSharp;

namespace DeviceMaster.Devices.CorsairLink;

public sealed class LinkHubException : Exception
{
    public LinkHubException(string message, byte? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    public byte? ErrorCode { get; }

    public bool IsIncorrectMode => ErrorCode == LinkHubProtocol.ResponseStatus.IncorrectModeError;
}

/// <summary>
/// An open session to one iCUE LINK System Hub. Transport and sequencing ported from
/// EvanMulawski/FanControl.CorsairLink (MIT).
/// Safety: construction refuses devices that aren't a positively identified Link hub;
/// speed writes go through <see cref="LinkDutyPlanner"/> (pump always 100%, unknowns abort);
/// Dispose restores hardware mode if this session put the hub into software mode.
/// </summary>
public sealed class LinkHub : IDisposable
{
    private const int IoTimeoutMs = 500;
    private const int WaitForDataTypeTimeoutMs = 500;

    private readonly HidDevice _device;
    private readonly Action<string>? _trace;
    private readonly object _ioLock = new();
    private HidStream? _stream;
    private bool _inSoftwareMode;
    private bool _supportsAdditionalSubDevices;
    private List<LinkChannel> _channels = [];

    private LinkHub(HidDevice device, Action<string>? trace)
    {
        var usbId = new UsbId((ushort)device.VendorID, (ushort)device.ProductID);
        if (KnownDeviceRegistry.Identify(usbId)?.Kind != DeviceKind.CorsairLinkHub
            || !KnownDeviceRegistry.IsWriteAllowed(usbId))
        {
            throw new InvalidOperationException($"Device {usbId} is not a recognized iCUE LINK hub; refusing to open.");
        }

        _device = device;
        _trace = trace;
        SerialNumber = TryGet(device.GetSerialNumber) ?? "?";
        ProductName = TryGet(device.GetProductName) ?? "iCUE LINK System Hub";
        DevicePath = device.DevicePath;

        static string? TryGet(Func<string> get)
        {
            try { return get(); } catch { return null; }
        }
    }

    public string SerialNumber { get; }
    public string ProductName { get; }
    public string DevicePath { get; }
    public string FirmwareVersion { get; private set; } = "?";
    public bool InSoftwareMode => _inSoftwareMode;
    public IReadOnlyList<LinkChannel> Channels => _channels;
    public bool HasUnknownChannels => _channels.Any(c => !c.IsKnown);

    /// <summary>All Link hub command interfaces present (the hub's MI_00 with output reports).</summary>
    public static IReadOnlyList<HidDevice> FindHubDevices() =>
        DeviceList.Local.GetHidDevices(KnownDeviceRegistry.CorsairVid, 0x0C3F)
            .Where(d =>
            {
                try { return d.GetMaxOutputReportLength() > 0; }
                catch { return false; }
            })
            .ToList();

    public static LinkHub Open(HidDevice device, Action<string>? trace = null)
    {
        var hub = new LinkHub(device, trace);
        try
        {
            if (!device.TryOpen(out var stream))
            {
                throw new LinkHubException($"Could not open HID stream for hub {hub.SerialNumber} — is another program holding it?");
            }

            stream.ReadTimeout = IoTimeoutMs;
            stream.WriteTimeout = IoTimeoutMs;
            hub._stream = stream;

            hub.FirmwareVersion = LinkHubParser.GetFirmwareVersion(hub.SendCommand(LinkHubProtocol.Commands.ReadFirmwareVersion));
            hub._supportsAdditionalSubDevices = SupportsAdditionalSubDevices(hub.FirmwareVersion);
            return hub;
        }
        catch
        {
            hub.Dispose();
            throw;
        }
    }

    /// <summary>Firmware 2.5+ returns the sub-device list across two endpoint reads (up to 24 devices).</summary>
    public static bool SupportsAdditionalSubDevices(string firmwareVersion)
    {
        var parts = firmwareVersion.Split('.');
        return parts.Length >= 2
            && int.TryParse(parts[0], out var major)
            && int.TryParse(parts[1], out var minor)
            && (major > 2 || (major == 2 && minor >= 5));
    }

    /// <summary>
    /// Enumerates the Link chain. If the hub rejects the read in hardware mode and
    /// <paramref name="allowEnterSoftwareMode"/> is set, switches to software mode and retries.
    /// Returns true if the mode switch happened.
    /// </summary>
    public bool EnumerateChannels(bool allowEnterSoftwareMode)
    {
        try
        {
            LoadSubDevices();
            return false;
        }
        catch (LinkHubException ex) when (ex.IsIncorrectMode && allowEnterSoftwareMode)
        {
            EnterSoftwareMode();
            LoadSubDevices();
            return true;
        }
    }

    public void EnterSoftwareMode()
    {
        SendCommand(LinkHubProtocol.Commands.EnterSoftwareMode);
        _inSoftwareMode = true;
    }

    public void EnterHardwareMode()
    {
        SendCommand(LinkHubProtocol.Commands.EnterHardwareMode);
        _inSoftwareMode = false;
        _colorReady = false;
    }

    public IReadOnlyList<LinkSpeedReading> ReadSpeeds() =>
        LinkHubParser.ParseSpeeds(ReadEndpoint(LinkHubProtocol.Endpoints.GetSpeeds, LinkHubProtocol.DataTypes.Speeds));

    public IReadOnlyList<LinkTemperatureReading> ReadTemperatures() =>
        LinkHubParser.ParseTemperatures(ReadEndpoint(LinkHubProtocol.Endpoints.GetTemperatures, LinkHubProtocol.DataTypes.Temperatures));

    /// <summary>
    /// Writes fixed duties for every populated channel (the hub expects the full map each time).
    /// Pump channels get <paramref name="pumpDutyPercent"/> floored at 50%, and unknown devices
    /// abort — see <see cref="LinkDutyPlanner"/>.
    /// </summary>
    public IReadOnlyDictionary<int, byte> WriteFixedDuties(
        IReadOnlyDictionary<int, int>? requestedDuties = null,
        int pumpDutyPercent = Core.Safety.SafetyLimits.FailsafeDutyPercent)
    {
        if (_channels.Count == 0)
        {
            throw new InvalidOperationException("Channels not enumerated — call EnumerateChannels first.");
        }

        var duties = LinkDutyPlanner.BuildDutyMap(_channels, requestedDuties, pumpDutyPercent);
        var data = LinkHubPackets.CreateSoftwareSpeedFixedPercentData(duties);
        WriteEndpoint(LinkHubProtocol.Endpoints.SoftwareSpeedFixedPercent, LinkHubProtocol.DataTypes.SoftwareSpeedFixedPercent, data);
        return duties;
    }

    /// <summary>Fans to the reference default (50%), pumps to 100% — a defined, safe state.</summary>
    public IReadOnlyDictionary<int, byte> WriteSafeDefaults() => WriteFixedDuties();

    // ---- RGB (ported from OpenLinkHub) ----

    private bool _colorReady;
    private IReadOnlyDictionary<int, int> _ledCounts = new Dictionary<int, int>();

    /// <summary>Total LEDs across all connected chain devices (0 before the first color write).</summary>
    public int TotalLeds => _ledCounts.Values.Sum();

    /// <summary>Per-channel LED counts the color buffer was sized from (diagnostics).</summary>
    public IReadOnlyDictionary<int, int> LedCounts => _ledCounts;

    /// <summary>
    /// Sets every LED on the chain to one static color. On first use, sizes the per-channel
    /// LED map, opens the color endpoint (0x22, handle 0), and writes a black reset frame.
    /// The color buffer is interleaved R,G,B per LED in ascending channel order, wrapped in
    /// the write envelope and chunked at 508 bytes (first chunk 0x06 0x00, rest 0x07 0x00).
    /// </summary>
    public void ApplyStaticColor(byte r, byte g, byte b)
    {
        EnsureColorReady();
        WriteColorBuffer(r, g, b);
    }

    private void WriteColorBuffer(byte r, byte g, byte b)
    {
        var total = TotalLeds;
        var data = new byte[total * 3];
        for (var i = 0; i < total; i++)
        {
            data[i * 3] = r;
            data[i * 3 + 1] = g;
            data[i * 3 + 2] = b;
        }

        var envelope = LinkHubPackets.CreateWriteData(LinkHubProtocol.DataTypes.SetColor, data);

        lock (_ioLock)
        {
            var offset = 0;
            var first = true;
            while (offset < envelope.Length)
            {
                var length = Math.Min(LinkHubProtocol.MaxColorChunk, envelope.Length - offset);
                SendCommand(
                    first ? LinkHubProtocol.Commands.WriteColor : LinkHubProtocol.Commands.WriteColorNext,
                    envelope.AsSpan(offset, length));
                first = false;
                offset += length;
            }
        }
    }

    private void EnsureColorReady()
    {
        if (_colorReady)
        {
            return;
        }

        if (_channels.Count == 0)
        {
            throw new InvalidOperationException("Channels not enumerated — call EnumerateChannels first.");
        }

        // LED counts come from the device catalog, as in OpenLinkHub (its endpoint 0x20 read
        // only ever overrides Commander Duo counts). On fw 3.10 that endpoint reports every
        // channel as disconnected, so it must not gate the color write.
        var counts = new Dictionary<int, int>();
        foreach (var channel in _channels)
        {
            if (channel.Info is { LedCount: > 0 } info)
            {
                counts[channel.Channel] = info.LedCount;
            }
        }

        try
        {
            var reported = LinkHubParser.ParseLedCounts(ReadEndpointOnce(LinkHubProtocol.Endpoints.GetLeds));
            _trace?.Invoke($"endpoint 0x20 reports: {(reported.Count == 0 ? "no connected LEDs" : string.Join(", ", reported.Select(kv => $"ch{kv.Key}={kv.Value}")))}");
            foreach (var (channel, leds) in reported)
            {
                // trust the hardware only where the catalog has no count (dynamic devices
                // like Commander Duo); catalog counts stay authoritative otherwise
                if (leds > 0 && !counts.ContainsKey(channel))
                {
                    counts[channel] = leds;
                }
            }
        }
        catch (LinkHubException ex)
        {
            _trace?.Invoke($"LED enumeration read failed: {ex.Message} (continuing with catalog counts)");
        }

        if (counts.Values.Sum() == 0)
        {
            throw new LinkHubException("no RGB-capable devices on this chain — RGB unavailable (will retry)");
        }

        _ledCounts = counts;

        lock (_ioLock)
        {
            // The reference (OpenLinkHub) ignores the responses to these two entirely — on
            // fw 3.10 the hub answers 0x03 (incorrect mode) yet color writes still work.
            try
            {
                SendCommand(LinkHubProtocol.Commands.CloseEndpoint, LinkHubProtocol.Endpoints.SetColor);
            }
            catch (LinkHubException)
            {
            }

            try
            {
                SendCommand(LinkHubProtocol.Commands.OpenColorEndpoint, LinkHubProtocol.Endpoints.SetColor);
            }
            catch (LinkHubException ex)
            {
                _trace?.Invoke($"color endpoint open answered: {ex.Message} (continuing — reference ignores this)");
            }
        }

        _colorReady = true;

        // Reference quirk (OpenLinkHub setDeviceColor): mixed QX/RX chains randomly stay dark
        // unless a reset frame is sent and 40 ms pass before the first real color packet.
        try
        {
            WriteColorBuffer(0, 0, 0);
        }
        catch (LinkHubException ex)
        {
            _colorReady = false;
            throw new LinkHubException($"initial reset frame failed: {ex.Message}", ex.ErrorCode);
        }

        Thread.Sleep(40);
    }

    /// <summary>Close/open/read without waiting for a specific data type (single response read).</summary>
    private byte[] ReadEndpointOnce(ReadOnlySpan<byte> endpoint)
    {
        lock (_ioLock)
        {
            SendCommand(LinkHubProtocol.Commands.CloseEndpoint, endpoint);
            SendCommand(LinkHubProtocol.Commands.OpenEndpoint, endpoint);
            var response = SendCommand(LinkHubProtocol.Commands.Read);
            SendCommand(LinkHubProtocol.Commands.CloseEndpoint, endpoint);
            return response;
        }
    }

    public void Dispose()
    {
        if (_stream is not null && _inSoftwareMode)
        {
            try
            {
                EnterHardwareMode();
            }
            catch
            {
                // Best effort: the hub also recovers to hardware defaults on its own power cycle.
            }
        }

        _stream?.Dispose();
        _stream = null;
    }

    private void LoadSubDevices()
    {
        byte[] first;
        byte[]? continuation = null;

        lock (_ioLock)
        {
            if (!_supportsAdditionalSubDevices)
            {
                first = ReadEndpoint(LinkHubProtocol.Endpoints.GetSubDevices, LinkHubProtocol.DataTypes.SubDevices);
            }
            else
            {
                SendCommand(LinkHubProtocol.Commands.CloseEndpoint, LinkHubProtocol.Endpoints.GetSubDevices);
                SendCommand(LinkHubProtocol.Commands.OpenEndpoint, LinkHubProtocol.Endpoints.GetSubDevices);
                first = SendCommand(LinkHubProtocol.Commands.Read, waitForDataType: LinkHubProtocol.DataTypes.SubDevices);
                continuation = SendCommand(LinkHubProtocol.Commands.Read);
                SendCommand(LinkHubProtocol.Commands.CloseEndpoint, LinkHubProtocol.Endpoints.GetSubDevices);
            }
        }

        var previous = ChannelSignature();
        _channels = LinkHubParser.ParseSubDevices(first, continuation ?? default)
            .Select(d => new LinkChannel(d.Channel, d.Id, d.Model, d.Variant, LinkDeviceCatalog.Find(d.Model, d.Variant)))
            .ToList();

        if (previous.Length > 0 && previous != ChannelSignature())
        {
            _colorReady = false; // chain changed — LED map and color endpoint must be rebuilt
        }
    }

    /// <summary>Stable fingerprint of the enumerated chain, for change detection across re-enumerations.</summary>
    public string ChannelSignature() =>
        string.Join("|", _channels.Select(c => $"{c.Channel}:{c.Id}:{c.Model:X2}"));

    /// <summary>
    /// Diagnostic: the hub's own per-channel LED data, read through the color handle the way
    /// OpenLinkHub's getLedDeviceTypes does — command codes from endpoint 0x1E, LED counts
    /// from endpoint 0x1D. Returns the raw payloads (offsets are firmware-defined).
    /// </summary>
    public (byte[] CommandCodes, byte[] LedCounts) ReadLedDeviceInfo()
    {
        lock (_ioLock)
        {
            return (ReadViaColorHandle(0x1E), ReadViaColorHandle(0x1D));
        }
    }

    /// <summary>
    /// The hub persists its LED registry (which channels carry an LED device, endpoint 0x1E)
    /// in flash, and only vendor software ever rewrites it — re-arranging the chain leaves it
    /// pointing at phantom channels, so color data never reaches the fans that moved.
    /// This compares the registry against the enumerated chain and rewrites it when they
    /// disagree, then pulses LED power (0x15 0x01) so the hub re-registers its devices.
    /// Command codes come from the catalog or the hub's own current table — never invented;
    /// channels whose code is unknown keep their existing entry.
    /// Returns true when the registry was rewritten (colors must be re-applied).
    /// </summary>
    public bool SyncLedRegistry(Action<string>? log = null)
    {
        if (_channels.Count == 0)
        {
            return false;
        }

        IReadOnlyDictionary<int, byte> current;
        lock (_ioLock)
        {
            current = LinkHubParser.ParseLedRegistry(ReadViaColorHandle(0x1E));
        }

        // codes we can trust: catalog first, then whatever the hub already uses for the model
        var codeByModel = new Dictionary<byte, byte>();
        foreach (var channel in _channels)
        {
            if (current.TryGetValue(channel.Channel, out var existing) && existing != 0)
            {
                codeByModel.TryAdd(channel.Model, existing);
            }
        }

        var target = new Dictionary<int, byte>();
        foreach (var channel in _channels)
        {
            if (channel.Info is not { LedCount: > 0 } info)
            {
                continue;
            }

            if (info.LedCommandCode != 0)
            {
                target[channel.Channel] = info.LedCommandCode;
            }
            else if (codeByModel.TryGetValue(channel.Model, out var learned))
            {
                target[channel.Channel] = learned;
            }
            else if (current.TryGetValue(channel.Channel, out var existing))
            {
                target[channel.Channel] = existing; // keep what's there rather than guess
            }
            else
            {
                log?.Invoke($"hub {SerialNumber[..8]}… ch{channel.Channel} ({channel.Name}): no known LED command code — leaving the registry slot empty");
            }
        }

        if (target.Count == current.Count && target.All(kv => current.TryGetValue(kv.Key, out var c) && c == kv.Value))
        {
            return false; // registry already matches the chain
        }

        var maxChannel = Math.Max(
            _channels.Max(c => c.Channel),
            current.Keys.DefaultIfEmpty(0).Max());
        log?.Invoke($"hub {SerialNumber[..8]}… LED registry stale: hub has [{Describe(current)}], chain needs [{Describe(target)}] — rewriting");

        lock (_ioLock)
        {
            WriteEndpoint(
                [0x1E],
                LinkHubProtocol.DataTypes.LedRegistry,
                LinkHubPackets.CreateLedRegistryData(maxChannel, target));
            SendCommand(LinkHubProtocol.Commands.ResetLedPower);
        }

        _colorReady = false; // LED map changed — rebuild the color path on the next apply
        return true;

        static string Describe(IReadOnlyDictionary<int, byte> registry) =>
            string.Join(", ", registry.OrderBy(kv => kv.Key).Select(kv => $"ch{kv.Key}=0x{kv.Value:X2}"));
    }

    private byte[] ReadViaColorHandle(byte endpoint)
    {
        // open (0x0d 0x00 + endpoint) → read (0x08 0x00) → close (0x05 0x01); responses to
        // open/close are best-effort on fw 3.10, like the color endpoint itself
        try
        {
            SendCommand(LinkHubProtocol.Commands.OpenColorEndpoint, [endpoint]);
        }
        catch (LinkHubException)
        {
        }

        try
        {
            return SendCommand([0x08, 0x00]);
        }
        finally
        {
            try
            {
                SendCommand([0x05, 0x01]);
            }
            catch (LinkHubException)
            {
            }
        }
    }

    private byte[] ReadEndpoint(ReadOnlySpan<byte> endpoint, ReadOnlySpan<byte> dataType)
    {
        lock (_ioLock)
        {
            SendCommand(LinkHubProtocol.Commands.CloseEndpoint, endpoint);
            SendCommand(LinkHubProtocol.Commands.OpenEndpoint, endpoint);
            var response = SendCommand(LinkHubProtocol.Commands.Read, waitForDataType: dataType);
            SendCommand(LinkHubProtocol.Commands.CloseEndpoint, endpoint);
            return response;
        }
    }

    private void WriteEndpoint(ReadOnlySpan<byte> endpoint, ReadOnlySpan<byte> dataType, ReadOnlySpan<byte> data)
    {
        var writeData = LinkHubPackets.CreateWriteData(dataType, data);

        lock (_ioLock)
        {
            SendCommand(LinkHubProtocol.Commands.CloseEndpoint, endpoint);
            SendCommand(LinkHubProtocol.Commands.OpenEndpoint, endpoint);
            SendCommand(LinkHubProtocol.Commands.Write, writeData);
            SendCommand(LinkHubProtocol.Commands.CloseEndpoint, endpoint);
        }
    }

    private byte[] SendCommand(ReadOnlySpan<byte> command, ReadOnlySpan<byte> data = default, ReadOnlySpan<byte> waitForDataType = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Hub session is not open.");
        var packet = LinkHubPackets.CreateCommandPacket(command, data);
        var response = new byte[LinkHubProtocol.PacketSize];

        lock (_ioLock)
        {
            DrainPendingReports(stream);

            Trace("WRITE", packet);
            stream.Write(packet, 0, packet.Length);
            ReadPacket(stream, response);
            Trace("READ ", response);

            var errorCode = response[4];
            if (errorCode != LinkHubProtocol.ResponseStatus.Ok)
            {
                throw new LinkHubException(
                    $"Hub returned error 0x{errorCode:X2} for command {Convert.ToHexString(command)}"
                    + (errorCode == LinkHubProtocol.ResponseStatus.IncorrectModeError ? " (incorrect device mode)" : ""),
                    errorCode);
            }

            if (waitForDataType.Length == 2)
            {
                var deadline = Environment.TickCount64 + WaitForDataTypeTimeoutMs;
                while (!response.AsSpan(5, 2).SequenceEqual(waitForDataType))
                {
                    if (Environment.TickCount64 > deadline)
                    {
                        throw new LinkHubException(
                            $"Timed out waiting for data type {Convert.ToHexString(waitForDataType)} from the hub.");
                    }

                    ReadPacket(stream, response);
                    Trace("READ ", response);
                }
            }

            return response.AsSpan(1).ToArray();
        }
    }

    /// <summary>A HID stream returns exactly one input report per Read call — one call is the whole packet.</summary>
    private static void ReadPacket(HidStream stream, byte[] buffer)
    {
#pragma warning disable CA2022 // inexact read is the correct unit for HID report streams
        _ = stream.Read(buffer, 0, buffer.Length);
#pragma warning restore CA2022
    }

    /// <summary>Discards queued input reports so the next read matches the next write (reference behaviour).</summary>
    private static void DrainPendingReports(HidStream stream)
    {
        var originalTimeout = stream.ReadTimeout;
        stream.ReadTimeout = 1;
        try
        {
            while (true)
            {
                _ = stream.Read();
            }
        }
        catch (TimeoutException)
        {
            // queue is empty
        }
        finally
        {
            stream.ReadTimeout = originalTimeout;
        }
    }

    private void Trace(string direction, byte[] buffer)
    {
        if (_trace is null)
        {
            return;
        }

        var significant = buffer.AsSpan(0, Math.Min(buffer.Length, 48));
        _trace($"{direction} {SerialNumber[..Math.Min(8, SerialNumber.Length)]}: {Convert.ToHexString(significant)}...");
    }
}
