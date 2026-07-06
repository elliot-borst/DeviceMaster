using System.Globalization;
using DeviceMaster.Devices.AsusAura;
using Serilog;

namespace DeviceMaster.App.Commands;

/// <summary>
/// CLI verification for the ASUS Aura mainboard controller.
/// `aura status` reads firmware + zones; `aura set --color RRGGBB` applies a static color.
/// </summary>
internal static class AuraCommands
{
    public static int Run(string[] args)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";

        var devices = AuraController.FindDevices();
        if (devices.Count == 0)
        {
            Log.Error("No Aura mainboard controller found (expected 0B05:19AF with 65-byte reports).");
            return 1;
        }

        foreach (var device in devices)
        {
            using var aura = AuraController.Open(device);
            Log.Information("Aura controller {Id}: firmware '{Fw}', {Zones} zone(s), {Leds} effect LED slot(s)",
                aura.UsbId, aura.FirmwareName, aura.Zones.Count, aura.TotalEffectLeds);
            foreach (var zone in aura.Zones)
            {
                Console.WriteLine($"  zone: effectCh={zone.EffectChannel} directCh=0x{zone.DirectChannel:X2} "
                    + $"leds={zone.LedCount} {(zone.IsAddressable ? "ARGB header" : "onboard")}");
            }

            if (sub == "set")
            {
                var hex = GetString(args, "--color") ?? "00FF00";
                var rgb = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var (r, g, b) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                Log.Information("Applying static #{Hex} to all zones…", hex.ToUpperInvariant());
                aura.ApplyStaticColor(r, g, b);
                Log.Information("Done — colors are committed (persist across reboot).");
            }
        }

        return 0;
    }

    private static string? GetString(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
