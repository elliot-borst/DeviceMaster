using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceMaster.Control;

/// <summary>What the agent API needs from the app it runs in (the Windows window or the headless loop).</summary>
public interface IAgentHost
{
    /// <summary>The loop's latest status (capabilities included).</summary>
    ControlStatus Status { get; }

    /// <summary>The fleet-shared slice of the settings this machine runs now.</summary>
    FleetProfile ReadProfile();

    /// <summary>Applies and persists a profile through the local clamps; true when anything changed.</summary>
    bool ApplyProfile(FleetProfile profile);
}

/// <summary>
/// The fleet agent API (#33 phase 2), identical on Windows and Linux. Every route needs
/// <c>Authorization: Bearer &lt;agent token&gt;</c>; a machine without a token serves no API at all.
/// Control never depends on it: the API only reads status and hands settings to the same
/// clamped path the local editors use.
/// <list type="bullet">
/// <item><c>GET /api/v1/info</c> — name, platform, build, capabilities</item>
/// <item><c>GET /api/v1/status</c> — the loop status</item>
/// <item><c>GET /api/v1/profile</c> — the profile in force, with its revision</item>
/// <item><c>PUT /api/v1/profile</c> — apply a whole profile; answers with the profile now in force</item>
/// </list>
/// </summary>
public sealed class AgentApi
{
    public const string Prefix = "/api/v1/";
    public const int ApiVersion = 1;
    private const int MaxBodyBytes = 64 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAgentHost _host;
    private readonly byte[] _token;
    private readonly Action<string>? _log;

    public AgentApi(IAgentHost host, string token, Action<string>? log = null)
    {
        _host = host;
        _token = Encoding.UTF8.GetBytes(token);
        _log = log;
    }

    /// <summary>A short, stable fingerprint of a profile: the hub compares it to decide whether to push.</summary>
    public static string Revision(FleetProfile profile) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, Json))))[..12].ToLowerInvariant();

    /// <summary>Handles the request when it is an API route (true); leaves anything else to the caller.</summary>
    public bool TryHandle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (!path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (!Authorized(ctx.Request))
            {
                Respond(ctx, 401, new { error = "unauthorized" });
                return true;
            }

            var route = path[Prefix.Length..];
            switch ((ctx.Request.HttpMethod, route))
            {
                case ("GET", "info"):
                    var status = _host.Status;
                    Respond(ctx, 200, new
                    {
                        apiVersion = ApiVersion,
                        name = status.Capabilities.MachineName,
                        platform = status.Capabilities.Platform,
                        version = status.Version,
                        commit = status.Commit,
                        capabilities = status.Capabilities,
                    });
                    break;
                case ("GET", "status"):
                    Respond(ctx, 200, _host.Status);
                    break;
                case ("GET", "profile"):
                    var profile = _host.ReadProfile();
                    Respond(ctx, 200, new { revision = Revision(profile), profile });
                    break;
                case ("PUT", "profile"):
                    HandlePut(ctx);
                    break;
                default:
                    Respond(ctx, 404, new { error = "not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"agent api: {ctx.Request.HttpMethod} {path} failed: {ex.Message}");
            try
            {
                Respond(ctx, 500, new { error = "internal error" });
            }
            catch
            {
                // connection already gone
            }
        }

        return true;
    }

    private void HandlePut(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBodyBytes)
        {
            Respond(ctx, 413, new { error = "payload too large" });
            return;
        }

        FleetProfile? incoming;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = reader.ReadToEnd();
            if (body.Length > MaxBodyBytes)
            {
                Respond(ctx, 413, new { error = "payload too large" });
                return;
            }

            incoming = JsonSerializer.Deserialize<FleetProfile>(body, Json);
        }
        catch (JsonException ex)
        {
            Respond(ctx, 400, new { error = $"invalid profile: {ex.Message}" });
            return;
        }

        if (incoming is null)
        {
            Respond(ctx, 400, new { error = "empty profile" });
            return;
        }

        var changed = _host.ApplyProfile(incoming);
        var now = _host.ReadProfile(); // what actually runs, after the local clamps
        if (changed)
        {
            _log?.Invoke($"agent api: fleet profile applied (revision {Revision(now)})");
        }

        Respond(ctx, 200, new { changed, revision = Revision(now), profile = now });
    }

    private bool Authorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        const string scheme = "Bearer ";
        if (_token.Length == 0 || header is null || !header.StartsWith(scheme, StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[scheme.Length..].Trim()), _token);
    }

    private static void Respond(HttpListenerContext ctx, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.AddHeader("Cache-Control", "no-store");
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }
}

/// <summary>
/// The agent token: a random secret in a file beside the config, created on first start with
/// owner-only permissions. The hub holds a copy per machine. Never logged, never in the config.
/// </summary>
public static class AgentToken
{
    public static string LoadOrCreate(string path, Action<string>? log = null)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32)
            {
                return existing;
            }
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, token + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 600
        }

        log?.Invoke($"agent api: created a new agent token at {path} (give it to the fleet hub)");
        return token;
    }
}
