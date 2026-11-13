using System.Net;
using System.Net.Sockets;
using DeviceMaster.Control;
using DeviceMaster.Core.Lighting;

namespace DeviceMaster.Core.Tests;

public class FleetStateTests
{
    [Fact]
    public void OnlyManagedSectionsComeFromTheFleet()
    {
        var current = new FleetProfile { RgbEffect = RgbEffect.Static, RgbR = 10, Source = CurveSource.Cpu, TurzxScreen = LcdMode.Metrics };
        var state = new FleetState
        {
            Profile = new FleetProfile { RgbEffect = RgbEffect.Rainbow, RgbR = 200, Source = CurveSource.Hottest, TurzxScreen = LcdMode.Off },
            Managed = [FleetSection.Lighting],
        };

        var desired = state.DesiredFor("a", current);

        Assert.Equal(RgbEffect.Rainbow, desired.RgbEffect);
        Assert.Equal(200, desired.RgbR);
        Assert.Equal(CurveSource.Cpu, desired.Source);       // cooling not managed: untouched
        Assert.Equal(LcdMode.Metrics, desired.TurzxScreen);   // screens not managed: untouched
    }

    [Fact]
    public void AMachinesOwnLightingWins()
    {
        var state = new FleetState
        {
            Profile = new FleetProfile { RgbEffect = RgbEffect.Rainbow },
            Managed = [FleetSection.Lighting],
            Overrides = new Dictionary<string, FleetOverride> { ["b"] = new() { RgbEffect = RgbEffect.Static, RgbB = 255 } },
        };

        Assert.Equal(RgbEffect.Rainbow, state.DesiredFor("a", new FleetProfile()).RgbEffect);
        var b = state.DesiredFor("b", new FleetProfile());
        Assert.Equal(RgbEffect.Static, b.RgbEffect);
        Assert.Equal(255, b.RgbB);
    }

    [Fact]
    public void TheKeyIgnoresUnmanagedChanges()
    {
        var state = new FleetState { Managed = [FleetSection.Lighting] };
        var p = new FleetProfile();
        Assert.Equal(state.ManagedKey(p), state.ManagedKey(p with { Source = CurveSource.Gpu, TurzxBrightness = 10 }));
        Assert.NotEqual(state.ManagedKey(p), state.ManagedKey(p with { RgbSpeed = 5 }));
    }

    [Fact]
    public void TheStoreSavesAndAudits()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dm-fleet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "fleet-state.json");
            var store = new FleetStore(path);
            store.Update(s => s with { Managed = [FleetSection.Lighting], Profile = s.Profile with { RgbEffect = RgbEffect.Rainbow } }, "owner@example.com", "rainbow");

            var reloaded = new FleetStore(path).State;
            Assert.Equal(1, reloaded.Version);
            Assert.Equal([FleetSection.Lighting], reloaded.Managed);
            Assert.Equal(RgbEffect.Rainbow, reloaded.Profile.RgbEffect);
            Assert.Contains("owner@example.com", Assert.Single(store.RecentAudit(5)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class FleetPushTests : IDisposable
{
    private const string Token = "00112233445566778899aabbccddeeff";

    private sealed class FakeHost : IAgentHost
    {
        public ControlSettings Settings { get; } = new() { RgbEnabled = true, RgbR = 10, RgbG = 20, RgbB = 30 };
        public int Applies { get; private set; }

        public ControlStatus Status { get; } = new() { Running = true, Capabilities = new MachineCapabilities { MotherboardRgb = true } };

        public FleetProfile ReadProfile() => FleetProfile.From(Settings);

        public bool ApplyProfile(FleetProfile profile)
        {
            Applies++;
            return profile.ApplyTo(Settings);
        }
    }

    private readonly FakeHost _agent = new();
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dm-push-{Guid.NewGuid():N}");
    private readonly FleetStore _store;
    private readonly FleetHub _hub;

    public FleetPushTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _url = $"http://localhost:{port}";
        _listener.Prefixes.Add(_url + "/");
        _listener.Start();
        var api = new AgentApi(_agent, Token);
        new Thread(() =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    if (!api.TryHandle(ctx))
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                }
                catch
                {
                    break;
                }
            }
        }) { IsBackground = true }.Start();

        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "token"), Token);
        _store = new FleetStore(Path.Combine(_dir, "fleet-state.json"));
        _hub = new FleetHub(new HubConfig { Agents = [new HubAgent { Name = "A", Url = _url, TokenFile = Path.Combine(_dir, "token") }] }, store: _store);
    }

    [Fact]
    public async Task NothingIsPushedUntilTheFleetManagesASection()
    {
        await _hub.PollOnce();
        Assert.Equal(0, _agent.Applies);
        Assert.Null(_hub.Machine("A")!.InSync);
    }

    [Fact]
    public async Task FleetLightingIsPushedOnceAndThenLeftAlone()
    {
        _store.Update(s => s with { Managed = [FleetSection.Lighting], Profile = s.Profile with { RgbEffect = RgbEffect.Rainbow, RgbSpeed = 4 } }, "t", "t");

        await _hub.PollOnce();
        Assert.Equal(1, _agent.Applies);
        Assert.Equal(RgbEffect.Rainbow, _agent.Settings.RgbEffect);
        Assert.Equal(4, _agent.Settings.RgbSpeed);
        Assert.True(_hub.Machine("A")!.InSync);

        await _hub.PollOnce();
        Assert.Equal(1, _agent.Applies); // no change, no push

        // a local change on the machine is not fought over — it shows as differing
        _agent.Settings.RgbEffect = RgbEffect.Static;
        await _hub.PollOnce();
        Assert.Equal(1, _agent.Applies);
        Assert.False(_hub.Machine("A")!.InSync);

        // the next fleet change goes out again
        _store.Update(s => s with { Profile = s.Profile with { RgbSpeed = 2 } }, "t", "t");
        await _hub.PollOnce();
        Assert.Equal(2, _agent.Applies);
        Assert.Equal(RgbEffect.Rainbow, _agent.Settings.RgbEffect);
        Assert.Equal(2, _agent.Settings.RgbSpeed);
    }

    [Fact]
    public async Task OnlyTheManagedSectionChangesOnTheMachine()
    {
        _agent.Settings.SetSource(CurveSource.Cpu);
        _agent.Settings.PumpDutyPercent = 80;
        _store.Update(s => s with
        {
            Managed = [FleetSection.Lighting],
            Profile = s.Profile with { Source = CurveSource.Hottest, PumpDutyPercent = 100, RgbOff = true },
        }, "t", "t");

        await _hub.PollOnce();

        Assert.True(_agent.Settings.RgbOff);
        Assert.Equal(CurveSource.Cpu, _agent.Settings.Source);  // cooling stays the machine's own
        Assert.Equal(80, _agent.Settings.PumpDutyPercent);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _listener.Stop();
        _listener.Close();
        Directory.Delete(_dir, recursive: true);
    }
}
