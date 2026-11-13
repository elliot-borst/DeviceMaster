using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeviceMaster.Control;
using DeviceMaster.Core.Lighting;
using DeviceMaster.Core.Safety;

namespace DeviceMaster.Core.Tests;

public sealed class AgentApiTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private sealed class FakeHost : IAgentHost
    {
        public ControlSettings Settings { get; } = new();

        public ControlStatus Status { get; } = new() { Running = true, Capabilities = new MachineCapabilities { MachineName = "test-box", Pump = true } };

        public FleetProfile ReadProfile() => FleetProfile.From(Settings);

        public bool ApplyProfile(FleetProfile profile) => profile.ApplyTo(Settings);
    }

    private readonly FakeHost _host = new();
    private readonly HttpListener _listener = new();
    private readonly HttpClient _client = new();
    private readonly string _base;

    public AgentApiTests()
    {
        var port = FreePort();
        _base = $"http://localhost:{port}";
        _listener.Prefixes.Add(_base + "/");
        _listener.Start();
        var api = new AgentApi(_host, Token);
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
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private HttpRequestMessage Request(HttpMethod method, string route, string? token = Token, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"{_base}{AgentApi.Prefix}{route}");
        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, AgentApi.Json), Encoding.UTF8, "application/json");
        }

        return request;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token-wrong-token-wrong-token")]
    public async Task EveryRouteNeedsTheToken(string? token)
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "status", token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InfoDescribesTheMachine()
    {
        var info = await (await _client.SendAsync(Request(HttpMethod.Get, "info"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AgentApi.ApiVersion, info.GetProperty("apiVersion").GetInt32());
        Assert.Equal("test-box", info.GetProperty("name").GetString());
        Assert.True(info.GetProperty("capabilities").GetProperty("Pump").GetBoolean());
    }

    [Fact]
    public async Task PutAppliesThroughTheLocalClamps()
    {
        var profile = FleetProfile.From(_host.Settings) with { PumpDutyPercent = 10, RgbEffect = RgbEffect.Rainbow };
        var response = await _client.SendAsync(Request(HttpMethod.Put, "profile", body: profile));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var answer = await response.Content.ReadFromJsonAsync<JsonElement>(AgentApi.Json);
        Assert.True(answer.GetProperty("changed").GetBoolean());
        Assert.Equal(SafetyLimits.PumpMinimumDutyPercent, _host.Settings.PumpDutyPercent); // never below the floor
        Assert.Equal(RgbEffect.Rainbow, _host.Settings.RgbEffect);

        // the revision the agent reports is the one for what now runs
        var now = await (await _client.SendAsync(Request(HttpMethod.Get, "profile"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(answer.GetProperty("revision").GetString(), now.GetProperty("revision").GetString());
        Assert.Equal(AgentApi.Revision(FleetProfile.From(_host.Settings)), now.GetProperty("revision").GetString());
    }

    [Fact]
    public async Task GarbageIsRejectedAndChangesNothing()
    {
        var before = FleetProfile.From(_host.Settings);
        var request = Request(HttpMethod.Put, "profile");
        request.Content = new StringContent("{not json", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);
        Assert.Equal(before, FleetProfile.From(_host.Settings));
    }

    [Fact]
    public void ATokenIsCreatedOnceAndKept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dm-token-{Guid.NewGuid():N}", "agent-token");
        try
        {
            var first = AgentToken.LoadOrCreate(path);
            Assert.Equal(64, first.Length);
            Assert.Equal(first, AgentToken.LoadOrCreate(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.124.104.92", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.1.10", false)]
    public void OnlyTailscaleAddressesCountAsTailnet(string ip, bool expected) =>
        Assert.Equal(expected, AgentServer.IsTailnet(IPAddress.Parse(ip)));

    public void Dispose()
    {
        _client.Dispose();
        _listener.Stop();
        _listener.Close();
    }
}
