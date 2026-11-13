using DeviceMaster.Core.Devices;
using HidSharp;

namespace DeviceMaster.Devices.MsiMysticLight;

/// <summary>
/// Session to an MSI Mystic Light mainboard controller on the 761 protocol (0DB0:0076 on X870 /
/// B850 / Z890 boards). Drives the four addressable headers (JARGB 1–3 and JAF) with direct
/// colours; nothing is written to the controller's flash, so the BIOS/vendor effect comes back
/// after a power cycle until DeviceMaster runs again.
/// <para>
/// Safety (CLAUDE.md): construction refuses a device the registry doesn't allow writes to, a board
/// whose DMI name isn't on the tested 761 list, and an interface whose report descriptor isn't the
/// 761 layout (0DB0:0076 is shared by older Mystic Light protocols that must never get these bytes).
/// </para>
/// </summary>
public sealed class MysticLightController : IDisposable
{
    public const ushort MsiVid = 0x0DB0;
    private const int IoTimeoutMs = 1000;

    private readonly HidStream _stream;
    private readonly int _featureLength;
    private readonly object _ioLock = new();

    private MysticLightController(HidDevice device, HidStream stream, string board)
    {
        _stream = stream;
        _featureLength = SafeMaxFeature(device);
        DevicePath = device.DevicePath;
        UsbId = new UsbId((ushort)device.VendorID, (ushort)device.ProductID);
        Board = board;
    }

    public string DevicePath { get; }
    public UsbId UsbId { get; }

    /// <summary>The board's DMI name, e.g. "MAG X870E TOMAHAWK WIFI (MS-7E59)".</summary>
    public string Board { get; }

    /// <summary>Mystic Light interfaces present whose report descriptor is the 761 layout.</summary>
    public static IReadOnlyList<HidDevice> FindDevices() =>
        DeviceList.Local.GetHidDevices(MsiVid)
            .Where(d => KnownDeviceRegistry.Identify(new UsbId((ushort)d.VendorID, (ushort)d.ProductID))?.Kind
                    == DeviceKind.MotherboardRgbController)
            .Where(Is761Interface)
            .ToList();

    private static bool Is761Interface(HidDevice device)
    {
        try
        {
            var reports = device.GetReportDescriptor().FeatureReports.Select(r => (r.ReportID, r.Length));
            return MysticLight761Protocol.LooksLike761(reports);
        }
        catch
        {
            return false;
        }
    }

    public static MysticLightController Open(HidDevice device)
    {
        var usbId = new UsbId((ushort)device.VendorID, (ushort)device.ProductID);
        if (!KnownDeviceRegistry.IsWriteAllowed(usbId))
        {
            throw new InvalidOperationException($"Device {usbId} is not registry-approved for writes.");
        }

        var board = ReadBoardName();
        if (!MysticLight761Protocol.IsSupportedBoard(board))
        {
            throw new InvalidOperationException(
                $"Mystic Light: board '{board ?? "unknown"}' is not on the tested 761-protocol list — not driving it.");
        }

        if (!Is761Interface(device))
        {
            throw new InvalidOperationException("Mystic Light: this interface is not the 761 protocol — not driving it.");
        }

        if (!device.TryOpen(out var stream))
        {
            throw new InvalidOperationException("Could not open the Mystic Light controller — is MSI Center holding it?");
        }

        stream.ReadTimeout = IoTimeoutMs;
        stream.WriteTimeout = IoTimeoutMs;
        var controller = new MysticLightController(device, stream, board!);
        try
        {
            controller.Send(MysticLight761Protocol.BuildSetup()); // headers into direct mode
            return controller;
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    /// <summary>Every header one static colour.</summary>
    public void ApplyStaticColor(byte r, byte g, byte b)
    {
        lock (_ioLock)
        {
            foreach (var zone in MysticLight761Protocol.Zones)
            {
                Send(MysticLight761Protocol.BuildStatic(zone, r, g, b));
            }
        }
    }

    /// <summary>Every header one frame of interleaved R,G,B (repeated along each strip).</summary>
    public void ApplyFrame(byte[] rgb)
    {
        lock (_ioLock)
        {
            foreach (var zone in MysticLight761Protocol.Zones)
            {
                Send(MysticLight761Protocol.BuildColors(zone, rgb));
            }
        }
    }

    public string Describe() => $"Mystic Light '{Board}' (761 protocol, JARGB 1–3 + JAF)";

    private void Send(byte[] packet)
    {
        // Windows' HID class wants every feature buffer at the interface's longest feature length
        if (OperatingSystem.IsWindows() && _featureLength > packet.Length)
        {
            Array.Resize(ref packet, _featureLength);
        }

        _stream.SetFeature(packet);
    }

    private static int SafeMaxFeature(HidDevice device)
    {
        try
        {
            return device.GetMaxFeatureReportLength();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The mainboard's DMI product name (Linux sysfs, Windows registry); null when unreadable.</summary>
    public static string? ReadBoardName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                return (key?.GetValue("BaseBoardProduct") as string)?.Trim();
            }

            const string path = "/sys/class/dmi/id/board_name";
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
