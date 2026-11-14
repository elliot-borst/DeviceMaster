using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DeviceMaster.Control;

/// <summary>Another fleet hub in the failover group (#68).</summary>
public sealed record HubPeer
{
    /// <summary>The peer's name (e.g. its machine name), unique in the group.</summary>
    public string Name { get; init; } = "";

    /// <summary>Its peer API base URL, e.g. http://100.x.y.z:27081.</summary>
    public string Url { get; init; } = "";

    /// <summary>Lower runs first: the reachable hub with the lowest number is the active one.</summary>
    public int Priority { get; init; }
}

/// <summary>Active hub: pushes fleet settings, sends power, keeps the audit. Standby: watches and forwards writes.</summary>
public enum HubRole
{
    Standby,
    Active,
}

/// <summary>One hub as the election sees it.</summary>
public sealed record HubCandidate(string Name, int Priority, bool Reachable, long? UpSince, bool ClaimsActive);

/// <summary>
/// The failover election (#68) — pure, so every hub reaches the same answer from the same facts.
/// The active hub is the lowest-priority-number hub that is reachable and settled: up for the
/// hold time, or already the active one. The hold time stops a rebooting higher-priority hub from
/// grabbing control the moment it answers (and dropping it again a second later), gives it time to
/// pull the latest state first, and makes a freshly started hub wait before claiming anything.
/// </summary>
public static class HubElection
{
    public static string? Elect(IEnumerable<HubCandidate> candidates, long now, long holdMs) =>
        candidates
            .Where(c => c.Reachable && c.UpSince is { } up && (now - up >= holdMs || c.ClaimsActive))
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .FirstOrDefault()?.Name;
}

/// <summary>What a hub says about itself to its peers.</summary>
public sealed record HubHeartbeat(string Name, int Priority, HubRole Role, long StateVersion);

/// <summary>Fleet state as hubs hand it to each other: the state plus the recent audit log.</summary>
public sealed record HubStateTransfer(FleetState State, IReadOnlyList<string> Audit);

/// <summary>
/// Runs this hub's side of the failover group (#68): heartbeats every peer, elects the active hub,
/// keeps standbys' fleet state in step with the active one, and tells the hub (and its web server)
/// which role it has. With no peers configured it is simply always active.
/// </summary>
public sealed class HubFailover : IDisposable
{
    /// <summary>A peer that hasn't answered for this long is down.</summary>
    public const int DownAfterMs = 15_000;

    /// <summary>How long a hub must be up before it may take (or take back) the active role.</summary>
    public const int HoldMs = 60_000;

    public const int HeartbeatMs = 3_000;

    /// <summary>The active hub re-sends its state to every standby this often, changes or not.</summary>
    public const int StateResendMs = 30_000;

    private readonly string _self;
    private readonly int _priority;
    private readonly IReadOnlyList<HubPeer> _peers;
    private readonly FleetStore? _store;
    private readonly string? _token;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private readonly Func<long> _clock;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly long _startedAt;
    private readonly Dictionary<string, PeerSeen> _seen = [];
    private readonly Dictionary<string, (long Version, long At)> _pushedTo = [];
    private Thread? _thread;
    private HubRole _role;

    private sealed class PeerSeen
    {
        public long LastOk = long.MinValue / 4; // "never" — far past, but now - LastOk can't overflow
        public long? UpSince;
        public HubHeartbeat? Last;
    }

    public HubFailover(string self, int priority, IReadOnlyList<HubPeer> peers, FleetStore? store, string? token,
        Action<string>? log = null, HttpMessageHandler? handler = null, Func<long>? clock = null)
    {
        _self = self;
        _priority = priority;
        _peers = peers.Where(p => p.Name != self).ToList();
        _store = store;
        _token = token;
        _log = log;
        _clock = clock ?? (() => Environment.TickCount64);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(4);
        _startedAt = _clock();
        _role = _peers.Count == 0 ? HubRole.Active : HubRole.Standby; // alone: nothing to wait for
    }

    public string Self => _self;

    public int Priority => _priority;

    public bool HasPeers => _peers.Count > 0;

    /// <summary>Raised (on the failover thread) whenever this hub's role changes.</summary>
    public event Action<HubRole>? RoleChanged;

    public HubRole Role
    {
        get
        {
            lock (_gate)
            {
                return _role;
            }
        }
    }

    public bool IsActive => Role == HubRole.Active;

    /// <summary>The peer currently acting as the active hub, when it isn't this one (where to forward writes).</summary>
    public HubPeer? ActivePeer
    {
        get
        {
            lock (_gate)
            {
                return _peers.FirstOrDefault(p => _seen.TryGetValue(p.Name, out var s)
                    && s.Last?.Role == HubRole.Active && _clock() - s.LastOk < DownAfterMs);
            }
        }
    }

    /// <summary>What this hub tells its peers.</summary>
    public HubHeartbeat Heartbeat() => new(_self, _priority, Role, _store?.State.Version ?? 0);

    /// <summary>Every hub in the group as this one sees it, for the status page.</summary>
    public IReadOnlyList<object> Describe()
    {
        var now = _clock();
        lock (_gate)
        {
            var list = new List<object> { new { name = _self, priority = _priority, role = _role.ToString(), online = true, self = true } };
            foreach (var p in _peers)
            {
                var s = _seen.GetValueOrDefault(p.Name);
                list.Add(new
                {
                    name = p.Name,
                    priority = p.Priority,
                    role = s?.Last?.Role.ToString(),
                    online = s is not null && now - s.LastOk < DownAfterMs,
                    self = false,
                });
            }

            return list;
        }
    }

    public void Start()
    {
        if (_peers.Count == 0)
        {
            return;
        }

        _log?.Invoke($"hub failover: {_self} (priority {_priority}) with peers {string.Join(", ", _peers.OrderBy(p => p.Priority).Select(p => $"{p.Name} ({p.Priority})"))} — standby until settled");
        _thread = new Thread(Run) { IsBackground = true, Name = "hub failover" };
        _thread.Start();
    }

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                RoundAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                _log?.Invoke($"hub failover: round failed: {ex.Message}");
            }

            _cts.Token.WaitHandle.WaitOne(HeartbeatMs);
        }
    }

    /// <summary>One heartbeat round, election and state sync (public for tests).</summary>
    public async Task RoundAsync()
    {
        await Task.WhenAll(_peers.Select(PollPeer));
        var now = _clock();

        HubRole was, next;
        string? winner;
        lock (_gate)
        {
            was = _role;
            var candidates = _peers.Select(p =>
            {
                var s = _seen.GetValueOrDefault(p.Name);
                var reachable = s is not null && now - s.LastOk < DownAfterMs;
                return new HubCandidate(p.Name, p.Priority, reachable, reachable ? s!.UpSince : null, s?.Last?.Role == HubRole.Active);
            }).Append(new HubCandidate(_self, _priority, true, _startedAt, _role == HubRole.Active));
            winner = HubElection.Elect(candidates, now, HoldMs);
            next = winner == _self ? HubRole.Active : HubRole.Standby;
        }

        if (next == HubRole.Active && was != HubRole.Active)
        {
            await PullNewestState(); // carry on from whatever the last active hub had
        }

        if (next != was)
        {
            lock (_gate)
            {
                _role = next;
            }

            _log?.Invoke(next == HubRole.Active
                ? $"hub failover: {_self} is now the ACTIVE hub"
                : $"hub failover: {_self} is standby — {winner ?? "no hub"} is active");
            RoleChanged?.Invoke(next);
        }

        if (next == HubRole.Active)
        {
            await PushState(now);
        }
    }

    private async Task PollPeer(HubPeer peer)
    {
        HubHeartbeat? beat = null;
        try
        {
            beat = await Get<HubHeartbeat>(peer, "heartbeat");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // judged by silence below
        }

        var now = _clock();
        lock (_gate)
        {
            if (!_seen.TryGetValue(peer.Name, out var s))
            {
                _seen[peer.Name] = s = new PeerSeen();
            }

            var wasUp = now - s.LastOk < DownAfterMs;
            if (beat is not null)
            {
                if (!wasUp)
                {
                    s.UpSince = now; // (re)appeared: the hold time starts now
                    _log?.Invoke($"hub failover: peer {peer.Name} is up");
                }

                s.LastOk = now;
                s.Last = beat;
            }

            if (beat is null && s.UpSince is not null && now - s.LastOk >= DownAfterMs)
            {
                s.UpSince = null;
                _pushedTo.Remove(peer.Name);
                _log?.Invoke($"hub failover: peer {peer.Name} is down");
            }
        }
    }

    private async Task PullNewestState()
    {
        if (_store is null)
        {
            return;
        }

        HubPeer? best;
        lock (_gate)
        {
            var now = _clock();
            best = _peers
                .Where(p => _seen.TryGetValue(p.Name, out var s) && now - s.LastOk < DownAfterMs && s.Last is not null)
                .OrderByDescending(p => _seen[p.Name].Last!.StateVersion)
                .FirstOrDefault();
            if (best is null || _seen[best.Name].Last!.StateVersion <= _store.State.Version)
            {
                return;
            }
        }

        try
        {
            if (await Get<HubStateTransfer>(best, "state") is { } transfer && _store.Replace(transfer.State, transfer.Audit))
            {
                _log?.Invoke($"hub failover: took fleet state version {transfer.State.Version} from {best.Name}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log?.Invoke($"hub failover: could not pull state from {best.Name}: {ex.Message} — carrying on with version {_store.State.Version}");
        }
    }

    private async Task PushState(long now)
    {
        if (_store is null)
        {
            return;
        }

        var version = _store.State.Version;
        List<HubPeer> due;
        lock (_gate)
        {
            due = _peers.Where(p => _seen.TryGetValue(p.Name, out var s) && now - s.LastOk < DownAfterMs
                && (!_pushedTo.TryGetValue(p.Name, out var last) || last.Version != version || now - last.At >= StateResendMs)).ToList();
        }

        if (due.Count == 0)
        {
            return;
        }

        var body = JsonSerializer.Serialize(new HubStateTransfer(_store.State, _store.RecentAudit(200).Reverse().ToList()), AgentApi.Json);
        foreach (var peer in due)
        {
            try
            {
                using var request = Request(HttpMethod.Put, peer, "state");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(request, _cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    lock (_gate)
                    {
                        _pushedTo[peer.Name] = (version, now);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // retried next round
            }
        }
    }

    /// <summary>A peer pushed its state (only the active one does); kept when it's newer than ours.</summary>
    public bool Accept(HubStateTransfer transfer)
    {
        if (_store is null || Role == HubRole.Active)
        {
            return false; // the active hub's own state is the source of truth
        }

        return _store.Replace(transfer.State, transfer.Audit);
    }

    /// <summary>This hub's state for a peer taking over.</summary>
    public HubStateTransfer? Export() =>
        _store is null ? null : new HubStateTransfer(_store.State, _store.RecentAudit(200).Reverse().ToList());

    /// <summary>Whether a request carries the group's peer token.</summary>
    public bool Authorized(string? authorization) =>
        _token is { Length: > 0 } t && authorization is { } a && a == $"Bearer {t}";

    /// <summary>
    /// Sends a user's write to the active hub (this one is standby) and returns its answer.
    /// The signed-in user travels in the Tailscale-User-Login header; the peer token proves it came from a hub.
    /// </summary>
    public async Task<(int Status, string ContentType, string Body)> ForwardToActiveAsync(string method, string pathAndQuery, string? body, string? user)
    {
        if (ActivePeer is not { } active)
        {
            return (503, "application/json; charset=utf-8", """{"ok":false,"error":"no active hub right now — try again in a moment"}""");
        }

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), active.Url.TrimEnd('/') + pathAndQuery);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (user is not null)
            {
                request.Headers.Add("Tailscale-User-Login", user);
            }

            request.Content = new StringContent(body ?? "", Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, _cts.Token);
            return ((int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8",
                await response.Content.ReadAsStringAsync(_cts.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (502, "application/json; charset=utf-8", JsonSerializer.Serialize(new { ok = false, error = $"active hub {active.Name} didn't answer: {ex.Message}" }));
        }
    }

    private HttpRequestMessage Request(HttpMethod method, HubPeer peer, string route)
    {
        var request = new HttpRequestMessage(method, peer.Url.TrimEnd('/') + "/api/hub/v1/" + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private async Task<T?> Get<T>(HubPeer peer, string route)
    {
        using var request = Request(HttpMethod.Get, peer, route);
        using var response = await _http.SendAsync(request, _cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"peer answered {(int)response.StatusCode}");
        }

        return await JsonSerializer.DeserializeAsync<T>(await response.Content.ReadAsStreamAsync(_cts.Token), AgentApi.Json, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _http.Dispose();
        _cts.Dispose();
    }
}
