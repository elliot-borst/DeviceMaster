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
    public MachineCapabilities? Capabilities { get; init; }
}

/// <summary>
/// The fleet hub's collector (#33 phase 3): asks every agent for its status on a short cadence and
/// keeps the latest answer per machine. Read-only — it never writes to a machine (phase 4 adds
/// profile pushes). A machine that stops answering is marked offline after
/// <see cref="OfflineAfterMs"/>; its last status is kept so the overview can still show it.
/// </summary>
public sealed class FleetHub : IDisposable
{
    public const int OfflineAfterMs = 10_000;

    private readonly HubConfig _config;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, MachineSnapshot> _machines = [];
    private readonly Dictionary<string, bool> _wasOnline = [];
    private Thread? _thread;

    public FleetHub(HubConfig config, Action<string>? log = null, HttpMessageHandler? handler = null)
    {
        _config = config;
        _log = log;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(3);
        foreach (var agent in config.Agents)
        {
            _machines[agent.Name] = new MachineSnapshot { Name = agent.Name, Url = agent.Url, Error = "not polled yet" };
        }
    }

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

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "DeviceMaster fleet hub" };
        _thread.Start();
        _log?.Invoke($"fleet hub: watching {_config.Agents.Count} machine(s): {string.Join(", ", _config.Agents.Select(a => a.Name))}");
    }

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            PollOnce().GetAwaiter().GetResult();
            _cts.Token.WaitHandle.WaitOne(Math.Max(_config.PollMs, 500));
        }
    }

    /// <summary>One round of status requests to every agent, in parallel (public for tests).</summary>
    public Task PollOnce() => Task.WhenAll(_config.Agents.Select(PollAgent));

    private async Task PollAgent(HubAgent agent)
    {
        ControlStatus? status = null;
        string? error = null;
        try
        {
            var token = File.ReadAllText(agent.TokenFile).Trim();
            using var request = new HttpRequestMessage(HttpMethod.Get, agent.Url.TrimEnd('/') + AgentApi.Prefix + "status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, _cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                error = $"agent answered {(int)response.StatusCode}";
            }
            else
            {
                status = await JsonSerializer.DeserializeAsync<ControlStatus>(await response.Content.ReadAsStreamAsync(_cts.Token), AgentApi.Json, _cts.Token);
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
                ? previous with { Online = true, LastSeenUtc = now, Error = null, Status = status }
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

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _http.Dispose();
        _cts.Dispose();
    }
}
