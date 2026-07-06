using System.Globalization;
using DeviceMaster.Devices.Nvidia;
using DeviceMaster.Sensors;
using Serilog;

namespace DeviceMaster.App.Commands;

/// <summary>
/// CLI verification for the chip-level RGB paths:
/// `ramrgb status|set --color RRGGBB` — ENE controllers on RGB DRAM (elevated, PawnIO).
/// `gpurgb status|set --color RRGGBB` — board-partner controller on the GPU I2C bus.
/// </summary>
internal static class RgbChipCommands
{
    public static int RunRam(string[] args)
    {
        if (!LhmFanController.IsElevated)
        {
            Log.Error("RAM RGB needs an elevated terminal (SMBus access via PawnIO).");
            return 1;
        }

        using var scanner = new EneRamScanner(m => Log.Information("{Message}", m));

        var sticks = scanner.ReadSticks();
        Log.Information("SPD: {Count} memory module(s)", sticks.Count);
        foreach (var stick in sticks)
        {
            Console.WriteLine($"  0x{stick.SpdAddress:X2} on {stick.BusName}: {stick.Manufacturer} {stick.PartNumber}");
        }

        var controllers = scanner.FindRgbControllers();
        Log.Information("ENE RGB: {Count} controller(s)", controllers.Count);

        if (Sub(args) == "set" && controllers.Count > 0)
        {
            var (r, g, b) = ParseColor(args);
            foreach (var controller in controllers)
            {
                controller.ApplyStaticColor(r, g, b, persist: true);
                Log.Information("static color applied to ENE 0x{Addr:X2} ({Leds} LEDs)", controller.Address, controller.LedCount);
            }
        }

        return 0;
    }

    public static int RunGpu(string[] args)
    {
        var gpus = GpuRgbLocator.Scan(m => Log.Information("{Message}", m));
        if (gpus.Count == 0)
        {
            Log.Error("No NVIDIA GPU visible via NvAPI.");
            return 1;
        }

        if (Sub(args) == "set")
        {
            var (r, g, b) = ParseColor(args);
            foreach (var gpu in gpus.Where(g2 => g2.Ene is not null))
            {
                gpu.Ene!.ApplyStaticColor(r, g, b, persist: true);
                Log.Information("static color applied to {Gpu} ({Leds} LEDs)", gpu.Gpu.Name, gpu.Ene.LedCount);
            }
        }

        return 0;
    }

    private static string Sub(string[] args) => args.Length > 1 ? args[1].ToLowerInvariant() : "status";

    private static (byte R, byte G, byte B) ParseColor(string[] args)
    {
        var hex = "00FF00";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--color", StringComparison.OrdinalIgnoreCase))
            {
                hex = args[i + 1];
            }
        }

        var rgb = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
