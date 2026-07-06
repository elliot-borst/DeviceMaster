using DeviceMaster.Devices.LianLi;
using Serilog;

namespace DeviceMaster.App.Commands;

internal static class Slv3Commands
{
    public static int Run(string[] args)
    {
        var subcommand = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        return subcommand switch
        {
            "status" => Status(),
            "set" => Set(GetInt(args, "--duty"), GetInt(args, "--hold") ?? 10),
            "bind" => Bind(GetString(args, "--mac")),
            "probe" => Probe(),
            _ => Usage(),
        };
    }

    private static int Status()
    {
        using var controller = Slv3Controller.Open(trace: m => Log.Debug("{Trace}", m));
        Log.Information("SL V3 TX dongle: master MAC {Mac}, RF channel {Channel}, firmware {Firmware}",
            controller.MasterMacText, controller.MasterChannel, controller.MasterFirmware);

        // The RX answers intermittently, and fans check in against the master heartbeat —
        // keep the clock on air while polling (the reference daemon always does).
        for (var i = 0; i < 30 && controller.Devices.Count == 0; i++)
        {
            if (i == 6)
            {
                Log.Information("RX silent — trying RF engine activation (VIDEO_START, reference soft-reset path)");
                controller.ActivateRfEngine();
            }

            if (i == 14)
            {
                Log.Information("RX still silent — sending RX reset (reference recovery step)");
                controller.ResetRx();
            }

            controller.SendMasterClock();
            controller.PollDevices();
            Thread.Sleep(700);
        }

        var devices = controller.PollDevices();
        Log.Information("Motherboard PWM readback: {Pwm}",
            controller.MotherboardPwm is { } p ? $"{p}/255" : "unavailable");

        if (devices.Count == 0)
        {
            Log.Warning("RX dongle reported no wireless devices (fans may need a moment after boot)");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"  SL V3 wireless devices ({devices.Count}):");
        foreach (var device in devices)
        {
            var bound = device.IsBoundTo(controller.MasterMac) ? "bound" : "NOT BOUND to this dongle";
            var rpms = string.Join("/", device.FanRpms.Take(Math.Max((int)device.FanCount, 1)));
            var coolant = device.CoolantTempC is { } t ? $"  coolant={t}°C" : "";
            Console.WriteLine($"    [{device.ListIndex}] {device.MacText}  fans={device.FanCount}  rpm={rpms}  "
                + $"pwm={device.CurrentPwm[0]}/255  type=0x{device.DeviceType:X2}  ch={device.Channel} rx={device.RxType}  ({bound}){coolant}");
        }

        Console.WriteLine();
        return 0;
    }

    private static int Set(int? duty, int holdSeconds)
    {
        if (duty is null)
        {
            Log.Error("--duty <0-100> is required");
            return 1;
        }

        using var controller = Slv3Controller.Open(trace: m => Log.Debug("{Trace}", m));
        Log.Information("SL V3 TX: master {Mac} on channel {Channel}", controller.MasterMacText, controller.MasterChannel);

        for (var i = 0; i < 5 && controller.Devices.Count == 0; i++)
        {
            controller.PollDevices();
            Thread.Sleep(300);
        }

        var bound = controller.Devices.Where(d => d.IsBoundTo(controller.MasterMac)).ToList();
        if (bound.Count == 0)
        {
            Log.Error("No bound SL V3 devices found — nothing to control");
            return 1;
        }

        Log.Information("Holding {Count} fan group(s) at {Duty}% for {Hold}s (keepalive every second; "
            + "fans revert to firmware defaults a moment after we stop)", bound.Count, duty, holdSeconds);

        var end = DateTime.UtcNow.AddSeconds(holdSeconds);
        while (DateTime.UtcNow < end)
        {
            controller.SendKeepalive(duty.Value);
            Thread.Sleep(700);
            var devices = controller.PollDevices();
            var line = string.Join("  ", devices
                .Where(d => d.IsBoundTo(controller.MasterMac))
                .Select(d => $"{d.MacText[..4]}:{string.Join("/", d.FanRpms.Take(Math.Max((int)d.FanCount, 1)))}rpm@{d.CurrentPwm[0]}"));
            Console.WriteLine($"    {DateTime.Now:HH:mm:ss}  {line}");
        }

        Log.Information("Stopped sending PWM — firmware reverts to its default speed within a few seconds");
        return 0;
    }

    /// <summary>Manually re-pairs a group to this dongle (exit the DeviceMaster app first).</summary>
    private static int Bind(string? macPrefix)
    {
        if (string.IsNullOrWhiteSpace(macPrefix))
        {
            Log.Error("--mac <hex prefix> is required (see `slv3 status` for group MACs)");
            return 1;
        }

        using var controller = Slv3Controller.Open(trace: m => Log.Debug("{Trace}", m));
        for (var i = 0; i < 10 && controller.Devices.Count == 0; i++)
        {
            controller.PollDevices();
            Thread.Sleep(300);
        }

        var device = controller.Devices.FirstOrDefault(
            d => d.MacText.StartsWith(macPrefix, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            Log.Error("No group with MAC prefix {Prefix} in telemetry ({Count} device(s) visible)",
                macPrefix, controller.Devices.Count);
            return 1;
        }

        var ok = controller.BindDevice(device, m => Log.Information("{Message}", m));
        return ok ? 0 : 1;
    }

    private static int Probe()
    {
        using var controller = Slv3Controller.Open(trace: m => Log.Debug("{Trace}", m));

        (string Name, bool ToTx, byte[] Head)[] probes =
        [
            ("TX GetDev 10 01", true, [0x10, 0x01]),
            ("TX 10 01 04 34", true, [0x10, 0x01, 0x04, 0x34]),
            ("TX 11 02 (info?)", true, [0x11, 0x02]),
            ("RX GetDev 10 01", false, [0x10, 0x01]),
            ("RX GetDev page2 10 02", false, [0x10, 0x02]),
            ("RX 10 01 04 30", false, [0x10, 0x01, 0x04, 0x30]),
            ("RX 10 01 04 31", false, [0x10, 0x01, 0x04, 0x31]),
            ("RX 11 01 (get mac?)", false, [0x11, 0x01]),
            ("RX 11 08 (self mac?)", false, [0x11, 0x08]),
        ];

        foreach (var (name, toTx, head) in probes)
        {
            try
            {
                var (length, data) = controller.ProbeRaw(toTx, head);
                var preview = length > 0 ? Convert.ToHexString(data.AsSpan(0, Math.Min(length, 48))) : "(timeout)";
                Console.WriteLine($"  {name,-26} -> len={length,4}  {preview}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name,-26} -> ERROR {ex.Message}");
            }

            Thread.Sleep(150);
        }

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

    private static int Usage()
    {
        Console.WriteLine("usage:");
        Console.WriteLine("  slv3 status                      master MAC, wireless fan groups, RPMs, mobo PWM");
        Console.WriteLine("  slv3 set --duty P [--hold S]     hold all fan groups at P% (keepalive loop), then release");
        Console.WriteLine("  slv3 bind --mac PREFIX           re-pair a group to this dongle (app must be exited)");
        return 1;
    }
}
