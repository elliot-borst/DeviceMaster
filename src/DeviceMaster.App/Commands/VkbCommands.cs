using System.Globalization;
using DeviceMaster.Devices.Vkb;
using Serilog;

namespace DeviceMaster.App.Commands;

/// <summary>
/// CLI for VKB joystick LEDs (overrides last until the stick is power-cycled).
/// <list type="bullet">
/// <item><c>vkb status</c> — list sticks and the overrides currently set</item>
/// <item><c>vkb set --led N[,N…] --color RRGGBB [--mode constant|off|slow|fast|ultra] [--pid 0200]</c></item>
/// <item><c>vkb sweep [--from 0] [--to 20] [--ms 1500] [--pid 0200]</c> — light each LED id in turn (green) to map them</item>
/// </list>
/// </summary>
internal static class VkbCommands
{
    public static int Run(string[] args)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        var pid = Get(args, "--pid");
        var devices = VkbController.FindDevices()
            .Where(d => pid is null || d.ProductID.ToString("X4", CultureInfo.InvariantCulture).Equals(pid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (devices.Count == 0)
        {
            Log.Error("No VKB device with the LED report (231D:*, feature 0x59) found.");
            return 1;
        }

        foreach (var device in devices)
        {
            using var vkb = VkbController.Open(device);
            var overrides = vkb.ReadOverrides();
            Log.Information("{Name} ({Id}): {Count} LED override(s) set", vkb.Name, vkb.UsbId, overrides.Count);
            foreach (var o in overrides)
            {
                Console.WriteLine($"  led {o.Led}: {o.LedMode} {o.ColorMode} c1=({o.Color1.R},{o.Color1.G},{o.Color1.B}) c2=({o.Color2.R},{o.Color2.G},{o.Color2.B})");
            }

            switch (sub)
            {
                case "set":
                {
                    var leds = (Get(args, "--led") ?? "0").Split(',').Select(s => byte.Parse(s, CultureInfo.InvariantCulture)).ToList();
                    var rgb = int.Parse(Get(args, "--color") ?? "00FF00", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var mode = (Get(args, "--mode") ?? "constant").ToLowerInvariant() switch
                    {
                        "off" => VkbLedMode.Off,
                        "slow" => VkbLedMode.SlowBlink,
                        "fast" => VkbLedMode.FastBlink,
                        "ultra" => VkbLedMode.UltraBlink,
                        _ => VkbLedMode.Constant,
                    };
                    var color = VkbColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                    vkb.SetLeds(leds.Select(id => new VkbLedConfig(id, VkbColorMode.Color1, mode, color, color)).ToList());
                    Log.Information("Set LED(s) {Leds} to {Mode} ({R},{G},{B}) — lasts until the stick is power-cycled", string.Join(",", leds), mode, color.R, color.G, color.B);
                    break;
                }

                case "sweep":
                {
                    var from = int.Parse(Get(args, "--from") ?? "0", CultureInfo.InvariantCulture);
                    var to = int.Parse(Get(args, "--to") ?? "20", CultureInfo.InvariantCulture);
                    var ms = int.Parse(Get(args, "--ms") ?? "1500", CultureInfo.InvariantCulture);
                    var green = new VkbColor(0, 7, 0);
                    for (var id = from; id <= to; id++)
                    {
                        Log.Information("{Name}: LED id {Id} → green", vkb.Name, id);
                        vkb.SetLeds([new VkbLedConfig((byte)id, VkbColorMode.Color1, VkbLedMode.Constant, green, green)]);
                        Thread.Sleep(ms);
                        vkb.SetLeds([new VkbLedConfig((byte)id, VkbColorMode.Color1, VkbLedMode.Off, green, green)]);
                    }

                    break;
                }
            }
        }

        return 0;
    }

    private static string? Get(string[] args, string name)
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
