using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;

namespace DeviceMaster.Devices.LianLi;

/// <summary>
/// Discovery helpers for the Lian Li UNI FAN SL V3 wireless ecosystem (WinUSB: TX/RX
/// controller pair plus one USB node per fan) — distinct from the classic 0CF2 Uni Hub that
/// OpenRGB's controller code covers. Control lives in <see cref="Slv3Controller"/>.
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
