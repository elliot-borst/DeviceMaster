using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;

namespace DeviceMaster.Devices.CorsairLink;

/// <summary>
/// Stage 0 placeholder for the iCUE LINK protocol family.
/// Stage 1 ports the speed/sensor protocol from EvanMulawski/FanControl.CorsairLink;
/// RGB and LCD framing come from jurkovic-nikola/OpenLinkHub (Stages 2 and 4).
/// </summary>
public static class CorsairLinkFamily
{
    public static IEnumerable<DiscoveredDevice> FindHubs(IEnumerable<DiscoveredDevice> hidDevices) =>
        hidDevices.Where(d => d.Kind == DeviceKind.CorsairLinkHub && d.Transport == DeviceTransport.Hid);

    public static IEnumerable<DiscoveredDevice> FindLcdModules(IEnumerable<DiscoveredDevice> hidDevices) =>
        hidDevices.Where(d => d.Kind == DeviceKind.CorsairLcd && d.Transport == DeviceTransport.Hid);
}
