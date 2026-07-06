using DeviceMaster.Core.Devices;
using DeviceMaster.Devices.CorsairLink.Protocol;
using HidSharp;

namespace DeviceMaster.Devices.CorsairLink;

/// <summary>
/// The Corsair pump/res LCD (1B1C:0C43, self-reports "XD5 ELITE LCD Pump") — a direct USB HID
/// device, not reached through the Link hub. JPEG frames go out as 1024-byte chunked output
/// reports; backlight brightness is a feature report. Ported from OpenLinkHub (MIT).
/// </summary>
public sealed class CorsairLcdDevice : IDisposable
{
    private const ushort LcdPid = 0x0C43;

    private readonly HidStream _stream;
    private readonly int _featureLength;

    private CorsairLcdDevice(HidDevice device, HidStream stream)
    {
        _stream = stream;
        SerialNumber = TryGet(device.GetSerialNumber) ?? "?";
        _featureLength = Math.Max(device.GetMaxFeatureReportLength(), 4);

        static string? TryGet(Func<string> get)
        {
            try { return get(); } catch { return null; }
        }
    }

    public string SerialNumber { get; }

    public static IReadOnlyList<HidDevice> FindDevices() =>
        DeviceList.Local.GetHidDevices(KnownDeviceRegistry.CorsairVid, LcdPid)
            .Where(d =>
            {
                try { return d.GetMaxOutputReportLength() >= CorsairLcdPackets.ReportSize; }
                catch { return false; }
            })
            .ToList();

    public static CorsairLcdDevice Open(HidDevice device)
    {
        var usbId = new UsbId((ushort)device.VendorID, (ushort)device.ProductID);
        if (KnownDeviceRegistry.Identify(usbId)?.Kind != DeviceKind.CorsairLcd
            || !KnownDeviceRegistry.IsWriteAllowed(usbId))
        {
            throw new InvalidOperationException($"Device {usbId} is not the recognized Corsair LCD; refusing to open.");
        }

        if (!device.TryOpen(out var stream))
        {
            throw new InvalidOperationException("Could not open the Corsair LCD HID stream — is another program holding it?");
        }

        stream.WriteTimeout = 2000;
        return new CorsairLcdDevice(device, stream);
    }

    /// <summary>Pushes one JPEG frame (480×480); the panel keeps showing it.</summary>
    public void SendJpegFrame(byte[] jpeg)
    {
        foreach (var report in CorsairLcdPackets.CreateImageReports(jpeg))
        {
            _stream.Write(report, 0, report.Length);
        }
    }

    /// <summary>Backlight brightness 0 (off) to 100 (feature report, padded to the device's length).</summary>
    public void SetBrightness(int percent)
    {
        var report = new byte[_featureLength];
        CorsairLcdPackets.CreateBrightnessReport(percent).CopyTo(report, 0);
        _stream.SetFeature(report);
    }

    public void Dispose() => _stream.Dispose();
}
