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

    /// <summary>
    /// Sections where a change made on one machine (its Windows app or its own web page) becomes the
    /// fleet's — so a colour or brightness picked anywhere shows everywhere. Cooling is not adopted:
    /// fleet cooling is sanitized and a machine's own fan tweaks stay a visible "differs".
    /// </summary>
    public static readonly IReadOnlyList<FleetSection> Adoptable = [FleetSection.Lighting, FleetSection.Screens];

    /// <summary>A fingerprint of one section of a profile.</summary>
    public static string SectionKey(FleetSection section, FleetProfile profile) =>
        AgentApi.Revision(Take(section, new FleetProfile(), profile));

    /// <summary>
    /// The state after one machine's own change to a managed section is adopted: into that machine's
    /// override when it has its own values for the section, otherwise into the fleet profile.
    /// </summary>
    public FleetState Adopt(string machine, FleetSection section, FleetProfile own)
    {
        var current = Overrides.GetValueOrDefault(machine);
        if (FleetOverrides.Has(current, section))
        {
            var overrides = new Dictionary<string, FleetOverride>(Overrides) { [machine] = FleetOverrides.Set(current!, section, own) };
            return this with { Overrides = overrides };
        }

        return this with { Profile = Take(section, Profile, own) };
    }

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

/// <summary>Per-section edits of a machine's <see cref="FleetOverride"/> (its own settings instead of the fleet's).</summary>
public static class FleetOverrides
{
    /// <summary>The override with one section set to a machine's own values.</summary>
    public static FleetOverride Set(FleetOverride o, FleetSection section, FleetProfile own) => section switch
    {
        FleetSection.Lighting => o with
        {
            RgbEnabled = own.RgbEnabled, RgbOff = own.RgbOff, RgbR = own.RgbR, RgbG = own.RgbG, RgbB = own.RgbB,
            RgbBrightness = own.RgbBrightness, RgbEffect = own.RgbEffect, RgbSpeed = own.RgbSpeed, Blackout = own.Blackout,
        },
        FleetSection.Screens => o with
        {
            LcdScreens = own.LcdScreens, LcdBrightness = own.LcdBrightness, PumpScreenMetric = own.PumpScreenMetric,
            FanScreenMetric = own.FanScreenMetric, TurzxScreen = own.TurzxScreen, TurzxMetric = own.TurzxMetric, TurzxBrightness = own.TurzxBrightness,
        },
        FleetSection.Cooling => o with
        {
            Mode = own.Mode, ManualDutyPercent = own.ManualDutyPercent, PumpDutyPercent = own.PumpDutyPercent,
            Source = own.Source, CurvePoints = own.CurvePoints,
        },
        _ => o,
    };

    /// <summary>The override with one section back to following the fleet.</summary>
    public static FleetOverride Clear(FleetOverride o, FleetSection section) => section switch
    {
        FleetSection.Lighting => o with
        {
            RgbEnabled = null, RgbOff = null, RgbR = null, RgbG = null, RgbB = null,
            RgbBrightness = null, RgbEffect = null, RgbSpeed = null, Blackout = null,
        },
        FleetSection.Screens => o with
        {
            LcdScreens = null, LcdBrightness = null, PumpScreenMetric = null, FanScreenMetric = null,
            TurzxScreen = null, TurzxMetric = null, TurzxBrightness = null,
        },
        FleetSection.Cooling => o with { Mode = null, ManualDutyPercent = null, PumpDutyPercent = null, Source = null, CurvePoints = null },
        _ => o,
    };

    /// <summary>Whether a machine has its own values for a section.</summary>
    public static bool Has(FleetOverride? o, FleetSection section) => o is not null && section switch
    {
        FleetSection.Lighting => o.RgbEnabled is not null || o.RgbOff is not null || o.RgbEffect is not null || o.RgbR is not null || o.Blackout is not null,
        FleetSection.Screens => o.LcdScreens is not null || o.TurzxScreen is not null || o.LcdBrightness is not null || o.TurzxBrightness is not null,
        FleetSection.Cooling => o.Mode is not null || o.Source is not null || o.CurvePoints is not null || o.ManualDutyPercent is not null || o.PumpDutyPercent is not null,
        _ => false,
    };

    /// <summary>
    /// Makes fleet cooling safe for every machine before it is stored: the curve source is always
    /// the hottest of each machine's own CPU and GPU (a GPU- or coolant-only source would put a
    /// machine without that sensor into failsafe), the mode is Curve or Fixed, the curve passes the
    /// shared rules, and the pump never goes below its floor. Null when the curve is unusable.
    /// </summary>
    public static FleetProfile? SanitizeCooling(FleetProfile p)
    {
        if (DeviceMaster.Core.Curves.CurveRules.Normalize(p.CurvePoints) is not { } curve)
        {
            return null;
        }

        return p with
        {
            Source = CurveSource.Hottest,
            Mode = p.Mode == ControlMode.Manual ? ControlMode.Manual : ControlMode.Curve,
            CurvePoints = curve,
            ManualDutyPercent = Math.Clamp(p.ManualDutyPercent, 0, 100),
            PumpDutyPercent = Math.Clamp(p.PumpDutyPercent, DeviceMaster.Core.Safety.SafetyLimits.PumpMinimumDutyPercent, 100),
        };
    }
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

    /// <summary>Records a change made directly on one server (the full per-server page) without touching the fleet state.</summary>
    public void Audit(string who, string what)
    {
        lock (_gate)
        {
            File.AppendAllText(_auditPath, JsonSerializer.Serialize(new { at = DateTime.UtcNow, who, what, version = _state.Version }) + "\n");
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
