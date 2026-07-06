using DeviceMaster.Control;
using Serilog;

namespace DeviceMaster.App.Commands;

internal static class ControlCommands
{
    public static int Run(string[] args)
    {
        var mode = GetString(args, "--mode") ?? "curve";
        var duty = GetInt(args, "--duty") ?? 50;
        var seconds = GetInt(args, "--seconds") ?? 15;
        var source = GetString(args, "--source") ?? "coolant";

        var settings = new ControlSettings
        {
            Mode = mode.Equals("manual", StringComparison.OrdinalIgnoreCase) ? ControlMode.Manual : ControlMode.Curve,
            ManualDutyPercent = duty,
            Source = Enum.TryParse<CurveSource>(source, ignoreCase: true, out var parsed) ? parsed : CurveSource.Coolant,
        };

        Log.Information("Control loop: mode={Mode} source={Source} for {Seconds}s (Ctrl+C safe — hardware reverts)",
            settings.Mode, settings.Source, seconds);

        using var loop = new ControlLoop(settings, m => Log.Debug("{Message}", m));
        loop.Start();

        for (var i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            var status = loop.Status;
            if (!status.Running)
            {
                continue;
            }

            var temp = status.SourceTemperatureC is { } t ? $"{t:F1}°C" : "n/a";
            var families = status.Devices.GroupBy(d => d.Family)
                .Select(g =>
                {
                    var rpms = g.Where(d => d.Rpm is not null && !d.IsPump).Select(d => d.Rpm!.Value).ToList();
                    return $"{g.Key}: {g.Count(d => !d.IsPump)} fan targets"
                        + (rpms.Count > 0 ? $" ~{rpms.Average():F0} rpm" : "");
                });
            var failsafe = status.FailsafeActive ? "  [FAILSAFE]" : "";
            Console.WriteLine($"  t+{i + 1,2}s  {status.SourceName}={temp} -> {status.TargetDutyPercent}%{failsafe}  |  {string.Join("  |  ", families)}");
            foreach (var warning in status.Warnings)
            {
                Log.Warning("{Warning}", warning);
            }
        }

        Log.Information("Stopping control loop (Corsair hubs -> hardware mode, SL V3 reverts on its own)");
        loop.Stop();
        return 0;
    }

    private static string? GetString(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int? GetInt(string[] args, string name) =>
        int.TryParse(GetString(args, name), out var value) ? value : null;
}
