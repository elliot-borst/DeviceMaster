using System.Text.Json;

namespace DeviceMaster.Control;

/// <summary>The parts of a machine's settings the fleet can take over, one at a time (#33 phase 4).</summary>
public enum FleetSection
{
    /// <summary>Effect, colour, brightness, speed, lights out, blackout.</summary>
    Lighting,

    /// <summary>Pump/fan LCD and Turzx modes, metrics and brightness.</summary>
    Screens,

    /// <summary>Fan mode, duty, curve source and curve; pump duty.</summary>
    Cooling,
}

/// <summary>
/// What the fleet wants (#33 phase 4): one profile for every machine, the sections the fleet
/// manages, and per-machine exceptions. A section that isn't managed is left to each machine.
/// Persisted by the hub as JSON; every change bumps <see cref="Version"/>.
/// </summary>
public sealed record FleetState
{
    public FleetProfile Profile { get; init; } = new();
    public IReadOnlyList<FleetSection> Managed { get; init; } = [];
    public IReadOnlyDictionary<string, FleetOverride> Overrides { get; init; } = new Dictionary<string, FleetOverride>();
    public long Version { get; init; }

    /// <summary>
    /// The profile a machine should run: its own current profile with every managed section taken
    /// from the fleet (with that machine's overrides laid on top). Unmanaged sections stay as they are.
    /// </summary>
    public FleetProfile DesiredFor(string machine, FleetProfile current)
    {
        var fleet = Profile.With(Overrides.GetValueOrDefault(machine));
        var desired = current;
        foreach (var section in Managed)
        {
            desired = Take(section, desired, fleet);
        }

        return desired;
    }

    /// <summary>A fingerprint of just the managed sections: equal keys mean nothing to push.</summary>
    public string ManagedKey(FleetProfile profile) =>
        AgentApi.Revision(Managed.Distinct().OrderBy(s => s).Aggregate(new FleetProfile(), (p, s) => Take(s, p, profile)))
        + ":" + string.Join(",", Managed.Distinct().OrderBy(s => s));

    /// <summary><paramref name="target"/> with one section copied from <paramref name="source"/>.</summary>
    public static FleetProfile Take(FleetSection section, FleetProfile target, FleetProfile source) => section switch
    {
        FleetSection.Lighting => target with
        {
            RgbEnabled = source.RgbEnabled,
            RgbOff = source.RgbOff,
            RgbR = source.RgbR,
            RgbG = source.RgbG,
            RgbB = source.RgbB,
            RgbBrightness = source.RgbBrightness,
            RgbEffect = source.RgbEffect,
            RgbSpeed = source.RgbSpeed,
            Blackout = source.Blackout,
        },
        FleetSection.Screens => target with
        {
            LcdScreens = source.LcdScreens,
            LcdBrightness = source.LcdBrightness,
            PumpScreenMetric = source.PumpScreenMetric,
            FanScreenMetric = source.FanScreenMetric,
            TurzxScreen = source.TurzxScreen,
            TurzxMetric = source.TurzxMetric,
            TurzxBrightness = source.TurzxBrightness,
        },
        FleetSection.Cooling => target with
        {
            Mode = source.Mode,
            ManualDutyPercent = source.ManualDutyPercent,
            PumpDutyPercent = source.PumpDutyPercent,
            Source = source.Source,
            CurvePoints = source.CurvePoints,
        },
        _ => target,
    };
}

/// <summary>Loads and saves the hub's <see cref="FleetState"/>, and appends every change to an audit log.</summary>
public sealed class FleetStore
{
    private readonly string _path;
    private readonly string _auditPath;
    private readonly object _gate = new();
    private FleetState _state;

    public FleetStore(string path)
    {
        _path = path;
        _auditPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "fleet-audit.log");
        _state = File.Exists(path)
            ? JsonSerializer.Deserialize<FleetState>(File.ReadAllText(path), AgentApi.Json) ?? new FleetState()
            : new FleetState();
    }

    public FleetState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Applies a change, saves atomically, and records who did what. Returns the new state.</summary>
    public FleetState Update(Func<FleetState, FleetState> change, string who, string what)
    {
        lock (_gate)
        {
            var next = change(_state) with { Version = _state.Version + 1 };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(next, AgentApi.Json));
            File.Move(tmp, _path, overwrite: true);
            File.AppendAllText(_auditPath, JsonSerializer.Serialize(new { at = DateTime.UtcNow, who, what, version = next.Version }) + "\n");
            _state = next;
            return next;
        }
    }

    /// <summary>The most recent audit entries, newest first.</summary>
    public IReadOnlyList<string> RecentAudit(int count)
    {
        lock (_gate)
        {
            return File.Exists(_auditPath) ? File.ReadLines(_auditPath).TakeLast(count).Reverse().ToList() : [];
        }
    }
}
