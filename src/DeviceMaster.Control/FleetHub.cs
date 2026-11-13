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

    /// <summary>Wake-on-LAN MAC of the machine's wired NIC (#49); null = can't be powered on from the fleet.</summary>
    public string? Mac { get; init; }
}

/// <summary>The hub's configuration file (#33 phase 3).</summary>
public sealed record HubConfig
{
    public List<HubAgent> Agents { get; init; } = [];

    /// <summary>How often each agent is asked for its status.</summary>
    public int PollMs { get; init; } = 2_000;

    /// <summary>
    /// The agent that sends Wake-on-LAN packets (#49): it must sit on the LAN with host networking —
    /// the hub's own namespace can't broadcast there. Null = power on isn't offered.
    /// </summary>
    public string? WakeVia { get; init; }

    /// <summary>SABnzbd base URL for the display (#49), e.g. http://192.168.1.5:8080; null = not shown.</summary>
    public string? SabnzbdUrl { get; init; }

    /// <summary>File holding SABnzbd's API key (mounted read-only into the hub).</summary>
    public string? SabnzbdApiKeyFile { get; init; }

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
        UptimeSeconds = Status?.UptimeSeconds,
        Storage = Status?.Storage ?? [],
        Docker = Status?.Docker,
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
    public double? UptimeSeconds { get; init; }
    public IReadOnlyList<StorageInfo> Storage { get; init; } = [];
    public DockerInfo? Docker { get; init; }

    /// <summary>Whether the fleet can power this machine on (set by the hub).</summary>
    public bool CanWake { get; init; }
}


/// <summary>
/// The fleet hub's collector and reconciler (#33 phases 3–4). Asks every agent for its status and
/// its settings on a short cadence and keeps the latest answer per machine; a machine that stops
/// answering is marked offline after <see cref="OfflineAfterMs"/> (its last status is kept).
/// <para>
/// With a <see cref="FleetStore"/>, it also pushes the fleet's managed sections to each machine —
/// only when what the fleet wants for that machine changed since its last successful push (a
/// machine that was offline gets it when it comes back). A lighting or screens change made on a
/// machine itself is adopted as the fleet's and pushed to the others (two-way sync); a local
/// cooling change is not overwritten until the next fleet change, and shows as "differs".
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
    private readonly Dictionary<(string Machine, FleetSection Section), string> _baseline = []; // section as the machine ran it after the last push
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

    /// <summary>Whether a machine can be woken from the fleet (it has a MAC and a waking agent is set).</summary>
    public bool CanWake(string name) =>
        _config.WakeVia is { Length: > 0 } via && via != name && Knows(via)
        && _config.Agents.FirstOrDefault(a => a.Name == name)?.Mac is { Length: > 0 } mac && WakeOnLan.ParseMac(mac) is not null;

    /// <summary>
    /// Power action for one machine (#49): "off" asks its agent for a clean shutdown, "cancel"
    /// withdraws it, "on" asks the waking agent to send its Wake-on-LAN packet.
    /// </summary>
    public Task<(int Status, string Body)> PowerAsync(string name, string action, string who) => action switch
    {
        "off" => ForwardAsync(name, HttpMethod.Post, "power/shutdown", headers: new() { ["X-DeviceMaster-Reason"] = $"fleet hub, {who}" }),
        "reboot" => ForwardAsync(name, HttpMethod.Post, "power/reboot", headers: new() { ["X-DeviceMaster-Reason"] = $"fleet hub, {who}" }),
        "cancel" => ForwardAsync(name, HttpMethod.Post, "power/cancel"),
        "on" when CanWake(name) => ForwardAsync(_config.WakeVia!, HttpMethod.Post, "power/wake",
            JsonSerializer.Serialize(new { mac = _config.Agents.First(a => a.Name == name).Mac })),
        "on" => Task.FromResult((409, """{"error":"no MAC address or waking machine set up for this server"}""")),
        _ => Task.FromResult((400, """{"error":"action must be on, off, reboot or cancel"}""")),
    };

    /// <summary>
    /// Sends one agent API request to a machine with its token (phase 5: the full per-server page
    /// goes through here). Returns the agent's status code and body; 502 when it can't be reached.
    /// </summary>
    public async Task<(int Status, string Body)> ForwardAsync(string machine, HttpMethod method, string route, string? body = null, Dictionary<string, string>? headers = null)
    {
        var agent = _config.Agents.FirstOrDefault(a => a.Name == machine);
        if (agent is null)
        {
            return (404, """{"error":"unknown machine"}""");
        }

        try
        {
            var token = File.ReadAllText(agent.TokenFile).Trim();
            using var request = new HttpRequestMessage(method, agent.Url.TrimEnd('/') + AgentApi.Prefix + route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            foreach (var (k, v) in headers ?? [])
            {
                request.Headers.TryAddWithoutValidation(k, v);
            }
            if (body is not null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }
            else if (method != HttpMethod.Get)
            {
                // a bodiless POST goes out without Content-Length, which the Linux agents' listener
                // answers with 411 (after acting on it) — send an explicit empty body
                request.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, _cts.Token);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(_cts.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return (502, JsonSerializer.Serialize(new { error = $"{machine} didn't answer ({ex.Message})" }));
        }
    }

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
        var sab = _config.SabnzbdUrl is { Length: > 0 } sabUrl && _config.SabnzbdApiKeyFile is { Length: > 0 } sabKey
            ? new SabnzbdProbe(sabUrl, sabKey, _http)
            : null;
        long sabDue = 0;
        while (!_cts.IsCancellationRequested)
        {
            PollOnce().GetAwaiter().GetResult();
            if (sab is not null && Environment.TickCount64 >= sabDue)
            {
                sabDue = Environment.TickCount64 + 5_000;
                Sabnzbd = sab.FetchAsync(_cts.Token).GetAwaiter().GetResult();
            }

            WaitHandle.WaitAny([_cts.Token.WaitHandle, _wake], Math.Max(_config.PollMs, 500));
        }
    }

    /// <summary>SABnzbd's queue for the display (null = not configured).</summary>
    public volatile SabnzbdStatus? Sabnzbd;

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
                state = AdoptLocalChanges(agent.Name, state, profile);
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
                            RememberBaseline(agent.Name, state, profile); // what it runs after its own clamps
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

    /// <summary>
    /// Two-way sync: a managed, adoptable section (lighting, screens) that a machine changed itself —
    /// on its Windows app or its own page — since the hub last pushed to it becomes the fleet's
    /// (or that machine's override, if it has one), and the next round pushes it to the others.
    /// "Changed itself" = different from what it ran right after the last push AND from what the
    /// fleet wants; the first keeps a machine whose clamps reshape a value from re-adopting it forever.
    /// </summary>
    private FleetState AdoptLocalChanges(string machine, FleetState state, FleetProfile reported)
    {
        foreach (var section in state.Managed.Intersect(FleetState.Adoptable))
        {
            var now = FleetState.SectionKey(section, reported);
            string? baseline;
            lock (_gate)
            {
                baseline = _baseline.GetValueOrDefault((machine, section));
            }

            if (baseline is null || now == baseline || now == FleetState.SectionKey(section, state.DesiredFor(machine, reported)))
            {
                continue;
            }

            state = _store!.Update(s => s.Adopt(machine, section, reported), $"{machine} (set on the machine)",
                $"{section} changed on {machine} — synced to the fleet");
            lock (_gate)
            {
                _baseline[(machine, section)] = now;
                _pushed[machine] = state.ManagedKey(state.DesiredFor(machine, reported)); // it already runs this
            }

            _log?.Invoke($"fleet hub: {machine}: {section} changed on the machine — adopted as the fleet's (version {state.Version})");
            _wake.Set(); // push it to the others now, not in a poll interval
        }

        return state;
    }

    private void RememberBaseline(string machine, FleetState state, FleetProfile running)
    {
        foreach (var section in state.Managed.Intersect(FleetState.Adoptable))
        {
            _baseline[(machine, section)] = FleetState.SectionKey(section, running);
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
