namespace DeviceMaster.Devices.MsiMysticLight;

/// <summary>One addressable header on a 761-protocol board.</summary>
public enum MysticZone
{
    /// <summary>JARGB_V2_1 — first 5 V addressable RGB header.</summary>
    Jargb1,

    /// <summary>JARGB_V2_2.</summary>
    Jargb2,

    /// <summary>JARGB_V2_3.</summary>
    Jargb3,

    /// <summary>JAF_1 — MSI's addressable fan connector (EZ Conn).</summary>
    Jaf,
}

/// <summary>
/// MSI Mystic Light "761" mainboard protocol (X870 / B850 / Z890 boards on 0DB0:0076): pure packet
/// builders. Byte-level facts ported from OpenRGB's MSIMysticLight761Controller (GPLv2 — facts
/// only, no code copied):
/// <list type="bullet">
/// <item>one setup feature report <c>0x50</c> (290 bytes): 18 rows of 16 bytes, one per zone slot;
/// it switches the four addressable headers into direct mode, the rest off. Sent once per open.</item>
/// <item>colours: one feature report <c>0x51</c> per header (727 bytes): <c>51 09 hdr0 hdr1 00 00 count</c>
/// then 240 × R,G,B. hdr0 = 0x04 for JARGB (hdr1 = header index 0–2), 0x08 for JAF (hdr1 0).</item>
/// <item>detection: a <c>0x52</c> feature read returning 112/162/185 bytes means an OLDER Mystic Light
/// protocol on the same PID — never drive those with this one.</item>
/// </list>
/// </summary>
public static class MysticLight761Protocol
{
    public const byte SetupReportId = 0x50;
    public const byte ColorReportId = 0x51;
    public const byte ProbeReportId = 0x52;

    public const int SetupLength = 290;
    public const int MaxLeds = 240;
    public const int ColorLength = 7 + MaxLeds * 3; // 727

    /// <summary>Feature lengths an older Mystic Light protocol answers the 0x52 probe with (±1 for the report id).</summary>
    public static readonly IReadOnlyList<int> LegacyProbeLengths = [185, 162, 112];

    public static readonly IReadOnlyList<MysticZone> Zones = [MysticZone.Jargb1, MysticZone.Jargb2, MysticZone.Jargb3, MysticZone.Jaf];

    /// <summary>
    /// Boards OpenRGB's 761 controller supports (its board_configs, tested upstream), as their DMI
    /// board name reads (without the "MSI " vendor prefix). Writes are refused on any other board.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedBoards = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MAG X870 TOMAHAWK WIFI (MS-7E51)",
        "MAG B850M MORTAR WIFI (MS-7E61)",
        "MPG B850I EDGE TI WIFI (MS-7E79)",
        "X870 GAMING PLUS WIFI (MS-7E47)",
        "B850 GAMING PLUS WIFI6E (MS-7E80)",
        "B850M GAMING PLUS WIFI6E (MS-7E81)",
        "MPG X870E CARBON WIFI (MS-7E49)",
        "Z890 GAMING PLUS WIFI (MS-7E34)",
        "X870E GAMING PLUS WIFI (MS-7E70)",
        "MAG X870E TOMAHAWK WIFI (MS-7E59)",
        "PRO B850-P WIFI (MS-7E56)",
        "B850M GAMING PLUS WIFI (MS-7E66)",
        "PRO X870E-P WIFI (MS-7E70)",
        "MPG X870I EDGE TI EVO WIFI (MS-7E50)",
        "B850 GAMING PLUS WIFI (MS-7E56)",
        "PRO X870-P WIFI (MS-7E47)",
        "MPG X870E EDGE TI WIFI (MS-7E59)",
        "MAG B850 TOMAHAWK MAX WIFI (MS-7E62)",
        "PRO B850M-P WIFI (MS-7E71)",
        "MAG Z890 TOMAHAWK WIFI (MS-7E32)",
        "MPG B850 EDGE TI WIFI (MS-7E62)",
        "PRO B850M-VC WIFI6E (MS-7E71)",
        "MAG B850 TOMAHAWK WIFI (MS-7E53)",
        "MEG Z890 UNIFY-X (MS-7E20)",
        "PRO X870E-S EVO WIFI (MS-7E86)",
        "PRO Z890-P WIFI (MS-7E34)",
    };

    /// <summary>Whether a DMI board name (with or without the "MSI " prefix) is a supported 761 board.</summary>
    public static bool IsSupportedBoard(string? boardName)
    {
        var name = (boardName ?? "").Trim();
        if (name.StartsWith("MSI ", StringComparison.OrdinalIgnoreCase))
        {
            name = name[4..].Trim();
        }

        return name.Length > 0 && SupportedBoards.Contains(name);
    }

    /// <summary>Whether a 0x52 probe answer length belongs to an older Mystic Light protocol.</summary>
    public static bool IsLegacyProbeLength(int length) => LegacyProbeLengths.Any(n => length == n || length == n + 1);

    /// <summary>
    /// Whether an interface's feature reports (id, length in bytes incl. the id) are the 761
    /// protocol's: a 0x50 setup and a 0x51 colour report big enough for ours, and no 0x52 report
    /// of an older protocol's size. Decided from the report descriptor — nothing is sent to find out.
    /// </summary>
    public static bool LooksLike761(IEnumerable<(byte Id, int Length)> featureReports)
    {
        var reports = featureReports.ToList();
        bool Has(byte id, int min) => reports.Any(r => r.Id == id && r.Length >= min - 1);
        return Has(SetupReportId, SetupLength)
            && Has(ColorReportId, ColorLength)
            && !reports.Any(r => r.Id == ProbeReportId && IsLegacyProbeLength(r.Length));
    }

    // one 16-byte row: 09 then the zone's setup bytes; "active" rows carry the direct-mode pattern
    private static readonly byte[] ActiveRow = [0x09, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x03, 0x15, 0x78];

    // the last row ("all zones") keeps the direct pattern with the 0x95/0x1E tail of the non-ARGB rows
    private static readonly byte[] AllRow = [0x09, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x03, 0x95, 0x1E];

    /// <summary>
    /// The 0x50 setup report exactly as OpenRGB sends it for these boards: rows 0–3 (JARGB 1–3,
    /// JAF) in direct mode; rows 4–16 (JPIPE, JRGB, onboard) cleared — OpenRGB's off-loop indexes
    /// one byte early, so each of those rows is all zero except its last byte (0x1E); row 17
    /// ("all") untouched; a trailing 0.
    /// </summary>
    public static byte[] BuildSetup()
    {
        var packet = new byte[SetupLength];
        packet[0] = SetupReportId;
        for (var row = 0; row < 18; row++)
        {
            var at = 1 + row * 16;
            if (row < 4)
            {
                ActiveRow.CopyTo(packet, at);
            }
            else if (row < 17)
            {
                packet[at + 15] = 0x1E;
            }
            else
            {
                AllRow.CopyTo(packet, at);
            }
        }

        return packet;
    }

    /// <summary>(hdr0, hdr1) addressing one header in a 0x51 report.</summary>
    public static (byte Hdr0, byte Hdr1) Address(MysticZone zone) => zone switch
    {
        MysticZone.Jargb1 => (0x04, 0x00),
        MysticZone.Jargb2 => (0x04, 0x01),
        MysticZone.Jargb3 => (0x04, 0x02),
        MysticZone.Jaf => (0x08, 0x00),
        _ => throw new ArgumentOutOfRangeException(nameof(zone)),
    };

    /// <summary>
    /// The 0x51 colour report for one header from interleaved R,G,B bytes (up to 240 LEDs; shorter
    /// input is repeated along the strip so a short pattern covers however many LEDs are fitted).
    /// </summary>
    public static byte[] BuildColors(MysticZone zone, ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length < 3 || rgb.Length % 3 != 0)
        {
            throw new ArgumentException("colours are whole R,G,B triples", nameof(rgb));
        }

        var (hdr0, hdr1) = Address(zone);
        var packet = new byte[ColorLength];
        packet[0] = ColorReportId;
        packet[1] = 0x09;
        packet[2] = hdr0;
        packet[3] = hdr1;
        packet[6] = MaxLeds;
        for (var i = 0; i < MaxLeds * 3; i++)
        {
            packet[7 + i] = rgb[i % rgb.Length];
        }

        return packet;
    }

    /// <summary>The 0x51 report painting one header a single colour.</summary>
    public static byte[] BuildStatic(MysticZone zone, byte r, byte g, byte b) => BuildColors(zone, [r, g, b]);
}
