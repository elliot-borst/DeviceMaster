using System.Net;
using System.Text;
using System.Text.Json;
using DeviceMaster.Control;

namespace DeviceMaster.Core.Tests;

public class HubElectionTests
{
    private const long Hold = HubFailover.HoldMs;

    [Fact]
    public void TheHighestPrioritySettledHubIsActive() =>
        Assert.Equal("StormVault", HubElection.Elect(
        [
            new("MindForge", 2, true, 0, false),
            new("StormVault", 1, true, 0, false),
            new("NightWatch", 3, true, 0, false),
        ], now: Hold, holdMs: Hold));

    [Fact]
    public void WhenTheActiveHubIsDownTheNextOneTakesOver() =>
        Assert.Equal("MindForge", HubElection.Elect(
        [
            new("StormVault", 1, false, null, true),
            new("MindForge", 2, true, 0, false),
            new("NightWatch", 3, true, 0, false),
        ], now: Hold * 5, holdMs: Hold));

    [Fact]
    public void AReturningHubWaitsTheHoldTimeBeforeTakingBack()
    {
        HubCandidate[] Group(long stormUpSince) =>
        [
            new("StormVault", 1, true, stormUpSince, false),
            new("MindForge", 2, true, 0, true),
        ];

        var back = 10 * Hold;
        Assert.Equal("MindForge", HubElection.Elect(Group(back), now: back + Hold - 1, holdMs: Hold));  // just back: not yet
        Assert.Equal("StormVault", HubElection.Elect(Group(back), now: back + Hold, holdMs: Hold));     // settled: fails back
    }

    [Fact]
    public void AFreshlyStartedGroupWaitsBeforeAnyoneClaims() =>
        Assert.Null(HubElection.Elect([new("StormVault", 1, true, 0, false), new("MindForge", 2, true, 0, false)], now: Hold - 1, holdMs: Hold));

    [Fact]
    public void TwoClaimantsAfterASplitResolveToTheHigherPriority() =>
        Assert.Equal("StormVault", HubElection.Elect(
            [new("MindForge", 2, true, 0, true), new("StormVault", 1, true, 0, true)], now: 1, holdMs: Hold));

    [Theory]
    [InlineData("PUT", "/api/fleet/settings/lighting", true)]
    [InlineData("POST", "/api/fleet/machines/StormVault/power", true)]
    [InlineData("POST", "/m/MindForge/config.json", true)]
    [InlineData("GET", "/api/fleet/settings", false)]
    [InlineData("GET", "/m/MindForge/status.json", false)]
    public void WritesAreTheRequestsAStandbyForwards(string method, string path, bool write) =>
        Assert.Equal(write, DeviceMaster.App.Headless.HubWebServer.IsWrite(method, path));
}

/// <summary>Two hubs in one process: their peer APIs wired to each other through a fake network.</summary>
public sealed class HubFailoverTests : IDisposable
{
    private const string Token = "peer-token-for-tests";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dm-failover-{Guid.NewGuid():N}");
    private long _now = 1_000_000;
    private readonly Dictionary<string, HubFailover?> _net = [];

    private sealed class FakeNet(HubFailoverTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var host = request.RequestUri!.Host;
            if (owner._net.GetValueOrDefault(host) is not { } hub)
            {
                throw new HttpRequestException("no route to host"); // that hub is down
            }

            if (request.Headers.Authorization?.ToString() != $"Bearer {Token}")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var route = request.RequestUri.AbsolutePath["/api/hub/v1/".Length..];
            object? answer = (request.Method.Method, route) switch
            {
                ("GET", "heartbeat") => hub.Heartbeat(),
                ("GET", "state") => hub.Export(),
                ("PUT", "state") => new { taken = hub.Accept(JsonSerializer.Deserialize<HubStateTransfer>(await request.Content!.ReadAsStringAsync(token), AgentApi.Json)!) },
                _ => null,
            };
            return new HttpResponseMessage(answer is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(answer, AgentApi.Json), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static readonly HubPeer A = new() { Name = "a", Url = "http://a:27081", Priority = 1 };
    private static readonly HubPeer B = new() { Name = "b", Url = "http://b:27081", Priority = 2 };

    public HubFailoverTests() => Directory.CreateDirectory(_dir);

    private (HubFailover Hub, FleetStore Store) Start(HubPeer self, List<string>? log = null)
    {
        var store = new FleetStore(Path.Combine(_dir, $"{self.Name}-state.json"));
        var hub = new HubFailover(self.Name, self.Priority, [A, B], store, Token, log is null ? null : log.Add, new FakeNet(this), () => _now);
        _net[self.Name] = hub;
        return (hub, store);
    }

    private async Task Round(params HubFailover[] hubs)
    {
        foreach (var hub in hubs)
        {
            await hub.RoundAsync();
        }
    }

    private async Task Run(long ms, params HubFailover[] hubs)
    {
        for (var t = 0L; t < ms; t += HubFailover.HeartbeatMs)
        {
            _now += HubFailover.HeartbeatMs;
            await Round(hubs);
        }
    }

    [Fact]
    public async Task FailsOverToTheNextHubAndBackWithTheLatestState()
    {
        var (a, storeA) = Start(A);
        var (b, storeB) = Start(B);

        // both start standby; once settled the higher priority (a) is active
        await Round(a, b);
        Assert.False(a.IsActive);
        Assert.False(b.IsActive);
        await Run(HubFailover.HoldMs + HubFailover.HeartbeatMs, a, b);
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);
        Assert.Equal("a", b.ActivePeer?.Name);

        // a change on the active hub reaches the standby
        storeA.Update(s => s with { Managed = [FleetSection.Lighting] }, "owner", "lighting managed");
        await Round(a, b);
        Assert.Equal(storeA.State.Version, storeB.State.Version);
        Assert.Contains("lighting managed", storeB.RecentAudit(1).Single());

        // a goes away: b takes over after the down time, and carries on from a's state
        _net["a"] = null;
        await Run(HubFailover.DownAfterMs + HubFailover.HeartbeatMs, b);
        Assert.True(b.IsActive);
        storeB.Update(s => s with { Managed = [FleetSection.Lighting, FleetSection.Cooling] }, "owner", "cooling managed");

        // a comes back (restarted): standby while it settles, picks up b's newer state, then takes over
        var (a2, storeA2) = Start(A);
        await Run(HubFailover.HeartbeatMs * 2, a2, b);
        Assert.False(a2.IsActive);
        Assert.True(b.IsActive);
        Assert.Equal(storeB.State.Version, storeA2.State.Version);                // pushed by the active b
        await Run(HubFailover.HoldMs + HubFailover.HeartbeatMs, a2, b);
        Assert.True(a2.IsActive);
        Assert.False(b.IsActive);
        Assert.Contains(FleetSection.Cooling, storeA2.State.Managed);             // nothing lost
    }

    [Fact]
    public async Task AStandbyNeverOverwritesTheActiveHubsState()
    {
        var (a, storeA) = Start(A);
        var (b, _) = Start(B);
        await Run(HubFailover.HoldMs + HubFailover.HeartbeatMs, a, b);
        storeA.Update(s => s with { Managed = [FleetSection.Screens] }, "owner", "x");

        var stale = new HubStateTransfer(new FleetState { Version = storeA.State.Version + 5 }, []);
        Assert.False(a.Accept(stale));                                           // the active hub's state wins
        Assert.Contains(FleetSection.Screens, storeA.State.Managed);
    }

    [Fact]
    public void ALoneHubIsAlwaysActive()
    {
        var store = new FleetStore(Path.Combine(_dir, "lone.json"));
        using var lone = new HubFailover("solo", 1, [], store, null);
        Assert.True(lone.IsActive);
        Assert.False(lone.HasPeers);
    }

    [Fact]
    public void OnlyThePeerTokenIsAccepted()
    {
        var (a, _) = Start(A);
        Assert.True(a.Authorized($"Bearer {Token}"));
        Assert.False(a.Authorized("Bearer wrong"));
        Assert.False(a.Authorized(null));
    }

    public void Dispose()
    {
        foreach (var hub in _net.Values)
        {
            hub?.Dispose();
        }

        Directory.Delete(_dir, recursive: true);
    }
}
