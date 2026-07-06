using DeviceMaster.Sensors;
using Serilog;

namespace DeviceMaster.App.Commands;

/// <summary>
/// CLI verification for motherboard SuperIO headers and GPU coolers (LHM Control sensors).
/// `headers status` lists controls, `headers set --duty N [--id X] [--seconds S]` writes and
/// watches RPMs, then always restores BIOS/driver control before exiting.
/// </summary>
internal static class HeaderCommands
{
    public static int Run(string[] args)
    {
        if (!LhmFanController.IsElevated)
        {
            Log.Error("Motherboard/GPU fan control needs an elevated terminal (SuperIO kernel driver).");
            return 1;
        }

        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        using var controller = new LhmFanController();

        var fans = controller.Enumerate();
        Log.Information("Motherboard: {Board} | GPU: {Gpu}", controller.MotherboardName ?? "?", controller.GpuName ?? "?");
        if (fans.Count == 0)
        {
            Log.Warning("No writable fan controls found — SuperIO chip unsupported by LHM, or driver blocked.");
            return 1;
        }

        PrintStatus(fans);
        if (sub == "status")
        {
            return 0;
        }

        if (sub != "set")
        {
            Log.Error("Usage: headers status | headers set --duty N [--id X] [--seconds S]");
            return 1;
        }

        var duty = GetInt(args, "--duty") ?? 50;
        var id = GetString(args, "--id");
        var seconds = GetInt(args, "--seconds") ?? 8;

        try
        {
            if (id is null)
            {
                Log.Information("Writing {Duty}% to ALL {Count} controls (floored at 30%) for {Seconds}s…", duty, fans.Count, seconds);
                controller.SetAllDuties(duty);
            }
            else
            {
                Log.Information("Writing {Duty}% to {Id} for {Seconds}s…", duty, id, seconds);
                controller.SetDuty(id, duty);
            }

            for (var i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);
                PrintStatus(controller.Enumerate(), $"t+{i + 1}s");
            }
        }
        finally
        {
            Log.Information("Restoring BIOS/driver-automatic control on every touched header…");
            controller.RestoreAll();
        }

        Thread.Sleep(2000);
        PrintStatus(controller.Enumerate(), "after restore");
        return 0;
    }

    private static void PrintStatus(IReadOnlyList<HeaderFan> fans, string? label = null)
    {
        Console.WriteLine(label is null ? "" : $"  --- {label} ---");
        foreach (var fan in fans)
        {
            Console.WriteLine($"  [{fan.Family,-11}] {fan.Name,-28} {(fan.Rpm is { } r ? $"{r,5} rpm" : "  no tach"),-9}  duty={(fan.CurrentPercent is { } p ? $"{p:F0}%" : "?")}  id={fan.Id}");
        }
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

    private static int? GetInt(string[] args, string name) =>
        int.TryParse(GetString(args, name), out var value) ? value : null;
}
