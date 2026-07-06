using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.CorsairLink.Protocol;
using Serilog;

namespace DeviceMaster.App.Commands;

internal static class LinkCommands
{
    public static int Run(string[] args)
    {
        var subcommand = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        return subcommand switch
        {
            "status" => Status(),
            "set" => Set(
                GetString(args, "--hub"),
                GetInt(args, "--channel"),
                GetInt(args, "--duty"),
                GetInt(args, "--hold") ?? 10),
            "leds" => Leds(),
            "ledtest" => LedTest(GetInt(args, "--count"), GetInt(args, "--hold") ?? 60),
            "ledreg" => LedRegistry(
                GetString(args, "--channels"),
                GetString(args, "--hub"),
                GetInt(args, "--hold") ?? 60),
            _ => Usage(),
        };
    }

    /// <summary>
    /// Diagnostic: dump the hub's own per-channel LED data (endpoints 0x1E/0x1D via the color
    /// handle) next to our catalog LED counts — ground truth for the color-buffer layout.
    /// </summary>
    private static int Leds()
    {
        foreach (var device in LinkHub.FindHubDevices())
        {
            LinkHub hub;
            try
            {
                hub = LinkHub.Open(device, trace: m => Log.Debug("{Trace}", m));
            }
            catch (Exception ex)
            {
                Log.Error("Skipping one hub: {Reason}", ex.Message);
                continue;
            }

            using var _ = hub;
            var restore = hub.EnumerateChannels(allowEnterSoftwareMode: true);
            Log.Information("Hub {Serial} (fw {Fw}):", hub.SerialNumber[..8], hub.FirmwareVersion);
            foreach (var channel in hub.Channels)
            {
                Console.WriteLine($"  ch{channel.Channel,2}  {channel.Name,-28} catalog LEDs = {channel.Info?.LedCount ?? 0}");
            }

            var (codes, leds) = hub.ReadLedDeviceInfo();
            Console.WriteLine($"  0x1E (command codes) raw: {Convert.ToHexString(codes.AsSpan(0, 48))}");
            Console.WriteLine($"  0x1D (led counts)   raw: {Convert.ToHexString(leds.AsSpan(0, 48))}");

            if (restore)
            {
                hub.EnterHardwareMode();
            }
        }

        return 0;
    }

    /// <summary>
    /// Diagnostic: paints every channel a distinct color (optionally overriding LEDs/device
    /// with --count N) so an observer can report which fan shows which color — reveals the
    /// real per-fan LED count and the stream order. Also dumps the raw speeds packet.
    /// </summary>
    private static int LedTest(int? ledsPerDevice, int holdSeconds)
    {
        // software-mode lighting only lives while the session is open — closing restores
        // hardware mode (safety), which wipes the colors. So paint, then HOLD.
        var hubs = new List<LinkHub>();
        try
        {
            PaintLedTest(ledsPerDevice, hubs);
            Log.Information("Holding for {Seconds}s — note which fan shows which color (Ctrl+C ends early; hardware mode is restored on exit)…", holdSeconds);
            for (var i = holdSeconds; i > 0; i -= 5)
            {
                Console.WriteLine($"  …{i}s");
                Thread.Sleep(Math.Min(5, i) * 1000);
            }
        }
        finally
        {
            foreach (var hub in hubs)
            {
                hub.Dispose(); // restores hardware mode
            }
        }

        return 0;
    }

    private static void PaintLedTest(int? ledsPerDevice, List<LinkHub> hubs)
    {
        // fixed palette in channel order: easy to name by eye
        var palette = new (string Name, byte R, byte G, byte B)[]
        {
            ("RED", 0xFF, 0x00, 0x00), ("GREEN", 0x00, 0xFF, 0x00), ("BLUE", 0x00, 0x00, 0xFF),
            ("WHITE", 0xFF, 0xFF, 0xFF), ("YELLOW", 0xFF, 0xFF, 0x00), ("MAGENTA", 0xFF, 0x00, 0xFF),
            ("CYAN", 0x00, 0xFF, 0xFF), ("ORANGE", 0xFF, 0x60, 0x00),
        };

        foreach (var device in LinkHub.FindHubDevices())
        {
            LinkHub hub;
            try
            {
                hub = LinkHub.Open(device, trace: m => Log.Debug("{Trace}", m));
            }
            catch (Exception ex)
            {
                Log.Error("Skipping one hub: {Reason}", ex.Message);
                continue;
            }

            hubs.Add(hub);
            hub.EnumerateChannels(allowEnterSoftwareMode: true);
            if (hub.InSoftwareMode && !hub.HasUnknownChannels)
            {
                hub.WriteSafeDefaults(); // software mode holds duties — pin known-safe ones for the observation window
            }

            hub.SyncLedRegistry(m => Log.Information("{Message}", m));

            var speedsRaw = hub.ReadSpeedsRaw();
            Log.Information("Hub {Serial} speeds raw [{Count} sensors]: {Hex}",
                hub.SerialNumber[..8], speedsRaw[6], Convert.ToHexString(speedsRaw.AsSpan(0, 60)));

            var ledChannels = hub.Channels.Where(c => c.Info is { LedCount: > 0 }).OrderBy(c => c.Channel).ToList();
            var colors = new Dictionary<int, (byte, byte, byte)>();
            for (var i = 0; i < ledChannels.Count; i++)
            {
                var (name, r, g, b) = palette[i % palette.Length];
                colors[ledChannels[i].Channel] = (r, g, b);
                Console.WriteLine($"  ch{ledChannels[i].Channel,2} ({ledChannels[i].Name}) -> {name}");
            }

            var perDevice = ledsPerDevice.HasValue ? $"{ledsPerDevice} (override)" : "catalog";
            Log.Information("Painting per-channel colors ({LedsPerDevice} LEDs/device)…", perDevice);
            hub.ApplyPerChannelColors(colors, ledsPerDevice);
        }
    }

    /// <summary>
    /// Registry-variant experiment: writes an LED registry containing ONLY the given channels
    /// (e.g. the pre-sync registry iCUE left behind), pulses LED power, then paints the
    /// selected channels distinct colors and holds — the observer reports what actually lights.
    /// Purpose: falsify/confirm that registering link-degraded fans breaks LED delivery to
    /// healthy fans on the same port. Non-destructive: the app re-syncs the registry to the
    /// full chain on its next start. Run with the DeviceMaster app EXITED (tray → Exit).
    /// </summary>
    private static int LedRegistry(string? channelsArg, string? hubFilter, int holdSeconds)
    {
        var hubs = new List<LinkHub>();
        try
        {
            foreach (var device in LinkHub.FindHubDevices())
            {
                if (hubFilter is not null)
                {
                    try
                    {
                        if (!device.GetSerialNumber().StartsWith(hubFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                LinkHub hub;
                try
                {
                    hub = LinkHub.Open(device, trace: m => Log.Debug("{Trace}", m));
                }
                catch (Exception ex)
                {
                    Log.Error("Skipping one hub: {Reason}", ex.Message);
                    continue;
                }

                hubs.Add(hub);
                hub.EnumerateChannels(allowEnterSoftwareMode: true);
                if (hub.InSoftwareMode && !hub.HasUnknownChannels)
                {
                    hub.WriteSafeDefaults();
                }

                var current = hub.ReadLedRegistry();
                Log.Information("Hub {Serial} registry now: [{Registry}]", hub.SerialNumber[..8],
                    string.Join(", ", current.OrderBy(kv => kv.Key).Select(kv => $"ch{kv.Key}=0x{kv.Value:X2}")));

                if (channelsArg is null)
                {
                    continue; // read-only mode
                }

                var wanted = channelsArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse).ToHashSet();
                var entries = new Dictionary<int, byte>();
                foreach (var channel in hub.Channels.Where(c => wanted.Contains(c.Channel)))
                {
                    // command code: catalog first, then whatever the hub already uses — never invented
                    var code = channel.Info?.LedCommandCode ?? 0;
                    if (code == 0 && !current.TryGetValue(channel.Channel, out code))
                    {
                        Log.Warning("ch{Channel}: no known LED command code — skipped", channel.Channel);
                        continue;
                    }

                    entries[channel.Channel] = code;
                }

                if (entries.Count == 0)
                {
                    Log.Warning("Hub {Serial}: none of the requested channels exist on this chain — registry untouched",
                        hub.SerialNumber[..8]);
                    continue;
                }

                Log.Information("Hub {Serial}: writing registry variant [{Variant}] + LED power pulse",
                    hub.SerialNumber[..8],
                    string.Join(", ", entries.OrderBy(kv => kv.Key).Select(kv => $"ch{kv.Key}=0x{kv.Value:X2}")));
                hub.WriteLedRegistry(entries);
                Thread.Sleep(500); // let the chain re-register before streaming colors

                var palette = new (string Name, byte R, byte G, byte B)[]
                {
                    ("RED", 0xFF, 0x00, 0x00), ("GREEN", 0x00, 0xFF, 0x00), ("BLUE", 0x00, 0x00, 0xFF),
                    ("WHITE", 0xFF, 0xFF, 0xFF), ("YELLOW", 0xFF, 0xFF, 0x00), ("MAGENTA", 0xFF, 0x00, 0xFF),
                };
                var colors = new Dictionary<int, (byte, byte, byte)>();
                var i = 0;
                foreach (var channel in entries.Keys.OrderBy(c => c))
                {
                    var (name, r, g, b) = palette[i++ % palette.Length];
                    colors[channel] = (r, g, b);
                    Console.WriteLine($"  ch{channel,2} -> {name}   (unlisted channels -> dark)");
                }

                hub.ApplyPerChannelColors(colors);
            }

            if (channelsArg is not null && hubs.Count > 0)
            {
                Log.Information("Holding {Seconds}s — note exactly which fans light and in which color. "
                    + "The DeviceMaster app restores the full registry on its next start.", holdSeconds);
                for (var t = holdSeconds; t > 0; t -= 5)
                {
                    Console.WriteLine($"  …{t}s");
                    Thread.Sleep(Math.Min(5, t) * 1000);
                }
            }
        }
        finally
        {
            foreach (var hub in hubs)
            {
                hub.Dispose(); // restores hardware mode
            }
        }

        return 0;
    }

    private static int Status()
    {
        var devices = LinkHub.FindHubDevices();
        if (devices.Count == 0)
        {
            Log.Error("No iCUE LINK System Hub command interfaces found");
            return 1;
        }

        foreach (var device in devices)
        {
            LinkHub hub;
            try
            {
                hub = LinkHub.Open(device, trace: m => Log.Debug("{Trace}", m));
            }
            catch (Exception ex)
            {
                Log.Error("Skipping one hub: {Reason}", ex.Message);
                continue;
            }

            using var _ = hub;
            Log.Information("Hub {Serial}: '{Product}', firmware {Firmware}",
                hub.SerialNumber, hub.ProductName, hub.FirmwareVersion);

            var enteredSoftwareMode = hub.EnumerateChannels(allowEnterSoftwareMode: true);
            if (enteredSoftwareMode)
            {
                Log.Information("Hub rejected reads in hardware mode; temporarily in software mode (restored on exit)");
                if (!hub.HasUnknownChannels)
                {
                    var defaults = hub.WriteSafeDefaults();
                    Log.Information("Wrote safe defaults: {Duties}",
                        string.Join(", ", defaults.Select(d => $"ch{d.Key}={d.Value}%")));
                }
            }

            if (hub.HasUnknownChannels)
            {
                Log.Warning("Hub {Serial} carries unrecognized chain devices — this hub stays read-only until "
                    + "they are added to LinkDeviceCatalog", hub.SerialNumber);
            }

            var speeds = ReadWithRetry(hub, static h => h.ReadSpeeds(), ref enteredSoftwareMode)
                .ToDictionary(s => s.Channel);
            var temperatures = ReadWithRetry(hub, static h => h.ReadTemperatures(), ref enteredSoftwareMode)
                .ToDictionary(t => t.Channel);

            Console.WriteLine();
            Console.WriteLine($"  Link chain on hub {Shorten(hub.SerialNumber)} (fw {hub.FirmwareVersion}):");
            foreach (var channel in hub.Channels)
            {
                var rpm = speeds.TryGetValue(channel.Channel, out var s) && s.Rpm is { } r ? $"{r,5} RPM" : "    -    ";
                var temp = temperatures.TryGetValue(channel.Channel, out var t) && t.TemperatureCelsius is { } tc
                    ? $"{tc,5:F1} °C"
                    : "";
                var tag = channel.IsPump ? " [PUMP]" : channel.IsKnown ? "" : " [UNKNOWN]";
                Console.WriteLine($"    ch{channel.Channel,2}  {channel.Name,-30}{tag,-10} {rpm}  {temp,-9} id={channel.Id}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static int Set(string? hubFilter, int? channel, int? duty, int holdSeconds)
    {
        if (duty is null)
        {
            Log.Error("--duty <0-100> is required");
            return 1;
        }

        var devices = LinkHub.FindHubDevices();
        var selected = devices.Where(d =>
        {
            if (hubFilter is null) return true;
            try { return d.GetSerialNumber().StartsWith(hubFilter, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }).ToList();

        if (selected.Count == 0)
        {
            Log.Error("No hub matches {Filter} (found {Count} hub(s) total)", hubFilter ?? "(any)", devices.Count);
            return 1;
        }

        foreach (var device in selected)
        {
            using var hub = LinkHub.Open(device, trace: m => Log.Debug("{Trace}", m));
            Log.Information("Hub {Serial}: firmware {Firmware}", hub.SerialNumber, hub.FirmwareVersion);

            hub.EnumerateChannels(allowEnterSoftwareMode: true);
            if (!hub.InSoftwareMode)
            {
                hub.EnterSoftwareMode();
            }

            if (hub.HasUnknownChannels)
            {
                Log.Warning("Skipping hub {Serial}: unrecognized chain devices present (no writes allowed). "
                    + "Restoring hardware mode.", hub.SerialNumber);
                continue; // Dispose restores hardware mode
            }

            var requests = new Dictionary<int, int>();
            foreach (var ch in hub.Channels.Where(c => !c.IsPump))
            {
                if (channel is null || ch.Channel == channel)
                {
                    requests[ch.Channel] = duty.Value;
                }
            }

            if (requests.Count == 0)
            {
                Log.Warning("No matching fan channel on hub {Serial} (pumps cannot be targeted in Stage 1)",
                    hub.SerialNumber);
                continue;
            }

            var applied = hub.WriteFixedDuties(requests);
            Log.Information("Applied: {Duties}", string.Join(", ", applied.Select(d => $"ch{d.Key}={d.Value}%")));

            for (var i = 0; i < holdSeconds; i++)
            {
                Thread.Sleep(1000);
                var speeds = hub.ReadSpeeds().Where(s => s.Rpm is not null).ToDictionary(s => s.Channel);
                var line = string.Join("  ", hub.Channels
                    .Where(c => speeds.ContainsKey(c.Channel))
                    .Select(c => $"ch{c.Channel}:{speeds[c.Channel].Rpm,4}rpm"));
                Console.WriteLine($"    t+{i + 1,2}s  {line}");
            }

            Log.Information("Restoring hardware mode on hub {Serial} (hub-managed curves resume)", hub.SerialNumber);
            hub.EnterHardwareMode();
        }

        return 0;
    }

    private static IReadOnlyList<T> ReadWithRetry<T>(LinkHub hub, Func<LinkHub, IReadOnlyList<T>> read,
        ref bool enteredSoftwareMode)
    {
        try
        {
            return read(hub);
        }
        catch (LinkHubException ex) when (ex.IsIncorrectMode)
        {
            hub.EnterSoftwareMode();
            enteredSoftwareMode = true;
            if (!hub.HasUnknownChannels)
            {
                hub.WriteSafeDefaults();
            }

            return read(hub);
        }
    }

    private static string Shorten(string serial) => serial.Length > 8 ? serial[..8] + "…" : serial;

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
        Console.WriteLine("  link status                                    firmware, chain devices, RPMs, temps per hub");
        Console.WriteLine("  link set --duty P [--channel N] [--hub SERIALPREFIX] [--hold S]");
        Console.WriteLine("      set fan duty (pumps always run 100% in Stage 1), watch RPMs for S seconds,");
        Console.WriteLine("      then restore hub-managed hardware mode");
        Console.WriteLine("  link ledreg [--channels 2,3,13,15] [--hub SERIALPREFIX] [--hold S]");
        Console.WriteLine("      show the hub's persisted LED registry; with --channels, write a registry");
        Console.WriteLine("      variant containing ONLY those channels, pulse LED power, and paint them");
        Console.WriteLine("      distinct colors for S seconds (exit the DeviceMaster app first)");
        return 1;
    }
}
