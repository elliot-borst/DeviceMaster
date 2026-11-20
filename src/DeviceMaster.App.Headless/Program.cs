using DeviceMaster.Hub;
using System.Text.Json;
using DeviceMaster.App.Headless;
using DeviceMaster.Control;
using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.CorsairLink.Protocol;
using DeviceMaster.Devices.EneRgb;
using DeviceMaster.Devices.Turzx;
using DeviceMaster.Platform.Linux;
using DeviceMaster.Sensors.Linux;
using Serilog;

namespace DeviceMaster.App.Headless;

/// <summary>
/// Headless (Linux/container) entry point. Same device sessions and safety primitives as the
/// Windows app; no tray, no WPF — console commands plus the `loop` daemon that runs in Docker.
///
///   devicemaster discover                        enumerate hubs, GPU i2c, sensors
///   devicemaster status                          hub tree, RPMs, temps (read; restores hardware mode)
///   devicemaster speed --duty 65 [--hold 10]
///   devicemaster rgb --hex 8000FF [--off] [--gpu]
///   devicemaster ene [--hex 8000FF] [--persist] [--i2c /dev/i2c-10]
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
                "loop" => RunLoop(rest, trace),
                "web" => RunWeb(rest, trace),
                "hub" => RunHub(rest),
                "dashboard-preview" => RunDashboardPreview(rest),
                "turzx-usb" => RunTurzxUsb(rest),
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

              discover                          enumerate hubs, GPU i2c, sensors
              status                            hub tree with RPMs/temps; restores hardware mode
              speed --duty N [--hold SECONDS]
              rgb --hex 8000FF [--off] [--gpu]
              ene [--hex 8000FF] [--persist] [--i2c /dev/i2c-N]
              loop [--config PATH]              run the 1 Hz control loop
              web [--config PATH] --port 27004  control loop + web dashboard (/, /status.json)
              hub [--config /config/hub.json] [--state PATH] [--port 27080] [--bind ADDR]  fleet hub: Servers overview + fleet settings (no hardware)

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
                var requested = hub.Channels.Where(c => c.ControlsSpeed).ToDictionary(c => c.Channel, _ => duty);
                var written = hub.WriteFixedDuties(requested);
                Console.WriteLine($"hub {hub.SerialNumber[..12]}…: {written.Count} channels at fans={duty}%");
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
    /// <summary>Renders the Turzx 8.8" dashboard with sample figures to a PNG (layout check without a panel).</summary>
    private static int RunDashboardPreview(List<string> args)
    {
        var output = GetOption(args, "--out") ?? "dashboard.png";
        var sample = SampleDashboard(args.Contains("--busy"), llm: !args.Contains("--no-llm"));
        File.WriteAllBytes(output, DeviceMaster.Lcd.Skia.TurzxDashboard.RenderPng(sample));
        var native = DeviceMaster.Lcd.Skia.TurzxDashboard.RenderNative(sample, flip: false);
        Console.WriteLine($"wrote {output} (landscape 1920×480); native frame {native.Length} bytes");
        return 0;
    }

    /// <summary>
    /// Hardware test for a Turzx 8.8" V1.x USB panel (#102): finds it, handshakes, sets the
    /// brightness and shows the sample dashboard for a while, timing each frame. Run it while the
    /// loop isn't managing the screen (TurzxScreen Unmanaged), or the two would fight over it.
    /// </summary>
    private static int RunTurzxUsb(List<string> args)
    {
        if (DeviceMaster.Platform.Linux.UsbfsTurzxTransport.Find() is not { } node)
        {
            Console.WriteLine("turzx-usb: no Turzx 8.8\" V1.x panel (1CBE:0088, TURZX1.0) found");
            return 1;
        }

        var brightness = int.TryParse(GetOption(args, "--brightness"), out var b) ? b : 80;
        var seconds = int.TryParse(GetOption(args, "--seconds"), out var s) ? s : 10;
        var flip = args.Contains("--flip");
        using var screen = TurzxUsbScreen.Open(DeviceMaster.Platform.Linux.UsbfsTurzxTransport.Open(node));
        Console.WriteLine($"turzx-usb: opened {node}, firmware {screen.Firmware ?? "?"}");
        screen.SetBrightness(brightness);
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var frames = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var jpeg = DeviceMaster.Lcd.Skia.TurzxDashboard.RenderNativeJpeg(SampleDashboard(busy: frames % 2 == 0, llm: !args.Contains("--no-llm")), flip);
            var started = watch.ElapsedMilliseconds;
            screen.SendJpeg(jpeg);
            Console.WriteLine($"turzx-usb: frame {++frames}: {jpeg.Length / 1024} KB in {watch.ElapsedMilliseconds - started} ms");
            Thread.Sleep(2_000);
        }
        while (DateTime.UtcNow < until);

        return 0;
    }

    private static DeviceMaster.Lcd.Skia.TurzxDashboardData SampleDashboard(bool busy, bool llm)
    {
        return new DeviceMaster.Lcd.Skia.TurzxDashboardData
        {
            HasLlm = llm,
            LlmModel = "qwen3.8-flash-next-unsloth-ud-iq3_xxs",
            LlmState = busy ? "generating" : "idle",
            LlmPhase = busy ? "writing a tool call: terminal" : null,
            LlmRunning = busy ? 1 : 0,
            LlmTokPerSec = busy ? 108.4 : 0,
            LlmTokHistory = Enumerable.Range(0, 60).Select(i => busy ? 80 + 30 * Math.Sin(i / 6.0) : 0.0).ToList(),
            // one conversation, run outside the batch slots: every slot reports idle
            LlmSlots = [new("idle", 0), new("idle", 0), new("idle", 0), new("idle", 0)],
            // busy: a reply being written (cache figure known, guess hit only once it's done); idle: the last reply
            LlmReply = busy
                ? new DeviceMaster.Lcd.Skia.DashboardReply(true, false, 1.9, 99, null, 412)
                : new DeviceMaster.Lcd.Skia.DashboardReply(false, false, 1.9, 99, 53, 731),
            LlmContextUsed = busy ? 193_007 : null,
            LlmContextMax = 262_144,
            GpuName = "NVIDIA GeForce RTX 4090",
            GpuTempC = busy ? 67 : 45,
            GpuUtil = busy ? 94 : 0,
            GpuPowerW = busy ? 312 : 15,
            GpuFanPercent = busy ? 41 : 0,
            GpuFanRpm = busy ? 1_450 : 0,
            VramUsedGb = 23.4,
            VramTotalGb = 24,
            CpuName = "AMD Ryzen 7 9800X3D 8-Core Processor",
            CpuTempC = busy ? 71 : 42,
            CpuLoad = busy ? 63 : 2,
            CpuPowerW = busy ? 88 : 24,
            RamUsedGb = 96,
            RamTotalGb = 121,
            FanRpm = busy ? 1_240 : 660,
            FanDuty = busy ? 62 : 35,
        };
    }

    /// <summary>
    /// Fleet hub (#33 phase 3): watches every agent listed in hub.json and serves the Servers
    /// overview. Touches no hardware, so it can run anywhere on the tailnet (normally its own
    /// container beside an agent, fronted by tailscale serve).
    /// </summary>
    private static int RunHub(List<string> args)
    {
        var configPath = GetOption(args, "--config") ?? "/config/hub.json";
        var portText = GetOption(args, "--port") ?? "27080";
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            Console.WriteLine("hub: --port must be a number in 1-65535");
            return 1;
        }

        var binds = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--bind")
            {
                binds.Add(args[i + 1]);
            }
        }

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"hub: {configPath} not found. Example:\n"
                + """{ "Agents": [ { "Name": "Gaming PC", "Url": "http://100.x.y.z:27004", "TokenFile": "/tokens/gaming-pc" } ] }""");
            return 1;
        }

        var log = (Action<string>)(msg => Log.Information("{msg}", msg));
        // fleet settings (phase 4) live beside hub.json unless --state says otherwise; audit log next to it
        var statePath = GetOption(args, "--state")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "fleet-state.json");
        var config = HubConfig.Load(configPath);
        var store = new FleetStore(statePath);
        using var hub = new FleetHub(config, log, store: store);

        // failover group (#68): with peers, this hub only pushes/adopts/powers while it's the active one
        var peerToken = config.PeerTokenFile is { } tokenFile && File.Exists(tokenFile) ? File.ReadAllText(tokenFile).Trim() : null;
        if (config.Peers.Count > 0 && (config.Self is null || peerToken is null))
        {
            Console.WriteLine("hub: Peers needs Self and a readable PeerTokenFile");
            return 1;
        }

        using var failover = new HubFailover(config.Self ?? Environment.MachineName, config.Priority, config.Peers, store, peerToken, log);
        hub.IsActive = () => failover.IsActive;
        failover.RoleChanged += role =>
        {
            if (role == HubRole.Active)
            {
                hub.ForgetPushes(); // re-push the fleet settings: the previous hub's pushes aren't ours to trust
                hub.Nudge();
            }
        };

        // automations (#83): bearer tokens that may change settings/power, audited under their own names
        var automations = HubAutomations.Load(config.Automations, log);
        if (automations.Count > 0)
        {
            log($"hub: automation tokens accepted for {string.Join(", ", automations.Names)}");
        }

        using var web = new HubWebServer(hub, port, log, binds, failover, config.PeerPort, automations);
        SignalWatcher.Install();
        hub.Start();
        failover.Start();
        web.Start();
        while (!SignalWatcher.StopRequested)
        {
            Thread.Sleep(500);
        }

        Log.Information("hub: stopping");
        return 0;
    }

    private static int RunWeb(List<string> args, bool trace)
    {
        var configPath = GetOption(args, "--config") ?? HeadlessConfig.DefaultPath;
        var portText = GetOption(args, "--port") ?? "27004";
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            Console.WriteLine("web: --port must be a number in 1-65535");
            return 1;
        }

        // --bind restricts the dashboard to given addresses (repeatable; e.g. a tailnet IP + 127.0.0.1)
        var binds = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--bind")
            {
                binds.Add(args[i + 1]);
            }
        }

        // --config, --port and --bind are the only flags
        var leftover = args.Where(a => a != "--config" && a != configPath
                                        && a != "--port" && a != portText
                                        && a != "--bind" && !binds.Contains(a)
                                        && !string.IsNullOrWhiteSpace(a)).ToList();
        if (leftover.Count > 0)
        {
            Console.WriteLine("web: unexpected arguments: " + string.Join(' ', leftover));
            return 1;
        }

        var log = (Action<string>)(msg => Log.Information("{msg}", msg));
        using var loop = StartLoop(configPath, trace, log);
        using var web = new HeadlessWebServer(loop, port, log, configPath, binds);

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
