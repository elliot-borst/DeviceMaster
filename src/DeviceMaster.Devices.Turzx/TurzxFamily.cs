using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;

namespace DeviceMaster.Devices.Turzx;

/// <summary>
/// Discovery helper for the Turzx / Turing-family smart screen (USB serial). The 8.8" panel's
/// protocol lives in <see cref="TurzxProtocol"/> / <see cref="TurzxScreen"/>, ported from
/// mathoudebine/turing-smart-screen-python (lcd_comm_rev_c, REV_8INCH / CT88INCH).
/// </summary>
public static class TurzxFamily
{
    public static IEnumerable<SerialPortInfo> FindScreens(IEnumerable<SerialPortInfo> serialPorts) =>
        serialPorts.Where(p => p.Identification?.Kind == DeviceKind.TurzxScreen);
}
