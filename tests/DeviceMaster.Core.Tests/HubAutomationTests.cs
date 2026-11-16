using System.Net;
using System.Text;
using System.Text.Json;
using DeviceMaster.Control;
using DeviceMaster.Hub;

namespace DeviceMaster.Core.Tests;

/// <summary>Automation tokens on the fleet hub (#83): an agent or scheduled job may change settings, audited by name.</summary>
public sealed class HubAutomationTests : IDisposable
{
    private const string Token = "automation-token-0123456789abcdef0123";
    private readonly string _dir = Directory.CreateTempSubdirectory("dm-hub-auto-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void ResolvesOnlyAMatchingBearerToken()
    {
        var automations = new HubAutomations();
        automations.Add("claudecode", Token);

        Assert.Equal("claudecode (automation)", automations.Resolve($"Bearer {Token}"));
        Assert.Null(automations.Resolve($"Bearer {Token}x"));
        Assert.Null(automations.Resolve(Token)); // not a bearer header
        Assert.Null(automations.Resolve(null));
        Assert.Null(new HubAutomations().Resolve($"Bearer {Token}"));
    }

    [Fact]
    public void LoadSkipsMissingShortOrUnnamedTokens()
    {
        var good = Path.Combine(_dir, "good");
        var shortToken = Path.Combine(_dir, "short");
        File.WriteAllText(good, Token + "\n");
        File.WriteAllText(shortToken, "tooshort");
        var problems = new List<string>();

        var automations = HubAutomations.Load(
        [
            new HubAutomation { Name = "claudecode", TokenFile = good },
            new HubAutomation { Name = "short", TokenFile = shortToken },
            new HubAutomation { Name = "missing", TokenFile = Path.Combine(_dir, "nope") },
            new HubAutomation { Name = " ", TokenFile = good },
        ], problems.Add);

        Assert.Equal(["claudecode (automation)"], automations.Names);
        Assert.Equal(3, problems.Count);
        Assert.DoesNotContain(problems, p => p.Contains(Token)); // never log a token
        Assert.Equal("claudecode (automation)", automations.Resolve($"Bearer {Token}")); // trailing newline trimmed
    }

    [Fact]
    public async Task HubAcceptsTheTokenForWritesAndAuditsItsName()
    {
        var store = new FleetStore(Path.Combine(_dir, "fleet-state.json"));
        using var hub = new FleetHub(new HubConfig(), _ => { }, store: store);
        var automations = new HubAutomations();
        automations.Add("claudecode", Token);
        var port = FreePort();
        using var web = new HubWebServer(hub, port, _ => { }, ["127.0.0.1"], automations: automations);
        web.Start();
        using var client = new HttpClient();
        var url = $"http://127.0.0.1:{port}/api/fleet/settings/lighting";
        var body = """{"RgbEnabled":true,"RgbOff":false,"RgbEffect":"Static","RgbR":150,"RgbG":0,"RgbB":255,"RgbBrightness":100,"Blackout":false}""";

        // no user, no token: refused
        using (var anonymous = await client.PutAsync(url, new StringContent(body, Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
        }

        // a wrong token: refused
        using (var wrong = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") })
        {
            wrong.Headers.Authorization = new("Bearer", "not-the-token-0123456789abcdef0123456");
            using var response = await client.SendAsync(wrong);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", Token);
        using (var ok = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        Assert.False(store.State.Profile.RgbOff);
        Assert.Equal(150, store.State.Profile.RgbR);
        Assert.Contains(FleetSection.Lighting, store.State.Managed);
        var audit = JsonSerializer.Serialize(store.RecentAudit(5));
        Assert.Contains("claudecode (automation)", audit);
        Assert.DoesNotContain(Token, audit);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
