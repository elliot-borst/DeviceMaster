using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;

namespace DeviceMaster.Devices.CorsairLink;

/// <summary>
/// Discovery helpers for the iCUE LINK protocol family. The speed/sensor protocol is ported
/// from EvanMulawski/FanControl.CorsairLink; RGB from jurkovic-nikola/OpenLinkHub.
/// </summary>
public static class CorsairLinkFamily
{
    /// <summary>The hub's command interface is the one with output reports (MI_00); MI_01 is input-only.</summary>
    public static IEnumerable<DiscoveredDevice> FindHubs(IEnumerable<DiscoveredDevice> hidDevices) =>
        hidDevices.Where(d => d.Kind == DeviceKind.CorsairLinkHub
            && d.Transport == DeviceTransport.Hid
            && d.MaxOutputReportLength > 0);
}
