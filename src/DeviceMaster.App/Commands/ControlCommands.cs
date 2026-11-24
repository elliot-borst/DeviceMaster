using DeviceMaster.Control;
using Serilog;

namespace DeviceMaster.App.Commands;

internal static class ControlCommands
{
    public static int Run(string[] args)
    {
        // --duty N runs a flat curve at N% (there is no fixed mode, #116); without it, the default curve
        var duty = GetInt(args, "--duty");
        var seconds = GetInt(args, "--seconds") ?? 15;
        var source = GetString(args, "--source") ?? "hottest";

        var settings = new ControlSettings
        {
            Mode = ControlMode.Curve,
            Source = Enum.TryParse<CurveSource>(source, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : CurveSource.Hottest,
        };
        if (duty is { } flat)
        {
            settings.SetCurve(DeviceMaster.Core.Curves.CurveRules.Flat(flat));
        }

        Log.Information("Control loop: source={Source} curve={Curve} for {Seconds}s (Ctrl+C safe — hardware reverts)",
            settings.Source, string.Join(" ", settings.CurvePoints.Select(p => $"{p.TemperatureC:0}°C:{p.DutyPercent}%")), seconds);

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
                    var rpms = g.Where(d => d.Rpm is not null).Select(d => d.Rpm!.Value).ToList();
                    return $"{g.Key}: {g.Count()} fan targets"
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
