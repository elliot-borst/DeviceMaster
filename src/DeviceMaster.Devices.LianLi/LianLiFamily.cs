using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;

namespace DeviceMaster.Devices.LianLi;

/// <summary>
/// Stage 0 placeholder for the Lian Li family. This machine runs the UNI FAN SL V3
/// wireless ecosystem (WinUSB: TX/RX controller pair plus one USB node per fan) — NOT the
/// classic 0CF2 Uni Hub that OpenRGB's controller code covers. Stage 1 must research the
/// SL V3 protocol (check current OpenRGB/other projects, else capture L-Connect USB traffic).
/// SL V3 devices are WinUSB, so this layer will use WinUSB/LibUsbDotNet rather than HidSharp.
/// </summary>
public static class LianLiFamily
{
    public static IEnumerable<UsbTreeNode> FindSlv3Controllers(IEnumerable<UsbTreeNode> usbTree) =>
        usbTree.Where(n => Kind(n) == DeviceKind.LianLiSlv3Controller && n.IsPhysicalDevice);

    public static IEnumerable<UsbTreeNode> FindSlv3FanNodes(IEnumerable<UsbTreeNode> usbTree) =>
        usbTree.Where(n => Kind(n) == DeviceKind.LianLiSlv3FanNode && n.IsPhysicalDevice);

    private static DeviceKind Kind(UsbTreeNode node) =>
        node.UsbId is { } id ? KnownDeviceRegistry.Identify(id)?.Kind ?? DeviceKind.Unknown : DeviceKind.Unknown;
}
