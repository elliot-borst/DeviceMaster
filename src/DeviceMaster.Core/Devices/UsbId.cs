using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DeviceMaster.Core.Devices;

/// <summary>USB vendor id / product id pair, e.g. 1B1C:0C3F.</summary>
public readonly partial record struct UsbId(ushort Vid, ushort Pid)
{
    public override string ToString() => $"{Vid:X4}:{Pid:X4}";

    public static UsbId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new FormatException($"'{text}' is not a VID:PID pair.");

    public static bool TryParse([NotNullWhen(true)] string? text, out UsbId id)
    {
        id = default;
        if (text is null) return false;
        var parts = text.Split(':');
        if (parts.Length != 2
            || !ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid)
            || !ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
        {
            return false;
        }

        id = new UsbId(vid, pid);
        return true;
    }

    /// <summary>Extracts VID/PID from a Windows PnP instance id such as <c>USB\VID_1B1C&amp;PID_0C3F\serial</c>.</summary>
    public static bool TryFromPnpInstanceId(string? instanceId, out UsbId id)
    {
        id = default;
        if (instanceId is null) return false;
        var match = PnpIdPattern().Match(instanceId);
        if (!match.Success) return false;
        id = new UsbId(
            ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ushort.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return true;
    }

    [GeneratedRegex(@"VID_([0-9A-F]{4}).*?PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PnpIdPattern();
}
