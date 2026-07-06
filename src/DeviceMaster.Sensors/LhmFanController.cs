using System.Security.Principal;
using DeviceMaster.Core.Safety;
using LibreHardwareMonitor.Hardware;

namespace DeviceMaster.Sensors;

/// <summary>One writable fan control (motherboard SuperIO header or GPU cooler) with its paired tach.</summary>
public sealed record HeaderFan(string Id, string Family, string Name, int? Rpm, float? CurrentPercent);

/// <summary>
/// Fan-duty control for motherboard SuperIO headers and GPU coolers via LibreHardwareMonitor
/// Control sensors. Requires administrator rights (LHM's kernel driver).
/// Safety: SuperIO has no hardware fallback — a crash leaves the last written duty in place —
/// so writes are floored at <see cref="SafetyLimits.HeaderMinimumDutyPercent"/> and Dispose
/// restores BIOS/driver-automatic control for every control we ever touched.
/// The water-loop-critical devices (pump, radiator fans) are NOT driven through this class;
/// they live on the Corsair/Lian Li paths which fail safe by design.
/// </summary>
public sealed class LhmFanController : IDisposable
{
    private readonly Computer _computer;
    private readonly object _gate = new();
    private readonly Dictionary<string, ISensor> _controls = new();
    private readonly HashSet<string> _touched = [];

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public LhmFanController()
    {
        if (!IsElevated)
        {
            throw new InvalidOperationException(
                "Motherboard/GPU fan control needs administrator rights (SuperIO access via the LHM kernel driver).");
        }

        _computer = new Computer
        {
            IsMotherboardEnabled = true,
            IsGpuEnabled = true,
        };
        _computer.Open();
    }

    /// <summary>The motherboard name as LHM reports it (diagnostics/inventory).</summary>
    public string? MotherboardName =>
        _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard)?.Name;

    /// <summary>
    /// The discrete GPU's name (diagnostics/inventory) — the GPU with fan controls wins,
    /// so an iGPU (e.g. Ryzen's Radeon Graphics) never shadows the real card.
    /// </summary>
    public string? GpuName =>
        (_computer.Hardware.FirstOrDefault(h => IsGpu(h) && h.Sensors.Any(s => s.SensorType == SensorType.Control))
            ?? _computer.Hardware.FirstOrDefault(IsGpu))?.Name;

    /// <summary>
    /// Re-scans all Control sensors and their sibling tachometers. Controls pair with fans by
    /// the trailing index of their LHM identifier (…/control/0 ↔ …/fan/0).
    /// </summary>
    public IReadOnlyList<HeaderFan> Enumerate()
    {
        lock (_gate)
        {
            foreach (var hardware in _computer.Hardware)
            {
                Refresh(hardware);
            }

            _controls.Clear();
            var fans = new List<HeaderFan>();
            foreach (var hardware in _computer.Hardware)
            {
                CollectControls(hardware, fans);
            }

            return fans;
        }
    }

    /// <summary>Writes one duty to every enumerated control. Floored per safety rules; errors bubble to the caller.</summary>
    public void SetAllDuties(int dutyPercent)
    {
        var duty = SafetyGuard.ClampHeaderDuty(dutyPercent);
        lock (_gate)
        {
            foreach (var (id, control) in _controls)
            {
                control.Control.SetSoftware(duty);
                _touched.Add(id);
            }
        }
    }

    /// <summary>Writes one control by id (CLI verification path).</summary>
    public void SetDuty(string id, int dutyPercent)
    {
        var duty = SafetyGuard.ClampHeaderDuty(dutyPercent);
        lock (_gate)
        {
            if (!_controls.TryGetValue(id, out var control))
            {
                throw new KeyNotFoundException($"No fan control '{id}' — run Enumerate first / check the id.");
            }

            control.Control.SetSoftware(duty);
            _touched.Add(id);
        }
    }

    /// <summary>Returns every control we ever wrote to BIOS/driver-automatic behaviour.</summary>
    public void RestoreAll()
    {
        lock (_gate)
        {
            foreach (var id in _touched)
            {
                if (_controls.TryGetValue(id, out var control))
                {
                    try
                    {
                        control.Control.SetDefault();
                    }
                    catch
                    {
                        // keep restoring the rest — a single failed header must not stop the sweep
                    }
                }
            }

            _touched.Clear();
        }
    }

    public void Dispose()
    {
        try
        {
            // one more enumeration if a rescan cleared the map, so restore can find them
            if (_touched.Count > 0 && _controls.Count == 0)
            {
                Enumerate();
            }

            RestoreAll();
        }
        catch
        {
            // best effort — BIOS control returns on reboot regardless
        }

        _computer.Close();
    }

    private static bool IsGpu(IHardware h) =>
        h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static void Refresh(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
        {
            Refresh(sub);
        }
    }

    private void CollectControls(IHardware hardware, List<HeaderFan> fans, IHardware? root = null)
    {
        root ??= hardware;
        var family = IsGpu(root) ? "GPU" : root.HardwareType == HardwareType.Motherboard ? "Motherboard" : null;
        if (family is not null)
        {
            var tachs = hardware.Sensors
                .Where(s => s.SensorType == SensorType.Fan)
                .ToDictionary(s => TrailingIndex(s.Identifier.ToString()), s => s);

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Control || sensor.Control is null)
                {
                    continue;
                }

                var id = sensor.Identifier.ToString();
                _controls[id] = sensor;
                tachs.TryGetValue(TrailingIndex(id), out var tach);
                fans.Add(new HeaderFan(
                    id,
                    family,
                    family == "GPU" ? $"{root.Name} · {sensor.Name}" : sensor.Name,
                    tach?.Value is { } rpm && !float.IsNaN(rpm) ? (int)rpm : null,
                    sensor.Value is { } v && !float.IsNaN(v) ? v : null));
            }
        }

        foreach (var sub in hardware.SubHardware)
        {
            CollectControls(sub, fans, root);
        }
    }

    private static string TrailingIndex(string identifier) =>
        identifier[(identifier.LastIndexOf('/') + 1)..];
}
