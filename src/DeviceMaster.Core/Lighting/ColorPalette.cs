namespace DeviceMaster.Core.Lighting;

/// <summary>One named lighting colour.</summary>
public sealed record NamedColor(string Name, byte R, byte G, byte B)
{
    public string Hex => $"{R:x2}{G:x2}{B:x2}";

    /// <summary>Whether the "selected" tick drawn on this colour should be dark rather than white.</summary>
    public bool DarkInk => 0.299 * R + 0.587 * G + 0.114 * B > 140;

    /// <summary>The tick colour on this swatch: "000" on light colours, "fff" on dark ones.</summary>
    public string Ink => DarkInk ? "000" : "fff";
}

/// <summary>
/// THE lighting colours — the only ones any DeviceMaster surface offers (Windows app, each
/// machine's web page, the hub, the fleet display). Saturated, LED-tuned values: LEDs wash out
/// mixes, so red is pure red and orange/yellow lean on red. To offer a new colour, add it here;
/// nothing else defines colours, and there is no free colour picker.
/// </summary>
public static class ColorPalette
{
    public static readonly IReadOnlyList<NamedColor> All =
    [
        new("White", 255, 255, 255),
        new("Red", 255, 0, 0),
        new("Orange", 255, 70, 0),
        new("Yellow", 255, 170, 0),
        new("Green", 0, 255, 0),
        new("Teal", 0, 255, 255),
        new("Blue", 0, 60, 255),
        new("Purple", 150, 0, 255),
        new("Pink", 255, 0, 110),
    ];

    /// <summary>The palette as the web pages use it: [["ff0000","Red","fff"], …] (hex, name, tick ink).</summary>
    public static string JsArray() =>
        "[" + string.Join(",", All.Select(c => $"[\"{c.Hex}\",\"{c.Name}\",\"{c.Ink}\"]")) + "]";

    /// <summary>Placeholder the web pages carry where the palette goes; servers replace it when serving.</summary>
    public const string Placeholder = "/*PALETTE*/[]";

    /// <summary>A page with its palette placeholder filled in.</summary>
    public static string Inject(string html) => html.Replace(Placeholder, JsArray());
}
