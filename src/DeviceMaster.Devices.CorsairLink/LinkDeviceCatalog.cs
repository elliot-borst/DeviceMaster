namespace DeviceMaster.Devices.CorsairLink;

public enum LinkDeviceModel : byte
{
    FanQxSeries = 0x01,
    FanLxSeries = 0x02,
    FanRxMaxRgbSeries = 0x03,
    FanRxMaxSeries = 0x04,
    LiquidCoolerHSeries = 0x07,
    WaterBlockXc7Series = 0x09,
    WaterBlockXg3Series = 0x0a,
    PsuHxiShiftSeries = 0x0b,
    PumpXd5Series = 0x0c,
    /// <summary>XD5 Elite LCD display module as a chain device (OpenLinkHub lsh.go: type 14 = XD5 Elite LCD; type 6 = AIO pump-cover LCD).</summary>
    LcdXd5Elite = 0x0e,
    FanRxRgbSeries = 0x0f,
    CapSwapModuleVrmFan = 0x10,
    LiquidCoolerTitanSeries = 0x11,
    FanRxSeries = 0x13,
    PumpXd6Series = 0x19,
    CommanderDuoSeries = 0x1b,
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
public sealed record LinkDeviceInfo(LinkDeviceModel Model, byte Variant, string Name, LinkDeviceFlags Flags, int LedCount = 0, byte LedCommandCode = 0)
{
    /// <summary>
    /// Pump-bearing devices get the pump duty floor and are excluded from fan control.
    /// If a model is not in the catalog at all, the whole hub is treated as
    /// do-not-write (see LinkHub.EnsureWritable) — unknowns never default to "fan".
    /// </summary>
    public bool IsPump =>
        Model is LinkDeviceModel.LiquidCoolerHSeries
            or LinkDeviceModel.LiquidCoolerTitanSeries
            or LinkDeviceModel.PumpXd5Series
            or LinkDeviceModel.PumpXd6Series;
}

/// <summary>Catalog of known iCUE LINK chain devices, ported from FanControl.CorsairLink.</summary>
public static class LinkDeviceCatalog
{
    private static readonly Dictionary<(LinkDeviceModel, byte), LinkDeviceInfo> Entries = new[]
    {
        new LinkDeviceInfo(LinkDeviceModel.FanQxSeries, 0x00, "QX Fan", LinkDeviceFlags.All, LedCount: 34),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerHSeries, 0x00, "H100i", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerHSeries, 0x01, "H115i", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerHSeries, 0x02, "H150i", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerHSeries, 0x03, "H170i", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerHSeries, 0x04, "H100i (white)", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerHSeries, 0x05, "H150i (white)", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.WaterBlockXc7Series, 0x00, "XC7", LinkDeviceFlags.ReportsTemperature, LedCount: 24),
        new LinkDeviceInfo(LinkDeviceModel.WaterBlockXc7Series, 0x01, "XC7 (white)", LinkDeviceFlags.ReportsTemperature, LedCount: 24),
        new LinkDeviceInfo(LinkDeviceModel.WaterBlockXg3Series, 0x00, "XG3", LinkDeviceFlags.All, LedCount: 22),
        new LinkDeviceInfo(LinkDeviceModel.FanRxSeries, 0x00, "RX Fan", LinkDeviceFlags.ControlsSpeed | LinkDeviceFlags.ReportsSpeed),
        new LinkDeviceInfo(LinkDeviceModel.FanRxRgbSeries, 0x00, "RX RGB Fan", LinkDeviceFlags.ControlsSpeed | LinkDeviceFlags.ReportsSpeed, LedCount: 8),
        new LinkDeviceInfo(LinkDeviceModel.FanRxMaxSeries, 0x00, "RX MAX Fan", LinkDeviceFlags.All),
        // LedCommandCode 0x19 read back from a live hub's LED registry (dev rig, fw 3.10.636)
        new LinkDeviceInfo(LinkDeviceModel.FanRxMaxRgbSeries, 0x00, "RX MAX RGB Fan", LinkDeviceFlags.ControlsSpeed | LinkDeviceFlags.ReportsSpeed, LedCount: 8, LedCommandCode: 0x19),
        new LinkDeviceInfo(LinkDeviceModel.PumpXd5Series, 0x00, "XD5", LinkDeviceFlags.All, LedCount: 22),
        new LinkDeviceInfo(LinkDeviceModel.PumpXd5Series, 0x01, "XD5 (white)", LinkDeviceFlags.All, LedCount: 22),
        // Verified on real hardware: the XD5 Elite LCD screen module enumerates as its own
        // chain device (model 0x0E, variant 0x00) next to the pump. No speed to control.
        new LinkDeviceInfo(LinkDeviceModel.LcdXd5Elite, 0x00, "XD5 Elite LCD display module", LinkDeviceFlags.None),
        new LinkDeviceInfo(LinkDeviceModel.FanLxSeries, 0x00, "LX Fan", LinkDeviceFlags.ControlsSpeed | LinkDeviceFlags.ReportsSpeed, LedCount: 18),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerTitanSeries, 0x00, "TITAN AIO", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerTitanSeries, 0x01, "TITAN AIO", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerTitanSeries, 0x02, "TITAN AIO", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerTitanSeries, 0x03, "TITAN AIO", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerTitanSeries, 0x04, "TITAN AIO", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.LiquidCoolerTitanSeries, 0x05, "TITAN 360 RX RGB AIO (white)", LinkDeviceFlags.All, LedCount: 20),
        new LinkDeviceInfo(LinkDeviceModel.CapSwapModuleVrmFan, 0x00, "VRM Fan CapSwap Module", LinkDeviceFlags.ControlsSpeed | LinkDeviceFlags.ReportsSpeed),
        // LedCommandCode 0x11 read back from a live hub's LED registry (dev rig, fw 3.10.636)
        new LinkDeviceInfo(LinkDeviceModel.PumpXd6Series, 0x00, "XD6", LinkDeviceFlags.All, LedCount: 22, LedCommandCode: 0x11),
        new LinkDeviceInfo(LinkDeviceModel.PumpXd6Series, 0x01, "XD6 (white)", LinkDeviceFlags.All, LedCount: 22),
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
