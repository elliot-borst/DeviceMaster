using System.Net;
using System.Net.Sockets;
using DeviceMaster.Control;

namespace DeviceMaster.Core.Tests;

public sealed class FleetHubTests : IDisposable
{
    private const string Token = "fedcba9876543210fedcba9876543210";

    private sealed class FakeHost : IAgentHost
    {
        public ControlStatus Status { get; } = new()
        {
            Running = true,
            Mode = ControlMode.Curve,
            SourceName = "Hottest",
            CpuTemperatureC = 52,
            GpuTemperatureC = 61,
            TargetDutyPercent = 47,
            Devices = [new DeviceReading("motherboard", "CPU Fan", 1200, 47, IsPump: false)],
            Warnings = ["pwm3 handed back"],
            Capabilities = new MachineCapabilities { MachineName = "box-a", MotherboardFans = true },
        };

        public FleetProfile ReadProfile() => new();

        public bool ApplyProfile(FleetProfile profile) => false;
    }

    private readonly HttpListener _listener = new();
    private readonly string _url;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dm-hub-{Guid.NewGuid():N}");

    public FleetHubTests()
    {
        var port = FreePort();
        _url = $"http://localhost:{port}";
        _listener.Prefixes.Add(_url + "/");
        _listener.Start();
        var api = new AgentApi(new FakeHost(), Token);
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
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private string TokenFile(string name, string token)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, token + "\n");
        return path;
    }

    [Fact]
    public async Task CollectsEachAgentsStatus()
    {
        using var hub = new FleetHub(new HubConfig
        {
            Agents = [new HubAgent { Name = "Server A", Url = _url, TokenFile = TokenFile("a", Token) }],
        });

        await hub.PollOnce();

        var m = Assert.Single(hub.Machines);
        Assert.True(m.Online);
        Assert.Null(m.Error);
        var s = m.Summarize(DateTime.UtcNow);
        Assert.Equal("box-a", s.MachineName);
        Assert.Equal(61, s.GpuTemperatureC);
        Assert.Equal(47, s.FanDutyPercent);
        Assert.Equal(1, s.WarningCount);
        Assert.Equal(1, s.FanCount);
        Assert.True(s.Capabilities!.MotherboardFans);
        Assert.Equal("CPU Fan", m.Status!.Devices.Single().Name);
    }

    [Fact]
    public async Task AWrongTokenOrADeadAgentIsOfflineWithTheReason()
    {
        using var hub = new FleetHub(new HubConfig
        {
            Agents =
            [
                new HubAgent { Name = "Wrong token", Url = _url, TokenFile = TokenFile("w", "nope-nope-nope-nope-nope-nope-nope") },
                new HubAgent { Name = "Gone", Url = $"http://localhost:{FreePort()}", TokenFile = TokenFile("g", Token) },
                new HubAgent { Name = "No token file", Url = _url, TokenFile = Path.Combine(_dir, "missing") },
            ],
        });

        await hub.PollOnce();

        Assert.All(hub.Machines, m => Assert.False(m.Online));
        Assert.Contains("401", hub.Machine("Wrong token")!.Error);
        Assert.NotNull(hub.Machine("Gone")!.Error);
        Assert.NotNull(hub.Machine("No token file")!.Error);
    }

    [Fact]
    public async Task AMachineThatStopsAnsweringKeepsItsLastStatus()
    {
        var tokenFile = TokenFile("a", Token);
        using var hub = new FleetHub(new HubConfig { Agents = [new HubAgent { Name = "A", Url = _url, TokenFile = tokenFile }] });
        await hub.PollOnce();
        Assert.True(hub.Machine("A")!.Online);

        File.WriteAllText(tokenFile, "now-wrong-now-wrong-now-wrong-now"); // next poll fails
        await hub.PollOnce();

        var m = hub.Machine("A")!;
        Assert.True(m.Online); // a single miss is within the offline grace
        Assert.NotNull(m.Error);
        Assert.Equal(52, m.Status!.CpuTemperatureC); // last known status kept for the overview
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        Directory.Delete(_dir, recursive: true);
    }
}
