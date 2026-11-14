using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeviceMaster.Control;

namespace DeviceMaster.App.Headless;

/// <summary>
/// Tiny dependency-free dashboard for the control loop (System.Net.HttpListener only):
/// serves a single auto-refreshing HTML page and the live status JSON. Plain HTTP by
/// design — reached over the tailnet (encrypted end-to-end) or the trusted home LAN.
/// </summary>
public sealed class HeadlessWebServer : IDisposable
{
    private readonly HeadlessLoop _loop;
    private readonly int _port;
    private readonly HttpListener _listener;
    private readonly Thread _thread;
    private readonly Action<string> _log;
    private readonly string _configPath;
    private readonly object _configLock = new();
    private readonly AgentApi _agentApi;

    /// <summary>The fleet agent API (#33) on top of this machine's config file.</summary>
    private sealed class Host(HeadlessWebServer server) : IAgentHost
    {
        public ControlStatus Status => server._loop.Status;

        public FleetProfile ReadProfile() => FleetProfile.From(HeadlessConfig.Load(server._configPath).Control);

        public string? RequestShutdown(string reason) =>
            HeadlessConfig.Load(server._configPath).ShutdownRequestDir is { Length: > 0 } dir && ShutdownRequest.Write(dir, reason)
                ? "host shutdown helper"
                : null;

        public string? RequestReboot(string reason) =>
            HeadlessConfig.Load(server._configPath).ShutdownRequestDir is { Length: > 0 } dir && ShutdownRequest.Write(dir, reason, reboot: true)
                ? "host reboot helper"
                : null;

        public bool CancelShutdown() =>
            HeadlessConfig.Load(server._configPath).ShutdownRequestDir is { Length: > 0 } dir && ShutdownRequest.Cancel(dir);

        public string? ReadSettingsJson() =>
            System.Text.Json.JsonSerializer.Serialize(HeadlessConfig.Load(server._configPath).Control, AgentApi.Json);

        public IReadOnlyList<string>? PatchSettings(string patchJson, out bool invalid)
        {
            lock (server._configLock)
            {
                var cfg = HeadlessConfig.Load(server._configPath);
                var applied = ConfigPatcher.ApplyPatch(cfg.Control, patchJson, out invalid);
                if (applied.Count > 0)
                {
                    var tmp = server._configPath + ".tmp";
                    File.WriteAllText(tmp, cfg.Save());
                    File.Move(tmp, server._configPath, overwrite: true); // the loop hot-reloads it
                }

                return applied;
            }
        }

        public bool ApplyProfile(FleetProfile profile)
        {
            lock (server._configLock)
            {
                var cfg = HeadlessConfig.Load(server._configPath);
                if (!profile.ApplyTo(cfg.Control))
                {
                    return false;
                }

                // atomic write; the loop hot-reloads the file on its next tick
                var tmp = server._configPath + ".tmp";
                File.WriteAllText(tmp, cfg.Save());
                File.Move(tmp, server._configPath, overwrite: true);
                return true;
            }
        }
    }

    /// <param name="binds">Addresses to listen on; empty = every interface.</param>
    public HeadlessWebServer(HeadlessLoop loop, int port, Action<string> log, string configPath, IReadOnlyList<string>? binds = null)
    {
        _loop = loop;
        _port = port;
        _log = log;
        _configPath = configPath;
        var tokenPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "agent-token");
        _agentApi = new AgentApi(new Host(this), AgentToken.LoadOrCreate(tokenPath, log), log);
        _listener = new HttpListener();
        if (binds is { Count: > 0 })
        {
            foreach (var host in binds)
            {
                _listener.Prefixes.Add($"http://{host}:{port}/");
            }
        }
        else
        {
            _listener.Prefixes.Add($"http://+:{port}/");
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "web" };
    }

    public void Start()
    {
        _listener.Start();
        _thread.Start();
        _log($"web dashboard on :{_port} (page /, data /status.json, fleet agent API {AgentApi.Prefix})");
    }

    private void Run()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.GetContext();
            }
            catch
            {
                break; // listener closed
            }

            try
            {
                Handle(ctx);
            }
            catch
            {
                // keep serving
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        if (_agentApi.TryHandle(ctx))
        {
            return; // /api/v1/… — token-authenticated fleet agent API
        }

        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        var handled = false;
        switch ((method, path))
        {
            case ("GET", "/") or ("GET", "/index.html"):
                ctx.Response.StatusCode = 200;
                ctx.Response.AddHeader("Cache-Control", "no-store");
                Write(ctx, "text/html; charset=utf-8", DeviceMaster.Core.Lighting.ColorPalette.Inject(DashboardHtml));
                handled = true;
                break;
            case ("GET", "/status.json"):
                ctx.Response.StatusCode = 200;
                var json = JsonSerializer.Serialize(_loop.Status, new JsonSerializerOptions { WriteIndented = false });
                Write(ctx, "application/json; charset=utf-8", json);
                handled = true;
                break;
            case ("GET", "/config.json"):
                ctx.Response.StatusCode = 200;
                Write(ctx, "application/json; charset=utf-8", File.ReadAllText(_configPath));
                handled = true;
                break;
            case ("POST", "/config.json"):
                HandleConfigPost(ctx);
                handled = true;
                break;
        }

        if (!handled)
        {
            ctx.Response.StatusCode = 404;
            Write(ctx, "text/plain; charset=utf-8", "not found");
        }
    }

    /// <summary>
    /// Applies a dashboard patch to the config file (whitelisted fields only, clamped).
    /// The loop hot-reloads the file on its next tick — the change takes effect within ~1 s.
    /// </summary>
    private void HandleConfigPost(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }

        if (body.Length > 64 * 1024)
        {
            ctx.Response.StatusCode = 413;
            Write(ctx, "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"payload too large\"}");
            return;
        }

        lock (_configLock)
        {
            var cfg = HeadlessConfig.Load(_configPath);
            if (cfg is null)
            {
                ctx.Response.StatusCode = 500;
                Write(ctx, "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"config load failed\"}");
                return;
            }

            var applied = ConfigPatcher.ApplyPatch(cfg.Control, body, out var invalid);
            if (invalid)
            {
                ctx.Response.StatusCode = 400;
                Write(ctx, "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"invalid json\"}");
                return;
            }

            if (applied.Count == 0)
            {
                Write(ctx, "application/json; charset=utf-8", "{\"ok\":true,\"applied\":[]}");
                return;
            }

            // atomic write — a half-written config must never be what the loop reloads
            var tmp = _configPath + ".tmp";
            File.WriteAllText(tmp, cfg.Save());
            File.Move(tmp, _configPath, overwrite: true);
            _log("web: config updated: " + string.Join(", ", applied));

            var payload = "{\"ok\":true,\"applied\":["
                + string.Join(",", applied.Select(a => "\"" + a + "\""))
                + "]}";
            Write(ctx, "application/json; charset=utf-8", payload);
        }
    }

    private static void Write(HttpListenerContext ctx, string contentType, string body)
    {
        ctx.Response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // shutting down
        }
    }

    /// <summary>Single-page dashboard (side-menu layout mirroring the Windows UI): live status + config controls.</summary>
    /// <remarks>Also served by the fleet hub for each server at /m/{name}/: every request is relative.</remarks>
    public const string DashboardHtml = DeviceMaster.Hub.AgentDashboardPage.Html;
}