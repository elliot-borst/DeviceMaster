namespace DeviceMaster.Core.Devices;

/// <summary>A positively identified device model.</summary>
/// <param name="SupportPlanned">Only devices with this flag may ever be written to (safety rule).</param>
public sealed record KnownDevice(UsbId Id, DeviceKind Kind, string Name, bool SupportPlanned, string? Notes = null);

/// <summary>
/// Single source of truth for VID/PID identification. The safety rule "no writes to devices
/// we haven't positively identified" is enforced through <see cref="IsWriteAllowed"/> — every
/// protocol layer must gate its writes on it.
/// Entries whose notes start with * have been verified against real hardware
/// (see docs/SUPPORTED-DEVICES.md).
/// </summary>
public static class KnownDeviceRegistry
{
    public const ushort CorsairVid = 0x1B1C;
    public const ushort LianLiClassicVid = 0x0CF2;

    private static readonly Dictionary<UsbId, KnownDevice> Entries = new[]
    {
        // ---- Corsair iCUE LINK ----
        Entry(0x1B1C, 0x0C3F, DeviceKind.CorsairLinkHub, "Corsair iCUE LINK System Hub", supportPlanned: true,
            "*Composite HID, 2 interfaces. Speed/sensors: FanControl.CorsairLink; RGB: OpenLinkHub."),
        Entry(0x1B1C, 0x0C43, DeviceKind.CorsairLcd, "Corsair pump/res LCD (self-reports 'XD5 ELITE LCD Pump')", supportPlanned: true,
            "*Single HID interface; out=1024 in=512 feature=32 — matches OpenLinkHub's LCD image framing."),

        // ---- Lian Li SL V3 wireless ecosystem ----
        Entry(0x0416, 0x8040, DeviceKind.LianLiSlv3Controller, "Lian Li SL V3 wireless TX (SLV3TX)", supportPlanned: true,
            "*WinUSB. Nuvoton MCU. Protocol needs Stage 1 research — not covered by OpenRGB's Uni Hub code."),
        Entry(0x0416, 0x8041, DeviceKind.LianLiSlv3Controller, "Lian Li SL V3 wireless RX (SLV3RX)", supportPlanned: true,
            "*WinUSB. Nuvoton MCU."),
        Entry(0x1CBE, 0x0005, DeviceKind.LianLiSlv3FanNode, "Lian Li UNI FAN SL V3 (per-fan USB node)", supportPlanned: true,
            "*WinUSB. TI/Luminary MCU; one node per fan (LCD streaming only)."),

        // ---- Classic wired Lian Li Uni hubs (VID/PIDs from OpenRGB; recognized, not implemented) ----
        Entry(0x0CF2, 0x7750, DeviceKind.LianLiUniHub, "Lian Li Uni Hub SL", supportPlanned: false),
        Entry(0x0CF2, 0xA100, DeviceKind.LianLiUniHub, "Lian Li Uni Hub AL", supportPlanned: false),
        Entry(0x0CF2, 0xA102, DeviceKind.LianLiUniHub, "Lian Li Uni Hub SL Infinity", supportPlanned: false),
        Entry(0x0CF2, 0xA103, DeviceKind.LianLiUniHub, "Lian Li Uni Hub SL V2", supportPlanned: false),
        Entry(0x0CF2, 0xA104, DeviceKind.LianLiUniHub, "Lian Li Uni Hub AL V2", supportPlanned: false),

        // ---- Turzx / Turing smart screen ----
        // NB: CA88 is only the panel's STANDBY/CONTROL port. Its protocol lives on a second,
        // gadget-serial DATA port (0525:A4A7 / serial 20080411 / 1D6B:0121|0106); TurzxScreen
        // opens that one, authorised by this CA88 identity being co-present. See docs.
        Entry(0x1A86, 0xCA88, DeviceKind.TurzxScreen, "Turzx/Turing 8.8\" smart screen", supportPlanned: true,
            "*USB serial (usbser), serial string CT88INCH — standby/control port. Protocol (rev_c REV_8INCH) "
            + "runs on the paired 0525:A4A7 gadget-serial DATA port; verified live (rom 1.90)."),

        // ---- ASUS Aura (motherboard RGB) ----
        Entry(0x0B05, 0x19AF, DeviceKind.MotherboardRgbController, "ASUS Aura LED Controller", supportPlanned: true,
            "*HID, 65-byte reports (0xEC prefix). Mainboard-class protocol (OpenRGB AuraMainboardController)."),
    }.ToDictionary(d => d.Id);

    private static readonly Dictionary<ushort, string> VendorNames = new()
    {
        [0x1B1C] = "Corsair",
        [0x0CF2] = "Lian Li",
        [0x0416] = "Nuvoton (Lian Li SL V3 TX/RX)",
        [0x1CBE] = "TI Luminary (Lian Li SL V3 fan)",
        [0x1A86] = "WCH/QinHeng (Turzx serial bridge)",
        [0x0B05] = "ASUS",
    };

    public static KnownDevice? Identify(UsbId id) => Entries.GetValueOrDefault(id);

    public static string? VendorName(ushort vid) => VendorNames.GetValueOrDefault(vid);

    /// <summary>Safety gate: writes are only ever allowed to positively identified, planned-support devices.</summary>
    public static bool IsWriteAllowed(UsbId id) => Identify(id) is { SupportPlanned: true };

    public static IReadOnlyCollection<KnownDevice> All => Entries.Values;

    private static KnownDevice Entry(ushort vid, ushort pid, DeviceKind kind, string name, bool supportPlanned, string? notes = null)
        => new(new UsbId(vid, pid), kind, name, supportPlanned, notes);
}
