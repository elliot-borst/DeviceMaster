using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceMaster.Control;

/// <summary>One machine the hub watches: its agent API address and where its token lives.</summary>
public sealed record HubAgent
{
    /// <summary>Display name ("Gaming PC"); the agent's own machine name is shown alongside.</summary>
    public string Name { get; init; } = "";

    /// <summary>Base address of the agent, e.g. <c>http://100.64.1.2:27004</c>.</summary>
    public string Url { get; init; } = "";

    /// <summary>File holding that agent's token (mounted read-only into the hub). Never the token itself.</summary>
    public string TokenFile { get; init; } = "";
}

/// <summary>The hub's configuration file (#33 phase 3).</summary>
public sealed record HubConfig
{
    public List<HubAgent> Agents { get; init; } = [];

    /// <summary>How often each agent is asked for its status.</summary>
    public int PollMs { get; init; } = 2_000;

    public static HubConfig Load(string path) =>
        JsonSerializer.Deserialize<HubConfig>(File.ReadAllText(path), AgentApi.Json) ?? new HubConfig();
}

/// <summary>What the hub last knew about one machine.</summary>
public sealed record MachineSnapshot
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Online { get; init; }
    public DateTime? LastSeenUtc { get; init; }
    public string? Error { get; init; }
    public ControlStatus? Status { get; init; }

    /// <summary>The fleet-shared settings the machine runs now (phase 4).</summary>
    public FleetProfile? Profile { get; init; }

    /// <summary>Whether the fleet-managed sections match what the fleet wants (null = nothing managed / unknown).</summary>
    public bool? InSync { get; init; }

    /// <summary>Why the last settings push failed, if it did.</summary>
    public string? PushError { get; init; }

    /// <summary>Phone- and sidebar-sized summary of a machine.</summary>
    public MachineSummary Summarize(DateTime nowUtc) => new()
    {
        Name = Name,
        Online = Online,
        SecondsSinceSeen = LastSeenUtc is { } seen ? (int)Math.Max(0, (nowUtc - seen).TotalSeconds) : null,
        Error = Error,
        MachineName = Status?.Capabilities.MachineName,
        Platform = Status?.Capabilities.Platform,
        Version = Status?.Version,
        Mode = Status?.Mode.ToString(),
        Source = Status?.SourceName,
        SourceTemperatureC = Status?.SourceTemperatureC,
        CpuTemperatureC = Status?.CpuTemperatureC,
        GpuTemperatureC = Status?.GpuTemperatureC,
        CoolantTemperatureC = Status?.CoolantTemperatureC,
        FanDutyPercent = Status?.TargetDutyPercent,
        PumpDutyPercent = Status?.PumpDutyPercent,
        Failsafe = Status?.FailsafeActive ?? false,
        WarningCount = Status?.Warnings.Count ?? 0,
        FanCount = Status?.Devices.Count(d => !d.IsPump && !d.IsScreen) ?? 0,
        InSync = InSync,
        PushError = PushError,
        Capabilities = Status?.Capabilities,
    };
}

/// <summary>The fleet overview row for one machine (the Servers list).</summary>
public sealed record MachineSummary
{
    public string Name { get; init; } = "";
    public bool Online { get; init; }
    public int? SecondsSinceSeen { get; init; }
    public string? Error { get; init; }
    public string? MachineName { get; init; }
    public string? Platform { get; init; }
    public string? Version { get; init; }
    public string? Mode { get; init; }
    public string? Source { get; init; }
    public double? SourceTemperatureC { get; init; }
    public double? CpuTemperatureC { get; init; }
    public double? GpuTemperatureC { get; init; }
    public double? CoolantTemperatureC { get; init; }
    public int? FanDutyPercent { get; init; }
    public int? PumpDutyPercent { get; init; }
    public bool Failsafe { get; init; }
    public int WarningCount { get; init; }
    public int FanCount { get; init; }
    public bool? InSync { get; init; }
    public string? PushError { get; init; }
    public MachineCapabilities? Capabilities { get; init; }
}


/// <summary>
/// The fleet hub's collector and reconciler (#33 phases 3–4). Asks every agent for its status and
/// its settings on a short cadence and keeps the latest answer per machine; a machine that stops
/// answering is marked offline after <see cref="OfflineAfterMs"/> (its last status is kept).
/// <para>
/// With a <see cref="FleetStore"/>, it also pushes the fleet's managed sections to each machine —
/// only when what the fleet wants for that machine changed since its last successful push (a
/// machine that was offline gets it when it comes back). A change made locally on a machine is
/// not overwritten until the next fleet change; the overview marks that machine "differs".
/// </para>
/// </summary>
public sealed class FleetHub : IDisposable
{
    public const int OfflineAfterMs = 10_000;

    private readonly HubConfig _config;
    private readonly FleetStore? _store;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private readonly Dictionary<string, MachineSnapshot> _machines = [];
    private readonly Dictionary<string, bool> _wasOnline = [];
    private readonly Dictionary<string, string> _pushed = []; // machine -> managed key last pushed successfully
    private Thread? _thread;

    public FleetHub(HubConfig config, Action<string>? log = null, HttpMessageHandler? handler = null, FleetStore? store = null)
    {
        _config = config;
        _store = store;
        _log = log;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(3);
        foreach (var agent in config.Agents)
        {
            _machines[agent.Name] = new MachineSnapshot { Name = agent.Name, Url = agent.Url, Error = "not polled yet" };
        }
    }

    /// <summary>The fleet settings store (null = read-only hub).</summary>
    public FleetStore? Store => _store;

    /// <summary>Every machine, in configuration order.</summary>
    public IReadOnlyList<MachineSnapshot> Machines
    {
        get
        {
            lock (_gate)
            {
                return _config.Agents.Select(a => _machines[a.Name]).ToList();
            }
        }
    }

    public MachineSnapshot? Machine(string name)
    {
        lock (_gate)
        {
            return _machines.GetValueOrDefault(name);
        }
    }

    public bool Knows(string name) => _config.Agents.Any(a => a.Name == name);

    /// <summary>Poll (and push) right away instead of at the next tick — after a settings change.</summary>
    public void Nudge() => _wake.Set();

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "DeviceMaster fleet hub" };
        _thread.Start();
        _log?.Invoke($"fleet hub: watching {_config.Agents.Count} machine(s): {string.Join(", ", _config.Agents.Select(a => a.Name))}"
            + (_store is null ? " (read-only)" : $" (fleet manages: {string.Join(", ", _store.State.Managed)})"));
    }

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            PollOnce().GetAwaiter().GetResult();
            WaitHandle.WaitAny([_cts.Token.WaitHandle, _wake], Math.Max(_config.PollMs, 500));
        }
    }

    /// <summary>One round of status requests (and pushes) to every agent, in parallel (public for tests).</summary>
    public Task PollOnce() => Task.WhenAll(_config.Agents.Select(PollAgent));

    private async Task<T?> Get<T>(HubAgent agent, string token, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, agent.Url.TrimEnd('/') + AgentApi.Prefix + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, _cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"agent answered {(int)response.StatusCode}");
        }

        return await JsonSerializer.DeserializeAsync<T>(await response.Content.ReadAsStreamAsync(_cts.Token), AgentApi.Json, _cts.Token);
    }

    private sealed record ProfileAnswer(string Revision, FleetProfile Profile);

    private async Task PollAgent(HubAgent agent)
    {
        ControlStatus? status = null;
        FleetProfile? profile = null;
        string? error = null;
        string? pushError = null;
        bool? inSync = null;
        try
        {
            var token = File.ReadAllText(agent.TokenFile).Trim();
            status = await Get<ControlStatus>(agent, token, "status");
            profile = (await Get<ProfileAnswer>(agent, token, "profile"))?.Profile;

            if (_store?.State is { Managed.Count: > 0 } state && profile is not null)
            {
                var desired = state.DesiredFor(agent.Name, profile);
                var key = state.ManagedKey(desired);
                bool due;
                lock (_gate)
                {
                    due = _pushed.GetValueOrDefault(agent.Name) != key;
                }

                if (due)
                {
                    try
                    {
                        profile = await Push(agent, token, desired);
                        lock (_gate)
                        {
                            _pushed[agent.Name] = key;
                        }

                        _log?.Invoke($"fleet hub: {agent.Name}: fleet settings applied (version {state.Version}, {string.Join(", ", state.Managed)})");
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                    {
                        pushError = ex is TaskCanceledException ? "push timed out" : ex.Message;
                        _log?.Invoke($"fleet hub: {agent.Name}: push failed ({pushError}) — retrying next round");
                    }
                }

                // compared after the agent's own clamps; a local change since the last push shows here
                inSync = state.ManagedKey(profile) == state.ManagedKey(state.DesiredFor(agent.Name, profile));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex is TaskCanceledException ? "no answer (timed out)" : ex.Message;
        }

        var now = DateTime.UtcNow;
        bool online, changed;
        lock (_gate)
        {
            var previous = _machines[agent.Name];
            _machines[agent.Name] = status is not null
                ? previous with { Online = true, LastSeenUtc = now, Error = null, Status = status, Profile = profile ?? previous.Profile, InSync = inSync, PushError = pushError }
                : previous with
                {
                    Online = previous.LastSeenUtc is { } seen && (now - seen).TotalMilliseconds < OfflineAfterMs,
                    Error = error,
                };
            online = _machines[agent.Name].Online;
            changed = _wasOnline.GetValueOrDefault(agent.Name, !online) != online;
            _wasOnline[agent.Name] = online;
        }

        if (changed)
        {
            _log?.Invoke(online ? $"fleet hub: {agent.Name} is online" : $"fleet hub: {agent.Name} went offline ({error})");
        }
    }

    private async Task<FleetProfile> Push(HubAgent agent, string token, FleetProfile desired)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, agent.Url.TrimEnd('/') + AgentApi.Prefix + "profile")
        {
            Content = new StringContent(JsonSerializer.Serialize(desired, AgentApi.Json), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, _cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"agent refused the settings ({(int)response.StatusCode})");
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(_cts.Token), cancellationToken: _cts.Token);
        return doc.RootElement.GetProperty("profile").Deserialize<FleetProfile>(AgentApi.Json) ?? desired;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _http.Dispose();
        _cts.Dispose();
    }
}
