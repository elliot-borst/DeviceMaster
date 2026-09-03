using System.Text.Json;
using DeviceMaster.App.Headless;
using DeviceMaster.Control;
using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.CorsairLink.Protocol;
using DeviceMaster.Devices.EneRgb;
using DeviceMaster.Platform.Linux;
using DeviceMaster.Sensors.Linux;
using Serilog;

namespace DeviceMaster.App.Headless;

/// <summary>
/// Headless (Linux/container) entry point. Same device sessions and safety primitives as the
/// Windows app; no tray, no WPF — console commands plus the `loop` daemon that runs in Docker.
///
///   devicemaster discover                        enumerate hubs, LCD, GPU i2c, sensors
///   devicemaster status                          hub tree, RPMs, temps (read; restores hardware mode)
///   devicemaster speed --duty 65 [--pump 80] [--hold 10]
///   devicemaster rgb --hex 8000FF [--off] [--gpu]
///   devicemaster ene [--hex 8000FF] [--persist] [--i2c /dev/i2c-10]
///   devicemaster lcd metrics [--metric CPU_TEMP] [--hold 10] [--brightness 80]
///   devicemaster loop [--config /config/config.json]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                standardErrorFromLevel: Serilog.Events.LogEventLevel.Warning)
            .CreateLogger();

        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var command = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToList();
            var trace = rest.Remove("--trace");
            var log = (Action<string>)(msg => Log.Information("{msg}", msg));

            return command switch
            {
                "discover" => RunDiscover(),
                "status" => RunStatus(trace),
                "speed" => RunSpeed(rest, trace),
                "rgb" => RunRgb(rest, trace),
                "ene" => RunEne(rest),
                "lcd" => RunLcd(rest, trace),
                "loop" => RunLoop(rest, trace),
                "web" => RunWeb(rest, trace),
                "help" or "--help" or "-h" => PrintUsageAnd(0),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "headless: unhandled error");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            DeviceMaster (headless / Linux)

              discover                          enumerate hubs, pump LCD, GPU i2c, sensors
              status                            hub tree with RPMs/temps; restores hardware mode
              speed --duty N [--pump-duty M] [--hold SECONDS]
              rgb --hex 8000FF [--off] [--gpu]
              ene [--hex 8000FF] [--persist] [--i2c /dev/i2c-N]
              lcd <off|black|white|metrics> [--metric COOLANT|CPU_TEMP|GPU_TEMP|FAN_DUTY|PUMP_DUTY|PUMP_RPM]
                  [--hold SECONDS] [--brightness PERCENT]
              loop [--config PATH]              run the 1 Hz control loop
              web [--config PATH] --port 27004  control loop + web dashboard (/, /status.json)

            Global: --trace (packet-level hub traffic)
            Config: DEVICEMASTER_CONFIG env var or --config (defaults to /config/config.json)
            """);
    }

    private static int PrintUsageAnd(int code)
    {
        PrintUsage();
        return code;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'");
        PrintUsage();
        return 1;
    }

    // ---- helpers ----

    private static bool Remove(this List<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        if (index < 0)
        {
            return false;
        }

        args.RemoveAt(index);
        return true;
    }

    private static string? GetOption(List<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                var value = args[i + 1];
                args.RemoveRange(i, 2);
                return value;
            }
        }

        return null;
    }

    private static (int R, int G, int B) ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"Bad hex color '{hex}' (expected RRGGBB).");
        }

        return (Convert.ToInt32(hex[0..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16));
    }

    private static IReadOnlyList<LinkHub> OpenHubs(Action<string>? trace, out List<string> warnings)
    {
        warnings = [];
        var hubs = new List<LinkHub>();
        foreach (var device in LinkHub.FindHubDevices())
        {
            try
            {
                var hub = LinkHub.Open(device, trace);
                hub.EnumerateChannels(allowEnterSoftwareMode: true);
                hubs.Add(hub);
            }
            catch (Exception ex)
            {
                warnings.Add($"hub open failed: {ex.Message}");
            }
        }

        return hubs;
    }

    // ---- commands ----

    private static int RunDiscover()
    {
        Console.WriteLine("== iCUE LINK hubs ==");
        var foundAny = false;
        foreach (var device in LinkHub.FindHubDevices())
        {
            foundAny = true;
            try
            {
                Console.WriteLine($"  {device.GetSerialNumber()[..12]}…  fw interface: output reports (MI_00)");
            }
            catch
            {
            }
        }

        if (!foundAny)
        {
            Console.WriteLine("  (none — check --privileged / hidraw visibility)");
        }

        Console.WriteLine("== pump/res LCD ==");
        var lcds = CorsairLcdDevice.FindDevices();
        if (lcds.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        foreach (var lcd in lcds)
        {
            try
            {
                Console.WriteLine($"  {lcd.GetSerialNumber()}");
            }
            catch
            {
            }
        }

        Console.WriteLine("== NVIDIA i2c adapters (GPU RGB) ==");
        var gpus = GpuI2cLocator.FindAll();
        if (gpus.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        foreach (var gpu in gpus)
        {
            Console.WriteLine($"  {gpu.DevicePath}  {gpu.Name}  pci={gpu.PciAddress}");
        }

        Console.WriteLine("== CPU sensors (hwmon) ==");
        foreach (var temp in Hwmon.ReadTemperatures())
        {
            Console.WriteLine($"  {temp.Sensor}/{temp.Key}: {temp.ValueC:F1} °C");
        }

        Console.WriteLine("== GPU (nvidia-smi) ==");
        var gpuInfo = NvidiaSmi.ReadFirst();
        if (gpuInfo is { } g)
        {
            var power = g.PowerW is { } w ? $", {w:F0} W" : "";
            Console.WriteLine($"  {g.Name}: {g.TemperatureC} °C, util {g.UtilizationPercent}%{power}");
        }
        else
        {
            Console.WriteLine("  (nvidia-smi unavailable)");
        }

        return 0;
    }

    private static int RunStatus(bool trace)
    {
        var hubs = OpenHubs(trace ? Console.WriteLine : null, out var warnings);
        if (hubs.Count == 0)
        {
            Console.WriteLine("No iCUE LINK hubs found.");
            return 1;
        }

        foreach (var warning in warnings)
        {
            Console.WriteLine($"  warning: {warning}");
        }

        foreach (var hub in hubs)
        {
            Console.WriteLine($"\nhub {hub.SerialNumber}  (fw {hub.FirmwareVersion}, {hub.TotalLeds} LEDs)");
            var speeds = SafeSpeeds(hub);
            var temps = SafeTemps(hub);
            foreach (var channel in hub.Channels)
            {
                var speed = speeds?.FirstOrDefault(s => s.Channel == channel.Channel);
                var temp = temps?.FirstOrDefault(t => t.Channel == channel.Channel);
                var rpm = speed is { IsAvailable: true } ? speed.Rpm.ToString() : "--";
                var tempStr = temp is { IsAvailable: true } ? $"{temp.TemperatureCelsius:F1} °C" : "";
                Console.WriteLine($"  ch {channel.Channel,2}  {channel.Name,-28}  {rpm,-6} {tempStr}");
            }
        }

        Console.WriteLine("\n(hardware mode restored)");

        // Dispose restores hardware mode
        foreach (var hub in hubs)
        {
            hub.Dispose();
        }

        return 0;
    }

    private static IReadOnlyList<LinkSpeedReading>? SafeSpeeds(LinkHub hub)
    {
        try
        {
            return hub.ReadSpeeds();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<LinkTemperatureReading>? SafeTemps(LinkHub hub)
    {
        try
        {
            return hub.ReadTemperatures();
        }
        catch
        {
            return null;
        }
    }

    private static int RunSpeed(List<string> args, bool trace)
    {
        var dutyText = GetOption(args, "--duty") ?? GetOption(args, "--duty");
        if (dutyText is null || !int.TryParse(dutyText, out var duty))
        {
            Console.WriteLine("speed: --duty N required (0-100)");
            return 1;
        }

        var pumpText = GetOption(args, "--pump-duty") ?? GetOption(args, "--pump");
        var pumpDuty = pumpText is not null && int.TryParse(pumpText, out var p) ? p : 100;
        var holdText = GetOption(args, "--hold");
        var holdSeconds = holdText is not null && int.TryParse(holdText, out var h) ? h : 0;

        var hubs = OpenHubs(trace ? Console.WriteLine : null, out var warnings);
        if (hubs.Count == 0)
        {
            Console.WriteLine("No iCUE LINK hubs found.");
            return 1;
        }

        try
        {
            foreach (var hub in hubs)
            {
                var requested = new Dictionary<int, int>();
                foreach (var channel in hub.Channels)
                {
                    if (!channel.IsPump && channel.Info is { Flags: var f }
                        && f.HasFlag(LinkDeviceFlags.ControlsSpeed))
                    {
                        requested[channel.Channel] = duty;
                    }
                }

                var written = hub.WriteFixedDuties(requested, pumpDuty);
                Console.WriteLine($"hub {hub.SerialNumber[..12]}…: {written} channels at fans={duty}% pump={pumpDuty}%");
            }

            if (holdSeconds > 0)
            {
                Console.WriteLine($"holding for {holdSeconds} s…");
                Thread.Sleep(holdSeconds * 1000);
            }
        }
        finally
        {
            foreach (var hub in hubs)
            {
                hub.Dispose(); // restores hardware mode
            }
        }

        Console.WriteLine("(hardware mode restored)");
        return 0;
    }

    private static int RunRgb(List<string> args, bool trace)
    {
        var hex = GetOption(args, "--hex") ?? "8000FF";
        var off = args.Remove("--off");
        var gpu = args.Remove("--gpu");
        var (r, g, b) = off ? (0, 0, 0) : ParseHex(hex);

        var hubs = OpenHubs(trace ? Console.WriteLine : null, out var warnings);
        if (hubs.Count == 0)
        {
            Console.WriteLine("No iCUE LINK hubs found.");
            return 1;
        }

        try
        {
            foreach (var hub in hubs)
            {
                hub.ApplyStaticColor((byte)r, (byte)g, (byte)b);
                Console.WriteLine($"hub {hub.SerialNumber[..12]}…: {hub.TotalLeds} LEDs -> #{hex}{(off ? " (off)" : "")}");
            }
        }
        finally
        {
            foreach (var hub in hubs)
            {
                hub.Dispose();
            }
        }

        if (gpu)
        {
            return EneApply(r, g, b, persist: false, i2cOverride: null);
        }

        return 0;
    }

    private static int RunEne(List<string> args)
    {
        var hex = GetOption(args, "--hex") ?? "8000FF";
        var persist = args.Remove("--persist");
        var i2c = GetOption(args, "--i2c");
        var (r, g, b) = ParseHex(hex);
        return EneApply(r, g, b, persist, i2c);
    }

    private static int EneApply(int r, int g, int b, bool persist, string? i2cOverride)
    {
        string? path = i2cOverride ?? GpuI2cLocator.Find()?.DevicePath;
        if (path is null)
        {
            Console.WriteLine("No NVIDIA i2c adapter found (is the nvidia driver loaded? run with --privileged).");
            return 1;
        }

        using var bus = new LinuxI2cSmBus(path);
        if (!EneRgbDevice.Fingerprint(bus, 0x67))
        {
            Console.WriteLine($"No ENE controller at 0x67 on {path}.");
            return 1;
        }

        var device = new EneRgbDevice(bus, 0x67);
        if (!device.Initialize())
        {
            Console.WriteLine($"ENE at 0x67 on {path} did not initialize.");
            return 1;
        }

        device.ApplyStaticColor((byte)r, (byte)g, (byte)b, persist);
        Console.WriteLine($"GPU ENE ({device.Version}, {device.LedCount} LEDs on {path}) -> #{r:X2}{g:X2}{b:X2}{(persist ? " [persisted to flash]" : "")}");
        return 0;
    }

    private static int RunLcd(List<string> args, bool trace)
    {
        if (args.Count == 0)
        {
            Console.WriteLine("lcd: <off|black|white|metrics> required");
            return 1;
        }

        var modeText = args[0].ToLowerInvariant();
        args.RemoveAt(0);
        var holdText = GetOption(args, "--hold");
        var holdSeconds = holdText is not null && int.TryParse(holdText, out var h) ? h : 10;
        var brightnessText = GetOption(args, "--brightness");
        var brightness = brightnessText is not null && int.TryParse(brightnessText, out var b) ? b : 100;
        var metricText = GetOption(args, "--metric");

        var device = CorsairLcdDevice.FindDevices().FirstOrDefault();
        if (device is null)
        {
            Console.WriteLine("No pump/res LCD found.");
            return 1;
        }

        using var lcd = CorsairLcdDevice.Open(device);
        lcd.SetBrightness(modeText == "off" ? 0 : brightness);

        switch (modeText)
        {
            case "off":
                Console.WriteLine("backlight off");
                break;

            case "black":
                lcd.SendJpegFrame(LcdFrames.Solid(480, 480, 0, 0, 0));
                Console.WriteLine("solid black frame");
                break;

            case "white":
                lcd.SendJpegFrame(LcdFrames.Solid(480, 480, 255, 255, 255));
                Console.WriteLine("solid white frame");
                break;

            case "metrics":
                var metric = metricText?.ToUpperInvariant() switch
                {
                    "CPU_TEMP" => LcdMetric.CpuTemp,
                    "GPU_TEMP" => LcdMetric.GpuTemp,
                    "FAN_DUTY" => LcdMetric.FanDuty,
                    "PUMP_DUTY" => LcdMetric.PumpDuty,
                    "PUMP_RPM" => LcdMetric.PumpRpm,
                    _ => LcdMetric.CpuTemp,
                };
                var cpu = Hwmon.CpuTemperatureC();
                var gpu = NvidiaSmi.ReadFirst();
                var (label, value, unit) = metric switch
                {
                    LcdMetric.CpuTemp => ("CPU", cpu?.ToString("0") ?? "--", "°C"),
                    LcdMetric.GpuTemp => ("GPU", gpu?.TemperatureC.ToString("0") ?? "--", "°C"),
                    _ => ("CPU", cpu?.ToString("0") ?? "--", "°C"),
                };
                lcd.SendJpegFrame(LcdMetricRenderer.Render(480, 480, label, value, unit, (128, 0, 255)));
                Console.WriteLine($"metrics frame: {label} {value} {unit}");
                break;

            default:
                Console.WriteLine($"lcd: unknown mode '{modeText}'");
                return 1;
        }

        Console.WriteLine($"held {holdSeconds} s (panel keeps the frame)");
        Thread.Sleep(holdSeconds * 1000);
        return 0;
    }

    /// <summary>Shared loop bootstrap (loop/web): env var, config load, defaults write, start.</summary>
    private static HeadlessLoop StartLoop(string configPath, bool trace, Action<string> log)
    {
        // the loop reads DEVICEMASTER_CONFIG / its default path itself — point it at --config
        Environment.SetEnvironmentVariable("DEVICEMASTER_CONFIG", configPath);
        var config = HeadlessConfig.Load(configPath);
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"no config at {configPath} — writing defaults (edit and the loop picks them up live)");
            var dir = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (dir is { Length: > 0 })
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(configPath, config.Save());
        }

        config.Trace |= trace;
        var loop = new HeadlessLoop(config, log);
        loop.Start();
        return loop;
    }

    private static int RunLoop(List<string> args, bool trace)
    {
        var configPath = GetOption(args, "--config") ?? HeadlessConfig.DefaultPath;
        if (!args.All(string.IsNullOrWhiteSpace))
        {
            Console.WriteLine("loop: unexpected arguments: " + string.Join(' ', args));
            return 1;
        }

        var log = (Action<string>)(msg => Log.Information("{msg}", msg));
        using var loop = StartLoop(configPath, trace, log);

        SignalWatcher.Install();

        Log.Information("headless: running (config {path}, Ctrl-C or SIGTERM to stop)", configPath);
        try
        {
            while (!SignalWatcher.StopRequested)
            {
                Thread.Sleep(500);
            }

            Log.Information("stop requested — restoring hubs to hardware mode");
        }
        finally
        {
            loop.Stop();
        }

        return 0;
    }

    /// <summary>Control loop + tiny web dashboard (the container's recommended mode).</summary>
    private static int RunWeb(List<string> args, bool trace)
    {
        var configPath = GetOption(args, "--config") ?? HeadlessConfig.DefaultPath;
        var portText = GetOption(args, "--port") ?? "27004";
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            Console.WriteLine("web: --port must be a number in 1-65535");
            return 1;
        }

        // --config and --port are the only flags
        var leftover = args.Where(a => a != "--config" && a != configPath
                                        && a != "--port" && a != portText
                                        && !string.IsNullOrWhiteSpace(a)).ToList();
        if (leftover.Count > 0)
        {
            Console.WriteLine("web: unexpected arguments: " + string.Join(' ', leftover));
            return 1;
        }

        var log = (Action<string>)(msg => Log.Information("{msg}", msg));
        using var loop = StartLoop(configPath, trace, log);
        using var web = new HeadlessWebServer(loop, port, log);

        SignalWatcher.Install();

        web.Start();
        Log.Information("headless: running with web dashboard (config {path}, Ctrl-C or SIGTERM to stop)", configPath);
        try
        {
            while (!SignalWatcher.StopRequested)
            {
                Thread.Sleep(500);
            }

            Log.Information("stop requested — restoring hubs to hardware mode");
        }
        finally
        {
            loop.Stop();
        }

        return 0;
    }
}
