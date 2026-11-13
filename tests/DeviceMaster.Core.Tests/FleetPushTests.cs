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

public class FleetCoolingTests
{
    [Fact]
    public void FleetCoolingAlwaysUsesEachMachinesHottestSensor()
    {
        var safe = FleetOverrides.SanitizeCooling(new FleetProfile { Source = CurveSource.Coolant, Mode = ControlMode.Off, PumpDutyPercent = 20 })!;
        Assert.Equal(CurveSource.Hottest, safe.Source);          // coolant/GPU-only would failsafe machines without that sensor
        Assert.Equal(ControlMode.Curve, safe.Mode);              // never hand fans back to hardware from the fleet
        Assert.Equal(DeviceMaster.Core.Safety.SafetyLimits.PumpMinimumDutyPercent, safe.PumpDutyPercent);
    }

    [Fact]
    public void AnUnusableCurveIsRejected() =>
        Assert.Null(FleetOverrides.SanitizeCooling(new FleetProfile { CurvePoints = [new(50, 50)] }));

    [Fact]
    public void TheOwnersCurveSurvivesSanitizing()
    {
        var safe = FleetOverrides.SanitizeCooling(new FleetProfile { CurvePoints = [new(40, 30), new(85, 100)] })!;
        Assert.Equal([new(40, 30), new(85, 100)], safe.CurvePoints);
    }

    [Theory]
    [InlineData(FleetSection.Lighting)]
    [InlineData(FleetSection.Screens)]
    [InlineData(FleetSection.Cooling)]
    public void OverridesSetAndClearPerSection(FleetSection section)
    {
        var own = FleetOverrides.Set(new FleetOverride(), section, new FleetProfile { Mode = ControlMode.Manual, LcdScreens = LcdMode.Off, RgbOff = true });
        Assert.True(FleetOverrides.Has(own, section));
        Assert.All(Enum.GetValues<FleetSection>().Where(s => s != section), other => Assert.False(FleetOverrides.Has(own, other)));
        Assert.False(FleetOverrides.Has(FleetOverrides.Clear(own, section), section));
    }

    [Fact]
    public void FleetCoolingReachesTheMachineThroughItsOwnClamps()
    {
        var machine = new ControlSettings { PumpDutyPercent = 100 };
        machine.SetSource(CurveSource.Cpu);
        var state = new FleetState
        {
            Profile = FleetOverrides.SanitizeCooling(new FleetProfile { CurvePoints = [new(40, 30), new(85, 100)], PumpDutyPercent = 100 })!,
            Managed = [FleetSection.Cooling],
        };

        state.DesiredFor("x", FleetProfile.From(machine)).ApplyTo(machine);

        Assert.Equal(CurveSource.Hottest, machine.Source);
        Assert.Equal(ControlMode.Curve, machine.Mode);
        Assert.Equal([new(40, 30), new(85, 100)], machine.CurvePoints);
        Assert.Equal(100, machine.PumpDutyPercent);
    }
}

public class GpuHandBackTests
{
    [Theory]
    [InlineData(false, 30, true)]   // low duty: the card takes its fans back (idle fan stop possible)
    [InlineData(false, 0, true)]
    [InlineData(false, 31, false)]
    [InlineData(true, 34, true)]    // in the gap: stay with the card (no flapping)
    [InlineData(true, 35, false)]   // rising: DeviceMaster takes them back
    [InlineData(true, 100, false)]  // failsafe is always ours
    public void GpuFansFollowTheCardWhenTheCurveIsLow(bool onCardNow, int requested, bool expected) =>
        Assert.Equal(expected, DeviceMaster.Core.Safety.SafetyGuard.GpuOnCardControl(onCardNow, requested));
}

public sealed class FullSettingsTests : IDisposable
{
    private const string Token = "99887766554433221100ffeeddccbbaa";

    private sealed class Host : IAgentHost
    {
        public ControlSettings Settings { get; } = new() { RgbR = 1 };
        public ControlStatus Status { get; } = new() { Running = true, Mode = ControlMode.Curve };
        public FleetProfile ReadProfile() => FleetProfile.From(Settings);
        public bool ApplyProfile(FleetProfile profile) => profile.ApplyTo(Settings);
        public string? ReadSettingsJson() => System.Text.Json.JsonSerializer.Serialize(Settings, AgentApi.Json);
        public IReadOnlyList<string>? PatchSettings(string patchJson, out bool invalid) => ConfigPatcher.ApplyPatch(Settings, patchJson, out invalid);
    }

    private readonly Host _agent = new();
    private readonly HttpListener _listener = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dm-full-{Guid.NewGuid():N}");
    private readonly FleetHub _hub;

    public FullSettingsTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        var api = new AgentApi(_agent, Token);
        new Thread(() =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    api.TryHandle(_listener.GetContext());
                }
                catch
                {
                    break;
                }
            }
        }) { IsBackground = true }.Start();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "t"), Token);
        _hub = new FleetHub(new HubConfig { Agents = [new HubAgent { Name = "A", Url = $"http://localhost:{port}", TokenFile = Path.Combine(_dir, "t") }] });
    }

    [Fact]
    public async Task TheHubReadsAServersWholeSettings()
    {
        var (status, body) = await _hub.ForwardAsync("A", HttpMethod.Get, "settings");
        Assert.Equal(200, status);
        Assert.Contains("\"RgbR\":1", body);
    }

    [Fact]
    public async Task APagePatchReachesTheServerThroughItsClamps()
    {
        var (status, body) = await _hub.ForwardAsync("A", HttpMethod.Patch, "settings", """{"pumpDuty":10,"rgbR":200}""");
        Assert.Equal(200, status);
        Assert.Contains("rgbR=200", body);
        Assert.Equal(200, _agent.Settings.RgbR);
        Assert.Equal(DeviceMaster.Core.Safety.SafetyLimits.PumpMinimumDutyPercent, _agent.Settings.PumpDutyPercent);
    }

    [Fact]
    public async Task AnUnknownServerOrADeadOneIsReported()
    {
        Assert.Equal(404, (await _hub.ForwardAsync("nope", HttpMethod.Get, "settings")).Status);
        using var dead = new FleetHub(new HubConfig { Agents = [new HubAgent { Name = "B", Url = "http://localhost:1", TokenFile = Path.Combine(_dir, "t") }] });
        Assert.Equal(502, (await dead.ForwardAsync("B", HttpMethod.Get, "settings")).Status);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _listener.Stop();
        _listener.Close();
        Directory.Delete(_dir, recursive: true);
    }
}
