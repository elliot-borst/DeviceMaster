using System.Security.Principal;
using DeviceMaster.App.Commands;
using DeviceMaster.Core.Conflicts;
using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;
using DeviceMaster.Core.Sensors;
using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.LianLi;
using DeviceMaster.Devices.Turzx;
using DeviceMaster.Sensors;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/devicemaster-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    var command = args.Length > 0 ? args[0].ToLowerInvariant() : "discover";

    // The desktop app owns the devices while it runs — a second controller would fight it.
    if (command is "link" or "slv3" or "control" or "headers"
        && System.Diagnostics.Process.GetProcessesByName("DeviceMaster").Length > 0
        && !args.Contains("--force", StringComparer.OrdinalIgnoreCase))
    {
        Log.Error("The DeviceMaster app is running (check the system tray) and owns the devices. "
            + "Exit it first, or pass --force if you know what you are doing.");
        return 1;
    }

    return command switch
    {
        "discover" => RunDiscover(),
        "monitor" => RunMonitor(GetOption(args, "--seconds", 2), GetOption(args, "--count", 10)),
        "link" => LinkCommands.Run(args),
        "slv3" => Slv3Commands.Run(args),
        "control" => ControlCommands.Run(args),
        "headers" => HeaderCommands.Run(args),
        "turzx" => TurzxCommands.Run(args),
        "aura" => AuraCommands.Run(args),
        "ramrgb" => RgbChipCommands.RunRam(args),
        "gpurgb" => RgbChipCommands.RunGpu(args),
        _ => PrintUsage(),
    };
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled error");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

static int RunDiscover()
{
    Log.Information("DeviceMaster Stage 0 — device discovery (read-only, no device I/O)");

    var conflicts = ConflictingSoftwareChecker.FindConflicts();
    foreach (var conflict in conflicts)
    {
        Log.Warning("Vendor software is running: {Kind} {Name} ({Detail}) — it may hold or fight over our devices",
            conflict.Kind, conflict.Name, conflict.Detail);
    }

    if (conflicts.Count == 0)
        Log.Information("No conflicting vendor software detected");

    var scan = DeviceScanner.ScanAll();

    Console.WriteLine();
    Console.WriteLine("=== Target hardware (positively identified; writes permitted once protocols land) ===");
    var targetGroups = scan.UsbTree
        .Where(n => n.UsbId is { } id && KnownDeviceRegistry.Identify(id) is { SupportPlanned: true })
        .GroupBy(n => n.UsbId!.Value)
        .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal);
    foreach (var group in targetGroups)
    {
        var known = KnownDeviceRegistry.Identify(group.Key)!;
        var physical = group.Where(n => n.IsPhysicalDevice).ToList();
        Console.WriteLine($"  [{group.Key}] {known.Name} — {physical.Count} unit(s)");
        foreach (var node in physical)
            Console.WriteLine($"      serial={SerialFromInstanceId(node.PnpInstanceId),-34} driver={node.DriverService ?? "?"}");
    }

    Console.WriteLine();
    Console.WriteLine("=== HID interfaces on target hardware ===");
    foreach (var hid in scan.HidDevices.Where(h => h.Identification is { SupportPlanned: true }))
    {
        Console.WriteLine($"  [{hid.UsbId}] {hid.Name}  in={hid.MaxInputReportLength} out={hid.MaxOutputReportLength} feature={hid.MaxFeatureReportLength}");
        Console.WriteLine($"      path={hid.Path}");
    }

    Console.WriteLine();
    Console.WriteLine("=== Serial ports ===");
    foreach (var port in scan.SerialPorts)
    {
        var tag = port.Identification is { } known ? $"  <-- {known.Name}" : string.Empty;
        Console.WriteLine($"  {port.ComPort}: {port.Name}  usb={port.UsbId?.ToString() ?? "n/a"} serial={port.SerialHint ?? "?"}{tag}");
    }

    Console.WriteLine();
    Console.WriteLine("=== Other USB devices (ignored — never written to) ===");
    var otherGroups = scan.UsbTree
        .Where(n => n.IsPhysicalDevice)
        .Where(n => n.UsbId is null || KnownDeviceRegistry.Identify(n.UsbId.Value) is not { SupportPlanned: true })
        .GroupBy(n => n.UsbId?.ToString() ?? "????:????")
        .OrderBy(g => g.Key, StringComparer.Ordinal);
    foreach (var group in otherGroups)
        Console.WriteLine($"  [{group.Key}] x{group.Count(),-2} {group.First().Name}");

    Console.WriteLine();
    Console.WriteLine("=== Stage 1 targets ===");
    var hubs = CorsairLinkFamily.FindHubs(scan.HidDevices).ToList();
    var lcds = CorsairLinkFamily.FindLcdModules(scan.HidDevices).ToList();
    var fanNodes = LianLiFamily.FindSlv3FanNodes(scan.UsbTree).ToList();
    var slv3Controllers = LianLiFamily.FindSlv3Controllers(scan.UsbTree).ToList();
    var screens = TurzxFamily.FindScreens(scan.SerialPorts).ToList();
    Console.WriteLine($"  Corsair Link hub HID interfaces : {hubs.Count}");
    Console.WriteLine($"  Corsair LCD modules             : {lcds.Count}");
    Console.WriteLine($"  Lian Li SL V3 controllers (TX/RX): {slv3Controllers.Count}");
    Console.WriteLine($"  Lian Li SL V3 fan nodes         : {fanNodes.Count}");
    Console.WriteLine($"  Turzx screens                   : {screens.Count} ({string.Join(", ", screens.Select(s => s.ComPort))})");

    Log.Information("Discovery complete: {UsbNodes} USB nodes, {Hid} HID interfaces, {Serial} serial ports",
        scan.UsbTree.Count, scan.HidDevices.Count, scan.SerialPorts.Count);
    return 0;
}

static int RunMonitor(int seconds, int count)
{
    if (!IsElevated())
        Log.Warning("Not running elevated — CPU temperatures will likely be missing (LibreHardwareMonitor needs admin for its kernel driver)");

    Log.Information("Opening LibreHardwareMonitor (can take a few seconds)...");
    using var source = new LhmSensorSource();

    for (var i = 0; i < count; i++)
    {
        var interesting = source.Read()
            .Where(r => r.Kind is SensorKind.Temperature or SensorKind.FanRpm or SensorKind.PumpRpm or SensorKind.FlowRate)
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"--- sample {i + 1}/{count} at {DateTime.Now:HH:mm:ss} ({interesting.Count} readings) ---");
        foreach (var reading in interesting)
            Console.WriteLine($"  {reading.Kind,-12} {reading.Name,-55} {reading.Value,8:F1} {reading.Unit}");

        if (i < count - 1)
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }

    return 0;
}

static int PrintUsage()
{
    Console.WriteLine("DeviceMaster Stage 0");
    Console.WriteLine("usage:");
    Console.WriteLine("  DeviceMaster.App discover                      list USB/HID/serial devices and vendor-software conflicts");
    Console.WriteLine("  DeviceMaster.App monitor [--seconds N] [--count N]   poll LibreHardwareMonitor sensors");
    Console.WriteLine("  DeviceMaster.App link status                         iCUE LINK hubs: chain devices, RPMs, temps");
    Console.WriteLine("  DeviceMaster.App link set --duty P [--channel N] [--hub SERIAL] [--hold S]");
    Console.WriteLine("  DeviceMaster.App slv3 status                         Lian Li SL V3 wireless fans: RPMs, PWM");
    Console.WriteLine("  DeviceMaster.App slv3 set --duty P [--hold S]");
    return 1;
}

static int GetOption(string[] args, string name, int fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[i + 1], out var value) && value > 0)
        {
            return value;
        }
    }

    return fallback;
}

static string SerialFromInstanceId(string instanceId) =>
    instanceId.Split('\\').LastOrDefault() ?? "?";

static bool IsElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}
