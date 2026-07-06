using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;

namespace DeviceMaster.Devices.Turzx;

/// <summary>
/// Stage 0 placeholder for the Turzx / Turing-family smart screen (USB serial).
/// Stage 5 ports the protocol from mathoudebine/turing-smart-screen-python and
/// tedd/Tedd.TuringScreen; the 8.8" panel's revision is confirmed there.
/// </summary>
public static class TurzxFamily
{
    public static IEnumerable<SerialPortInfo> FindScreens(IEnumerable<SerialPortInfo> serialPorts) =>
        serialPorts.Where(p => p.Identification?.Kind == DeviceKind.TurzxScreen);
}
