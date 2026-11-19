namespace DeviceMaster.Devices.CorsairLink;

public enum LinkDeviceModel : byte
{
    FanQxSeries = 0x01,
    FanLxSeries = 0x02,
    FanRxMaxRgbSeries = 0x03,
    FanRxMaxSeries = 0x04,
    PsuHxiShiftSeries = 0x0b,
    FanRxRgbSeries = 0x0f,
    CapSwapModuleVrmFan = 0x10,
    FanRxSeries = 0x13,
    CommanderDuoSeries = 0x1b,

    // Liquid-cooling devices (AIOs 0x07/0x11, water blocks 0x09/0x0A, XD5/XD6 pumps 0x0C/0x19, the
    // XD5 Elite LCD 0x0E) are deliberately NOT in the catalog: pump control is not supported, and an
    // uncatalogued chain device makes the whole hub do-not-write (it stays on its hardware curve).
}

[Flags]
public enum LinkDeviceFlags
{
    None = 0,
    ReportsTemperature = 1,
    ReportsSpeed = 2,
    ControlsSpeed = 4,
    All = ReportsTemperature | ReportsSpeed | ControlsSpeed,
}

/// <summary>A Link-chain device model we positively recognize (port of KnownLinkDevices, MIT).</summary>
/// <param name="LedCount">
/// Addressable LEDs this model contributes to the hub's color buffer, from OpenLinkHub's
/// device metadata (lsh.json). The hub's own LED enumeration (endpoint 0x20) reports
/// 0 connected LEDs on fw 3.10, so the catalog is the authoritative source; 0 means
/// the model has no addressable LEDs (or, for Commander Duo, a dynamic count the
/// endpoint read supplies).
/// </param>
/// <param name="LedCommandCode">
/// The LED command code the hub's persisted registry (endpoint 0x1E) uses for this model.
/// Only set for models where the code has been read back from real hardware — the registry
/// rewrite never invents codes. 0 = unknown.
/// </param>
/// <remarks>
/// A model that is not in the catalog makes the whole hub do-not-write (see
/// <see cref="LinkDutyPlanner"/>) — unknowns never default to "fan".
/// </remarks>
public sealed record LinkDeviceInfo(LinkDeviceModel Model, byte Variant, string Name, LinkDeviceFlags Flags, int LedCount = 0, byte LedCommandCode = 0);

/// <summary>Catalog of known iCUE LINK chain devices, ported from FanControl.CorsairLink.</summary>
public static class LinkDeviceCatalog
{
    private static readonly Dictionary<(LinkDeviceModel, byte), LinkDeviceInfo> Entries = new[]
    {
        new LinkDeviceInfo(LinkDeviceModel.FanQxSeries, 0x00, "QX Fan", LinkDeviceFlags.All, LedCount: 34),
        new LinkDeviceInfo(LinkDeviceModel.FanRxSeries, 0x00, "RX Fan", LinkDeviceFlags.ControlsSpeed | LinkDeviceFlags.ReportsSpeed),
        new LinkDeviceInfo(LinkDeviceModel.FanRxRgbSeries, 0x00, "RX RGB Fan", LinkDeviceFlags.ControlsSpeed | LinkDeviceFlags.ReportsSpeed, LedCount: 8),
        new LinkDeviceInfo(LinkDeviceModel.FanRxMaxSeries, 0x00, "RX MAX Fan", LinkDeviceFlags.All),
        // LedCommandCode 0x19 read back from a live hub's LED registry (dev rig, fw 3.10.636)
        new LinkDeviceInfo(LinkDeviceModel.FanRxMaxRgbSeries, 0x00, "RX MAX RGB Fan", LinkDeviceFlags.ControlsSpeed | LinkDeviceFlags.ReportsSpeed, LedCount: 8, LedCommandCode: 0x19),
        new LinkDeviceInfo(LinkDeviceModel.FanLxSeries, 0x00, "LX Fan", LinkDeviceFlags.ControlsSpeed | LinkDeviceFlags.ReportsSpeed, LedCount: 18),
        new LinkDeviceInfo(LinkDeviceModel.CapSwapModuleVrmFan, 0x00, "VRM Fan CapSwap Module", LinkDeviceFlags.ControlsSpeed | LinkDeviceFlags.ReportsSpeed),
        new LinkDeviceInfo(LinkDeviceModel.CommanderDuoSeries, 0x00, "COMMANDER DUO", LinkDeviceFlags.All),
        new LinkDeviceInfo(LinkDeviceModel.PsuHxiShiftSeries, 0x00, "HXi SHIFT PSU", LinkDeviceFlags.All),
        new LinkDeviceInfo(LinkDeviceModel.PsuHxiShiftSeries, 0x01, "HXi SHIFT PSU", LinkDeviceFlags.All),
        new LinkDeviceInfo(LinkDeviceModel.PsuHxiShiftSeries, 0x02, "HXi SHIFT PSU", LinkDeviceFlags.All),
    }.ToDictionary(d => (d.Model, d.Variant));

    public static LinkDeviceInfo? Find(byte model, byte variant) =>
        Entries.GetValueOrDefault(((LinkDeviceModel)model, variant));

    /// <summary>
    /// Finds a model by the LED command code the hub's registry uses for it — for sizing the
    /// color slot of a registered channel whose chain identity is unknown (phantom entries).
    /// </summary>
    public static LinkDeviceInfo? FindByLedCommandCode(byte code) =>
        code == 0 ? null : Entries.Values.FirstOrDefault(d => d.LedCommandCode == code);
}
