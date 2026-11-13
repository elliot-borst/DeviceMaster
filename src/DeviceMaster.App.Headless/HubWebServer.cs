using System.Net;
using System.Text;
using System.Text.Json;
using DeviceMaster.Control;

namespace DeviceMaster.App.Headless;

/// <summary>
/// The fleet hub's web front (#33 phase 3): the Servers overview at one address, plus the JSON the
/// Windows app's Servers page reads. Binds localhost by default — <c>tailscale serve</c> on the
/// hub's sidecar puts it at <c>https://devicemaster.&lt;tailnet&gt;.ts.net/</c>. Read-only in phase 3.
/// <list type="bullet">
/// <item><c>GET /api/fleet/machines</c> — one summary per machine (the Servers list)</item>
/// <item><c>GET /api/fleet/machines/{name}</c> — that machine's summary and full status</item>
/// </list>
/// </summary>
public sealed class HubWebServer : IDisposable
{
    private readonly FleetHub _hub;
    private readonly HttpListener _listener = new();
    private readonly Action<string> _log;
    private readonly Thread _thread;

    /// <summary>Only connections from these addresses are served (null = anyone who can reach the port).</summary>
    private readonly HashSet<IPAddress>? _allowedClients;

    public HubWebServer(FleetHub hub, int port, Action<string> log, IReadOnlyList<string> binds)
    {
        _hub = hub;
        _log = log;
        var hosts = binds.Count > 0 ? binds : ["127.0.0.1"];
        if (OperatingSystem.IsWindows())
        {
            foreach (var host in hosts)
            {
                _listener.Prefixes.Add($"http://{host}:{port}/");
            }
        }
        else
        {
            // A host-specific prefix makes HttpListener 404 every request whose Host header differs
            // — and tailscale serve forwards the public name (devicemaster.<tailnet>.ts.net). So
            // accept any Host, and enforce --bind by the client address instead: with the default
            // 127.0.0.1 only serve (same namespace) is answered, never the tailnet or LAN directly.
            _listener.Prefixes.Add($"http://+:{port}/");
            _allowedClients = hosts.Any(h => h is "+" or "*" or "0.0.0.0")
                ? null
                : hosts.Select(h => h == "localhost" ? IPAddress.Loopback : IPAddress.Parse(h)).ToHashSet();
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "hub web" };
    }

    /// <summary>Whether a client may be served under the --bind rule (loopback binds admit any loopback client).</summary>
    public static bool ClientAllowed(IPAddress client, IReadOnlySet<IPAddress>? allowed) =>
        allowed is null
        || allowed.Contains(client)
        || (IPAddress.IsLoopback(client) && allowed.Any(IPAddress.IsLoopback));

    public void Start()
    {
        _listener.Start();
        _thread.Start();
        _log($"fleet hub web on {string.Join(", ", _listener.Prefixes)} (page /, data /api/fleet/machines)");
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
                break;
            }

            try
            {
                Handle(ctx);
            }
            catch (Exception ex)
            {
                _log($"hub web: {ctx.Request.Url?.AbsolutePath} failed: {ex.Message}");
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                }
                catch
                {
                    // client gone
                }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (!ClientAllowed(ctx.Request.RemoteEndPoint.Address, _allowedClients))
        {
            Send(ctx, 403, "text/plain; charset=utf-8", "forbidden");
            return;
        }

        if (path.StartsWith("/m/", StringComparison.Ordinal))
        {
            HandleMachinePage(ctx, path);
            return;
        }

        if (ctx.Request.HttpMethod == "PUT")
        {
            HandleWrite(ctx, path);
            return;
        }

        if (ctx.Request.HttpMethod == "POST" && path.StartsWith("/api/fleet/machines/", StringComparison.Ordinal) && path.EndsWith("/power", StringComparison.Ordinal))
        {
            HandlePower(ctx, Uri.UnescapeDataString(path["/api/fleet/machines/".Length..^"/power".Length]));
            return;
        }

        if (ctx.Request.HttpMethod != "GET")
        {
            Send(ctx, 405, "text/plain; charset=utf-8", "GET or PUT");
            return;
        }

        if (path == "/api/fleet/settings")
        {
            var state = _hub.Store?.State;
            Json(ctx, 200, new
            {
                writable = _hub.Store is not null,
                user = User(ctx),
                state,
                audit = _hub.Store?.RecentAudit(20) ?? [],
            });
            return;
        }

        var now = DateTime.UtcNow;
        if (path is "/" or "/index.html")
        {
            Send(ctx, 200, "text/html; charset=utf-8", Page);
        }
        else if (path == "/api/fleet/machines")
        {
            Json(ctx, 200, _hub.Machines.Select(m => m.Summarize(now)));
        }
        else if (path is "/display" or "/display/")
        {
            Send(ctx, 200, "text/html; charset=utf-8", HubDisplayPage.Html);
        }
        else if (path == "/api/fleet/home")
        {
            Json(ctx, 200, new { machines = _hub.Machines.Select(m => m.Summarize(now) with { CanWake = _hub.CanWake(m.Name) }), sabnzbd = _hub.Sabnzbd, at = now });
        }
        else if (path.StartsWith("/api/fleet/machines/", StringComparison.Ordinal)
                 && _hub.Machine(Uri.UnescapeDataString(path["/api/fleet/machines/".Length..])) is { } machine)
        {
            Json(ctx, 200, new { summary = machine.Summarize(now), status = machine.Status });
        }
        else
        {
            Send(ctx, 404, "text/plain; charset=utf-8", "not found");
        }
    }

    /// <summary>
    /// Phase 5: the full DeviceMaster page for one server at <c>/m/{name}/</c> — the same page each
    /// Linux agent serves itself, with its two data calls routed to that server's agent API:
    /// <c>status.json</c> ← <c>GET /api/v1/status</c>, <c>config.json</c> ← <c>GET/PATCH /api/v1/settings</c>.
    /// Changes need a signed-in tailnet user and are written to the fleet audit log.
    /// </summary>
    private void HandleMachinePage(HttpListenerContext ctx, string path)
    {
        var parts = path.Split('/', 4); // "", "m", name, rest
        var name = parts.Length > 2 ? Uri.UnescapeDataString(parts[2]) : "";
        if (!_hub.Knows(name))
        {
            Send(ctx, 404, "text/plain; charset=utf-8", "unknown server");
            return;
        }

        var rest = parts.Length > 3 ? parts[3] : null;
        var method = ctx.Request.HttpMethod;
        if (rest is null)
        {
            ctx.Response.Redirect($"/m/{parts[2]}/"); // the page's relative requests need the slash
            ctx.Response.Close();
            return;
        }

        switch ((method, rest))
        {
            case ("GET", "" or "index.html"):
                Send(ctx, 200, "text/html; charset=utf-8", HeadlessWebServer.DashboardHtml);
                return;

            case ("GET", "status.json"):
            {
                var (status, body) = _hub.ForwardAsync(name, HttpMethod.Get, "status").GetAwaiter().GetResult();
                if (status != 200)
                {
                    Send(ctx, status, "application/json; charset=utf-8", body);
                    return;
                }

                // the page reads enums as numbers (as the agents' own /status.json writes them)
                var parsed = JsonSerializer.Deserialize<ControlStatus>(body, AgentApi.Json);
                Send(ctx, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(parsed));
                return;
            }

            case ("GET", "config.json"):
            {
                var (status, body) = _hub.ForwardAsync(name, HttpMethod.Get, "settings").GetAwaiter().GetResult();
                Send(ctx, status, "application/json; charset=utf-8", status == 200 ? $"{{\"Control\":{body}}}" : body);
                return;
            }

            case ("POST", "config.json"):
            {
                if (User(ctx) is not { } user)
                {
                    Json(ctx, 403, new { ok = false, error = "changes need a signed-in tailnet user (open the page through its tailnet address)" });
                    return;
                }

                string patch;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    patch = reader.ReadToEnd();
                }

                var (status, body) = _hub.ForwardAsync(name, HttpMethod.Patch, "settings", patch).GetAwaiter().GetResult();
                if (status == 200 && _hub.Store is { } store)
                {
                    using var doc = JsonDocument.Parse(body);
                    var applied = doc.RootElement.TryGetProperty("applied", out var a) ? a.EnumerateArray().Select(x => x.GetString()).ToList() : [];
                    if (applied.Count > 0)
                    {
                        store.Audit(user, $"{name}: {string.Join(", ", applied)}");
                    }
                }

                Send(ctx, status, "application/json; charset=utf-8", body);
                return;
            }

            default:
                Send(ctx, 404, "text/plain; charset=utf-8", "not found");
                return;
        }
    }

    /// <summary>
    /// Power on/off/cancel for one server (#49): <c>POST /api/fleet/machines/{name}/power</c> with
    /// <c>{"action":"on|off|cancel"}</c>. Signed-in tailnet users only; always audited.
    /// </summary>
    private void HandlePower(HttpListenerContext ctx, string name)
    {
        if (User(ctx) is not { } user)
        {
            Json(ctx, 403, new { error = "power needs a signed-in tailnet user (open the page through its tailnet address)" });
            return;
        }

        if (!_hub.Knows(name))
        {
            Json(ctx, 404, new { error = "unknown server" });
            return;
        }

        string action;
        try
        {
            using var doc = JsonDocument.Parse(new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEnd());
            action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            Json(ctx, 400, new { error = "invalid json" });
            return;
        }

        var (status, body) = _hub.PowerAsync(name, action, user).GetAwaiter().GetResult();
        _hub.Store?.Audit(user, $"{name}: power {action} → {(status is >= 200 and < 300 ? "sent" : $"failed ({status})")}");
        _log($"hub: {user} asked {name} to power {action} ({status})");
        Send(ctx, status, "application/json; charset=utf-8", body);
    }

    /// <summary>The signed-in tailnet user, from the header tailscale serve adds (null when absent).</summary>
    private static string? User(HttpListenerContext ctx) =>
        ctx.Request.Headers["Tailscale-User-Login"] is { Length: > 0 } login ? login : null;

    /// <summary>
    /// Fleet settings writes (phase 4). Allowed only for a request that tailscale serve marked with
    /// a tailnet user identity (tagged devices and anything else get 403); every change is audited.
    /// <list type="bullet">
    /// <item><c>PUT /api/fleet/settings/lighting</c> — body: the lighting fields plus <c>managed</c> (bool)</item>
    /// <item><c>PUT /api/fleet/machines/{name}/lighting</c> — body: lighting fields for that machine, or <c>{"follow":true}</c></item>
    /// </list>
    /// </summary>
    private void HandleWrite(HttpListenerContext ctx, string path)
    {
        if (_hub.Store is not { } store)
        {
            Json(ctx, 409, new { error = "this hub is read-only (no fleet state file)" });
            return;
        }

        if (User(ctx) is not { } user)
        {
            Json(ctx, 403, new { error = "changes need a signed-in tailnet user (open the page through its tailnet address)" });
            return;
        }

        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }

        if (body.Length > 32 * 1024)
        {
            Json(ctx, 413, new { error = "payload too large" });
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            FleetState next;
            var lastSlash = path.LastIndexOf('/');
            if (!TryParseSection(path[(lastSlash + 1)..], out var section))
            {
                Json(ctx, 404, new { error = "not found" });
                return;
            }

            var incoming = root.Deserialize<FleetProfile>(AgentApi.Json) ?? new FleetProfile();
            if (section == FleetSection.Cooling && !root.TryGetProperty("follow", out _))
            {
                // every machine reads its own sensors: only the hottest-of-CPU/GPU source is safe fleet-wide
                if (FleetOverrides.SanitizeCooling(incoming) is not { } safe)
                {
                    Json(ctx, 400, new { error = "the curve needs two usable points" });
                    return;
                }

                incoming = safe;
            }

            var label = section.ToString().ToLowerInvariant();
            if (path == $"/api/fleet/settings/{label}")
            {
                var managed = !root.TryGetProperty("managed", out var m) || m.ValueKind != JsonValueKind.False;
                next = store.Update(s => s with
                {
                    Profile = FleetState.Take(section, s.Profile, incoming),
                    Managed = managed ? s.Managed.Union([section]).ToList() : s.Managed.Where(x => x != section).ToList(),
                }, user, $"fleet {label}: {Describe(section, incoming)}{(managed ? "" : $" (fleet stops managing {label})")}");
            }
            else if (path.StartsWith("/api/fleet/machines/", StringComparison.Ordinal)
                     && Uri.UnescapeDataString(path["/api/fleet/machines/".Length..lastSlash]) is var name && _hub.Knows(name))
            {
                var follow = root.TryGetProperty("follow", out var f) && f.ValueKind == JsonValueKind.True;
                next = store.Update(s =>
                {
                    var overrides = new Dictionary<string, FleetOverride>(s.Overrides);
                    var current = overrides.GetValueOrDefault(name) ?? new FleetOverride();
                    overrides[name] = follow ? FleetOverrides.Clear(current, section) : FleetOverrides.Set(current, section, incoming);
                    return s with { Overrides = overrides };
                }, user, follow ? $"{name}: {label} follows the fleet" : $"{name}: own {label}: {Describe(section, incoming)}");
            }
            else
            {
                Json(ctx, 404, new { error = "not found" });
                return;
            }

            _log($"hub: {user} changed fleet settings (version {next.Version})");
            _hub.Nudge(); // push now, not at the next poll
            Json(ctx, 200, new { ok = true, version = next.Version });
        }
        catch (JsonException ex)
        {
            Json(ctx, 400, new { error = $"invalid settings: {ex.Message}" });
        }
    }

    private static bool TryParseSection(string text, out FleetSection section) =>
        Enum.TryParse(text, ignoreCase: true, out section) && Enum.IsDefined(section);

    private static string Describe(FleetSection section, FleetProfile p) => section switch
    {
        FleetSection.Cooling => p.Mode == ControlMode.Manual
            ? $"fixed {p.ManualDutyPercent}%, pump {p.PumpDutyPercent}%"
            : $"curve on CPU/GPU {string.Join(" → ", p.CurvePoints.Select(c => $"{c.TemperatureC:0} °C {c.DutyPercent}%"))}, pump {p.PumpDutyPercent}%",
        FleetSection.Screens => $"fan/pump screens {p.LcdScreens} {p.LcdBrightness}%, Turzx {p.TurzxScreen} {p.TurzxBrightness}%",
        _ => p.Blackout ? "blackout" : !p.RgbEnabled ? "not managed" : p.RgbOff ? "lights out"
            : p.RgbEffect == DeviceMaster.Core.Lighting.RgbEffect.Rainbow ? $"rainbow speed {p.RgbSpeed}, {p.RgbBrightness}%"
            : $"#{p.RgbR:X2}{p.RgbG:X2}{p.RgbB:X2}, {p.RgbBrightness}%",
    };

    private static void Json(HttpListenerContext ctx, int status, object body) =>
        Send(ctx, status, "application/json; charset=utf-8", JsonSerializer.Serialize(body, AgentApi.Json));

    private static void Send(HttpListenerContext ctx, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.AddHeader("Cache-Control", "no-store");
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

    /// <summary>The fleet page: a Servers menu on the left, the chosen machine (or all of them) on the right.</summary>
    private const string Page = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>DeviceMaster Fleet</title>
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'%3E%3Cdefs%3E%3ClinearGradient id='g' x1='0' y1='1' x2='1' y2='0'%3E%3Cstop offset='0' stop-color='%234f8cff'/%3E%3Cstop offset='.5' stop-color='%23d06cd8'/%3E%3Cstop offset='1' stop-color='%23ff7a45'/%3E%3C/linearGradient%3E%3CclipPath id='d'%3E%3Cpath d='M11.5 12H32.50A20.0 20.0 0 0 1 32.50 52H11.5Z'/%3E%3C/clipPath%3E%3C/defs%3E%3Crect x='2' y='2' width='60' height='60' rx='14' fill='url(%23g)'/%3E%3Cg clip-path='url(%23d)' fill='%230f1115'%3E%3Crect x='11.50' y='12' width='8.00' height='40' rx='2.40'/%3E%3Crect x='22.50' y='12' width='8.00' height='40' rx='2.40'/%3E%3Crect x='33.50' y='12' width='8.00' height='40' rx='2.40'/%3E%3Crect x='44.50' y='12' width='8.00' height='40' rx='2.40'/%3E%3C/g%3E%3C/svg%3E">
<style>
:root {
  --bg:#0f1115; --card:#171a20; --card2:#1d2129; --line:#2a2f39; --line2:#3c4351;
  --text:#eceef2; --dim:#a0a6b3; --faint:#6b7180; --accent:#ff7a45; --accent2:#ffb08f;
  --good:#34d399; --warn:#fbbf24; --danger:#ef4444; --inset:#0c0e12;
}
* { box-sizing:border-box; margin:0; }
html,body { height:100%; }
body { background:var(--bg); color:var(--text); font:14px/1.45 "Segoe UI",system-ui,sans-serif; display:flex; }
aside { width:240px; flex:none; background:var(--card); border-right:1px solid var(--line); padding:18px 14px; height:100vh; position:sticky; top:0; overflow:auto; }
.brand { font-size:17px; font-weight:700; padding:0 4px 18px; }
.brand span { display:block; font-size:10.5px; font-weight:400; color:var(--faint); }
.navhead { font-size:10.5px; font-weight:600; text-transform:uppercase; letter-spacing:.06em; color:var(--faint); padding:6px 12px; }
.nav { display:flex; align-items:center; gap:10px; width:100%; background:transparent; border:0; color:var(--dim);
  font:13.5px "Segoe UI",sans-serif; padding:9px 12px; margin:2px 0; border-radius:10px; cursor:pointer; text-align:left; }
.nav:hover { background:var(--card2); }
.nav.active { background:var(--card2); color:var(--text); font-weight:600; }
.nav small { margin-left:auto; color:var(--faint); font-size:11.5px; font-weight:400; }
.dot { width:8px; height:8px; border-radius:50%; background:var(--faint); flex:none; }
.dot.ok { background:var(--good); } .dot.bad { background:var(--danger); } .dot.warn { background:var(--warn); }
main { flex:1; padding:22px 26px; min-width:0; }
h1 { font-size:24px; font-weight:700; margin:0 0 4px 2px; }
.sub { font-size:12.5px; color:var(--faint); margin:0 0 18px 2px; }
.cards { display:grid; grid-template-columns:repeat(auto-fill,minmax(260px,1fr)); gap:14px; }
.card { background:var(--card); border:1px solid var(--line); border-radius:15px; padding:16px 18px; min-width:0; overflow-wrap:anywhere; }
.card.click { cursor:pointer; } .card.click:hover { border-color:var(--line2); }
.row { display:flex; align-items:center; gap:10px; flex-wrap:wrap; }
.cname { font-size:15.5px; font-weight:600; }
.cmeta { font-size:11.5px; color:var(--faint); margin-left:auto; }
.stats { display:grid; grid-template-columns:repeat(3,1fr); gap:8px; margin-top:12px; }
.stat { background:var(--inset); border:1px solid var(--line); border-radius:11px; padding:10px 12px; }
.stat .k { font-size:10.5px; font-weight:600; text-transform:uppercase; color:var(--faint); }
.stat .v { font-size:22px; font-weight:700; line-height:1.2; white-space:nowrap; }
.big .stat .v { font-size:32px; }
.banner { border-radius:11px; padding:10px 14px; margin:12px 0 0; font-weight:600; font-size:13px; }
.banner.bad { background:#3a1214; color:#fca5a5; border:1px solid #5b1d20; }
.banner.off { background:var(--inset); color:var(--dim); border:1px solid var(--line); }
.chips { display:flex; flex-wrap:wrap; gap:6px; margin-top:12px; }
.chip { font-size:11.5px; padding:3px 10px; border-radius:99px; background:var(--card2); border:1px solid var(--line); color:var(--dim); }
ul.warn { margin:12px 0 0 18px; color:var(--warn); font-size:12.5px; }
table { width:100%; border-collapse:collapse; margin-top:12px; font-size:13px; }
td,th { text-align:left; padding:6px 8px; border-bottom:1px solid var(--line); }
th { color:var(--faint); font-weight:600; font-size:11px; text-transform:uppercase; }
/* ---- lighting controls (phase 4) ---- */
.light { margin-top:14px; }
.ctl { display:flex; align-items:center; gap:10px; flex-wrap:wrap; margin-top:12px; }
.ctl > label { font-size:12px; color:var(--faint); min-width:76px; }
.seg { display:flex; gap:6px; flex-wrap:wrap; }
.seg button { background:var(--inset); border:1px solid var(--line2); color:var(--dim); font:13px "Segoe UI",sans-serif; padding:6px 14px; border-radius:99px; cursor:pointer; }
.openfull { background:linear-gradient(90deg,#4f8cff,#d06cd8,#ff7a45); color:#fff; font-weight:600; padding:7px 16px; border-radius:99px; text-decoration:none; font-size:13px; }
.seg button.on { background:linear-gradient(90deg,#4f8cff,#d06cd8,#ff7a45); border-color:transparent; color:#fff; font-weight:600; }
.sw { width:30px; height:30px; border-radius:9px; border:2px solid transparent; cursor:pointer; }
.sw.on { border-color:#fff; }
input[type=range] { width:min(220px, 52vw); accent-color:var(--accent); }
input[type=number] { background:var(--inset); color:var(--text); border:1px solid var(--line2); border-radius:8px; padding:4px 6px; font:13px "Segoe UI",sans-serif; }
.val { font-size:13px; font-weight:600; color:var(--accent2); min-width:44px; }
.badge { font-size:11.5px; padding:2px 9px; border-radius:99px; border:1px solid var(--line); color:var(--dim); margin-left:8px; }
.badge.sync { color:var(--good); border-color:#1f5a46; } .badge.differs { color:var(--warn); border-color:#5c4a14; }
.audit { font-size:11.5px; color:var(--faint); margin-top:10px; }
.note { font-size:12px; color:var(--faint); margin-top:10px; }
@media (max-width:760px) {
  body { display:block; }
  aside { width:auto; height:auto; position:static; border-right:0; border-bottom:1px solid var(--line); padding:12px; }
  .brand { padding-bottom:8px; }
  main { padding:16px; }
  .stats { grid-template-columns:repeat(2,1fr); }
  .ctl > label { min-width:0; width:100%; }
  .cmeta { margin-left:0; }
  body { overflow-x:hidden; }
}
</style>
</head>
<body>
<aside>
  <div class="brand">DeviceMaster<span>FLEET</span></div>
  <div class="navhead">Servers</div>
  <button class="nav" id="nav-all" onclick="pick(null)"><span class="dot" id="alldot"></span>All servers</button>
  <div id="servers"></div>
</aside>
<main id="main"><h1>Servers</h1><div class="sub">loading…</div></main>
<script>
let machines = [], selected = null, detail = null;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"})[c]);
const deg = v => v == null ? "—" : `${v.toFixed(0)} °C`;
const pct = v => v == null ? "—" : `${v}%`;
const state = m => !m.Online ? "bad" : (m.Failsafe ? "bad" : (m.WarningCount ? "warn" : "ok"));
const seen = m => m.Online ? "online" : (m.SecondsSinceSeen == null ? "never seen" : `offline · seen ${ago(m.SecondsSinceSeen)}`);
const ago = s => s < 90 ? `${s} s ago` : s < 5400 ? `${Math.round(s/60)} min ago` : `${Math.round(s/3600)} h ago`;
function hottest(m){ const v = [m.CpuTemperatureC, m.GpuTemperatureC].filter(x => x != null); return v.length ? Math.max(...v) : null; }
function caps(c){
  if (!c) return "";
  const names = { Pump:"Pump", CorsairHubs:"Corsair", LianLiFans:"Lian Li", MotherboardFans:"Board fans", MotherboardRgb:"Board RGB",
    RamRgb:"RAM RGB", GpuRgb:"GPU RGB", PumpLcd:"Pump LCD", FanLcds:"Fan LCDs", Turzx:"Turzx" };
  return `<div class="chips">${Object.keys(names).filter(k => c[k]).map(k => `<span class="chip">${names[k]}</span>`).join("")}</div>`;
}
function sidebar(){
  document.getElementById("nav-all").className = "nav" + (selected == null ? " active" : "");
  const any = machines.some(m => state(m) === "bad"), warn = machines.some(m => state(m) === "warn");
  document.getElementById("alldot").className = "dot " + (any ? "bad" : warn ? "warn" : "ok");
  document.getElementById("servers").innerHTML = machines.map(m =>
    `<button class="nav${selected === m.Name ? " active" : ""}" onclick='pick(${JSON.stringify(m.Name)})'>
      <span class="dot ${state(m)}"></span>${esc(m.Name)}<small>${m.Online ? deg(hottest(m)) : "off"}</small></button>`).join("");
}
function stats(m, big){
  return `<div class="stats${big ? " big" : ""}">
    <div class="stat"><div class="k">CPU</div><div class="v">${deg(m.CpuTemperatureC)}</div></div>
    <div class="stat"><div class="k">GPU</div><div class="v">${deg(m.GpuTemperatureC)}</div></div>
    <div class="stat"><div class="k">Fans</div><div class="v">${pct(m.FanDutyPercent)}</div></div>
    ${m.CoolantTemperatureC != null ? `<div class="stat"><div class="k">Coolant</div><div class="v">${deg(m.CoolantTemperatureC)}</div></div>` : ""}
    ${m.PumpDutyPercent != null ? `<div class="stat"><div class="k">Pump</div><div class="v">${pct(m.PumpDutyPercent)}</div></div>` : ""}
  </div>`;
}
function banners(m){
  if (!m.Online) return `<div class="banner off">Not answering${m.Error ? ` — ${esc(m.Error)}` : ""}. It keeps running on its own settings.</div>`;
  return m.Failsafe ? `<div class="banner bad">Failsafe: fans at 100 % (temperature unreadable)</div>` : "";
}
/* ---- fleet lighting (phase 4): one setting for every server, or a server's own ---- */
let settings = null, editing = false;
const SWATCHES = [["8000ff","Purple"],["ff00ff","Magenta"],["0066ff","Blue"],["00e5ff","Cyan"],["00ff66","Green"],["ff8800","Orange"],["ff2222","Red"],["ffffff","White"]];
const LIGHT_KEYS = ["RgbEnabled","RgbOff","RgbR","RgbG","RgbB","RgbBrightness","RgbEffect","RgbSpeed","Blackout"];
const SPEEDS = ["","Slowest","Slow","Medium","Fast","Fastest"];
const hex2 = n => n.toString(16).padStart(2,"0");
const fleetManaged = () => !!(settings && settings.state && (settings.state.Managed || []).includes("Lighting"));
function fleetLight(){ return settings && settings.state ? settings.state.Profile : null; }
function ownLight(name){
  const o = settings && settings.state && settings.state.Overrides ? settings.state.Overrides[name] : null;
  return o && LIGHT_KEYS.some(k => o[k] != null) ? o : null;
}
function lightFor(name){ const f = fleetLight() || {}, o = ownLight(name) || {}; const r = {}; LIGHT_KEYS.forEach(k => r[k] = o[k] ?? f[k]); return r; }
function badge(m){ return m.InSync === true ? `<span class="badge sync">in sync</span>` : m.InSync === false ? `<span class="badge differs">differs from fleet</span>` : ""; }
function mode(L){ return L.Blackout ? "blackout" : L.RgbOff ? "off" : L.RgbEffect === "Rainbow" ? "rainbow" : "colour"; }
async function put(url, body){
  const r = await fetch(url, { method:"PUT", headers:{"Content-Type":"application/json"}, body: JSON.stringify(body) });
  if (!r.ok) { const e = await r.json().catch(() => ({})); alert(e.error || `Not saved (${r.status})`); }
  await refresh();
}
function sendLight(scope, L){
  return scope == null ? put("/api/fleet/settings/lighting", { ...L, RgbEnabled:true, managed:true })
                       : put(`/api/fleet/machines/${encodeURIComponent(scope)}/lighting`, { ...L, RgbEnabled:true });
}
function lightControls(scope, L){
  const id = scope == null ? "fleet" : "m";
  const md = mode(L), cur = hex2(L.RgbR ?? 0) + hex2(L.RgbG ?? 0) + hex2(L.RgbB ?? 0);
  const S = JSON.stringify(scope), J = JSON.stringify(L).replace(/'/g, "&#39;");
  const set = patch => `sendLight(${S}, Object.assign(${J}, ${patch}))`;
  return `<div class="ctl"><label>Effect</label><div class="seg">
      <button class="${md==="colour"?"on":""}" onclick='${set(`{RgbEffect:"Static",RgbOff:false,Blackout:false}`)}'>Colour</button>
      <button class="${md==="rainbow"?"on":""}" onclick='${set(`{RgbEffect:"Rainbow",RgbOff:false,Blackout:false}`)}'>Rainbow</button>
      <button class="${md==="off"?"on":""}" onclick='${set(`{RgbOff:true,Blackout:false}`)}'>Lights out</button>
      <button class="${md==="blackout"?"on":""}" onclick='${set(`{Blackout:true}`)}'>Blackout</button></div></div>
    ${md === "colour" ? `<div class="ctl"><label>Colour</label>${SWATCHES.map(([h,n]) => `<div class="sw${h===cur?" on":""}" title="${n}" style="background:#${h}"
        onclick='${set(`{RgbR:${parseInt(h.slice(0,2),16)},RgbG:${parseInt(h.slice(2,4),16)},RgbB:${parseInt(h.slice(4,6),16)},RgbEffect:"Static",RgbOff:false,Blackout:false}`)}'></div>`).join("")}
        <input type="color" value="#${cur}" onchange='const v=this.value;${set(`{RgbR:parseInt(v.slice(1,3),16),RgbG:parseInt(v.slice(3,5),16),RgbB:parseInt(v.slice(5,7),16),RgbEffect:"Static",RgbOff:false,Blackout:false}`)}'></div>` : ""}
    ${md === "rainbow" ? `<div class="ctl"><label>Speed</label><input type="range" min="1" max="5" step="1" value="${L.RgbSpeed ?? 3}"
        oninput='document.getElementById("${id}-spd").textContent=${JSON.stringify(SPEEDS)}[this.value]' onchange='${set(`{RgbSpeed:+this.value}`)}'><span class="val" id="${id}-spd">${SPEEDS[L.RgbSpeed ?? 3]}</span></div>` : ""}
    ${md === "colour" || md === "rainbow" ? `<div class="ctl"><label>Brightness</label><input type="range" min="0" max="100" value="${L.RgbBrightness ?? 100}"
        oninput='document.getElementById("${id}-bri").textContent=this.value+"%"' onchange='${set(`{RgbBrightness:+this.value}`)}'><span class="val" id="${id}-bri">${L.RgbBrightness ?? 100}%</span></div>` : ""}`;
}
function fleetLightingCard(){
  if (!settings || !settings.writable) return "";
  const on = fleetManaged(), L = fleetLight() || {};
  const audit = (settings.audit || []).slice(0, 3).map(a => { try { const e = JSON.parse(a); return `${new Date(e.at).toLocaleString()} · ${esc(e.who)} · ${esc(e.what)}`; } catch { return ""; } }).filter(Boolean);
  return `<div class="card light" style="margin-bottom:14px"><div class="row"><span class="cname">Lighting — all servers</span>
      <span class="cmeta"><div class="seg"><button class="${on?"on":""}" onclick='sendLight(null, ${JSON.stringify(L)})'>Fleet</button>
      <button class="${on?"":"on"}" onclick='put("/api/fleet/settings/lighting", Object.assign(${JSON.stringify(L)}, {managed:false}))'>Each server</button></div></span></div>
    ${on ? lightControls(null, L) : `<div class="note">Each server keeps its own lighting. Pick Fleet to set every server at once.</div>`}
    ${audit.length ? `<div class="audit">${audit.join("<br>")}</div>` : ""}
    ${settings.user ? "" : `<div class="note">Open this page through its tailnet address to change settings.</div>`}</div>`;
}
function machineLightingCard(m){
  if (!settings || !settings.writable || !m.Capabilities || !m.Capabilities.Lighting) return "";
  if (!fleetManaged()) return `<div class="card light" style="margin-top:14px"><span class="cname">Lighting</span><div class="note">The fleet doesn't manage lighting; this server keeps its own (set it on the server, or switch "All servers" to Fleet).</div></div>`;
  const own = ownLight(m.Name), S = JSON.stringify(m.Name);
  return `<div class="card light" style="margin-top:14px"><div class="row"><span class="cname">Lighting</span>
      <span class="cmeta"><div class="seg"><button class="${own?"":"on"}" onclick='put("/api/fleet/machines/"+encodeURIComponent(${S})+"/lighting", {follow:true})'>Follow fleet</button>
      <button class="${own?"on":""}" onclick='sendLight(${S}, ${JSON.stringify(lightFor(m.Name))})'>Own</button></div></span></div>
    ${own ? lightControls(m.Name, lightFor(m.Name)) : `<div class="note">Uses the fleet lighting.</div>`}
    ${m.PushError ? `<div class="note" style="color:var(--warn)">Last push failed: ${esc(m.PushError)}</div>` : ""}</div>`;
}
/* ---- fleet cooling + screens: same pattern as lighting ---- */
const SECTION_KEYS = {
  cooling: ["Mode","ManualDutyPercent","PumpDutyPercent","Source","CurvePoints"],
  screens: ["LcdScreens","LcdBrightness","TurzxScreen","TurzxBrightness","PumpScreenMetric","FanScreenMetric","TurzxMetric"],
};
const SECTION_NAME = { cooling:"Cooling", screens:"Screens" };
const managedSection = s => !!(settings && settings.state && (settings.state.Managed || []).includes(SECTION_NAME[s]));
function ownSection(name, s){
  const o = settings && settings.state && settings.state.Overrides ? settings.state.Overrides[name] : null;
  return o && SECTION_KEYS[s].some(k => o[k] != null) ? o : null;
}
function valuesFor(name, s){ const f = (settings && settings.state && settings.state.Profile) || {}, o = (name && ownSection(name, s)) || {}; const r = {}; SECTION_KEYS[s].forEach(k => r[k] = o[k] ?? f[k]); return r; }
function sendSection(s, scope, v){
  return scope == null ? put(`/api/fleet/settings/${s}`, { ...v, managed:true })
                       : put(`/api/fleet/machines/${encodeURIComponent(scope)}/${s}`, v);
}
const anyPump = () => machines.some(m => m.Capabilities && m.Capabilities.Pump);
function coolingControls(scope, v){
  const S = JSON.stringify(scope), J = JSON.stringify(v).replace(/'/g, "&#39;"), fixed = v.Mode === "Manual";
  const set = patch => `sendSection("cooling", ${S}, Object.assign(${J}, ${patch}))`;
  const pts = (v.CurvePoints || []).length >= 2 ? v.CurvePoints : [{TemperatureC:20,DutyPercent:0},{TemperatureC:80,DutyPercent:100}];
  const id = scope == null ? "fc" : "mc";
  const pumpRow = (scope == null ? anyPump() : (machines.find(m => m.Name === scope)?.Capabilities?.Pump)) ? `<div class="ctl"><label>Pump</label>
      <input type="range" min="50" max="100" value="${v.PumpDutyPercent ?? 100}" oninput='document.getElementById("${id}-pump").textContent=this.value+"%"' onchange='${set(`{PumpDutyPercent:+this.value}`)}'>
      <span class="val" id="${id}-pump">${v.PumpDutyPercent ?? 100}%</span><span class="note" style="margin:0">never below 50 %</span></div>` : "";
  return `<div class="ctl"><label>Fans</label><div class="seg">
      <button class="${fixed?"":"on"}" onclick='${set(`{Mode:"Curve"}`)}'>Curve</button>
      <button class="${fixed?"on":""}" onclick='${set(`{Mode:"Manual"}`)}'>Fixed</button></div></div>
    ${fixed ? `<div class="ctl"><label>Duty</label><input type="range" min="0" max="100" value="${v.ManualDutyPercent ?? 50}"
        oninput='document.getElementById("${id}-duty").textContent=this.value+"%"' onchange='${set(`{ManualDutyPercent:+this.value}`)}'><span class="val" id="${id}-duty">${v.ManualDutyPercent ?? 50}%</span></div>`
    : `<div class="ctl"><label>Curve</label>
        <input id="${id}-t0" type="number" min="20" max="99" value="${pts[0].TemperatureC}" style="width:62px"> °C →
        <input id="${id}-d0" type="number" min="0" max="100" value="${pts[0].DutyPercent}" style="width:58px"> %, rising to
        <input id="${id}-d1" type="number" min="0" max="100" value="${pts[1].DutyPercent}" style="width:58px"> % at
        <input id="${id}-t1" type="number" min="21" max="100" value="${pts[1].TemperatureC}" style="width:62px"> °C
        <div class="seg"><button class="on" onclick='const g=k=>+document.getElementById("${id}-"+k).value;${set(`{CurvePoints:[{TemperatureC:g("t0"),DutyPercent:g("d0")},{TemperatureC:g("t1"),DutyPercent:g("d1")}]}`)}'>Apply</button></div></div>
      <div class="note">Each server follows the hotter of its own CPU and GPU (a server without a GPU uses its CPU). An unreadable temperature still means 100 %.</div>`}
    ${pumpRow}`;
}
function screensControls(scope, v){
  const S = JSON.stringify(scope), J = JSON.stringify(v).replace(/'/g, "&#39;"), id = scope == null ? "fs" : "ms";
  const set = patch => `sendSection("screens", ${S}, Object.assign(${J}, ${patch}))`;
  const lcdOn = v.LcdScreens === "Metrics", tzOn = v.TurzxScreen === "Metrics";
  return `<div class="ctl"><label>Fan &amp; pump screens</label><div class="seg">
      <button class="${lcdOn?"on":""}" onclick='${set(`{LcdScreens:"Metrics"}`)}'>On</button>
      <button class="${v.LcdScreens==="Off"?"on":""}" onclick='${set(`{LcdScreens:"Off"}`)}'>Off</button></div>
      <input type="range" min="0" max="100" value="${v.LcdBrightness ?? 100}" oninput='document.getElementById("${id}-lb").textContent=this.value+"%"' onchange='${set(`{LcdBrightness:+this.value}`)}'><span class="val" id="${id}-lb">${v.LcdBrightness ?? 100}%</span></div>
    <div class="ctl"><label>Turzx dashboard</label><div class="seg">
      <button class="${tzOn?"on":""}" onclick='${set(`{TurzxScreen:"Metrics"}`)}'>On</button>
      <button class="${v.TurzxScreen==="Off"?"on":""}" onclick='${set(`{TurzxScreen:"Off"}`)}'>Off</button></div>
      <input type="range" min="0" max="100" value="${v.TurzxBrightness ?? 100}" oninput='document.getElementById("${id}-tb").textContent=this.value+"%"' onchange='${set(`{TurzxBrightness:+this.value}`)}'><span class="val" id="${id}-tb">${v.TurzxBrightness ?? 100}%</span></div>`;
}
const CONTROLS = { cooling: coolingControls, screens: screensControls };
const DEFAULTS = {
  cooling: { Mode:"Curve", ManualDutyPercent:50, PumpDutyPercent:100, Source:"Hottest", CurvePoints:[{TemperatureC:20,DutyPercent:0},{TemperatureC:80,DutyPercent:100}] },
  screens: { LcdScreens:"Metrics", LcdBrightness:100, TurzxScreen:"Metrics", TurzxBrightness:100 },
};
function fleetSectionCard(s, title){
  if (!settings || !settings.writable) return "";
  const on = managedSection(s), v = on ? valuesFor(null, s) : Object.assign({}, valuesFor(null, s), s === "cooling" ? DEFAULTS.cooling : {});
  return `<div class="card light" style="margin-bottom:14px"><div class="row"><span class="cname">${title} — all servers</span>
      <span class="cmeta"><div class="seg"><button class="${on?"on":""}" onclick='sendSection("${s}", null, ${JSON.stringify(v)})'>Fleet</button>
      <button class="${on?"":"on"}" onclick='put("/api/fleet/settings/${s}", Object.assign(${JSON.stringify(v)}, {managed:false}))'>Each server</button></div></span></div>
    ${on ? CONTROLS[s](null, v) : `<div class="note">Each server keeps its own ${title.toLowerCase()}. Pick Fleet to set every server at once${s === "cooling" ? " (starts as the CPU/GPU curve 0 % at 20 °C → 100 % at 80 °C)" : ""}.</div>`}</div>`;
}
function machineSectionCard(s, title, m){
  if (!settings || !settings.writable || !managedSection(s)) return "";
  if (s === "screens" && m.Capabilities && !(m.Capabilities.PumpLcd || m.Capabilities.FanLcds || m.Capabilities.Turzx)) return "";
  const own = ownSection(m.Name, s), S = JSON.stringify(m.Name);
  return `<div class="card light" style="margin-top:14px"><div class="row"><span class="cname">${title}</span>
      <span class="cmeta"><div class="seg"><button class="${own?"":"on"}" onclick='put("/api/fleet/machines/"+encodeURIComponent(${S})+"/${s}", {follow:true})'>Follow fleet</button>
      <button class="${own?"on":""}" onclick='sendSection("${s}", ${S}, ${JSON.stringify(valuesFor(m.Name, s))})'>Own</button></div></span></div>
    ${own ? CONTROLS[s](m.Name, valuesFor(m.Name, s)) : `<div class="note">Uses the fleet ${title.toLowerCase()}.</div>`}</div>`;
}
function overview(){
  const online = machines.filter(m => m.Online).length;
  return `<h1>Servers</h1><div class="sub">${online} of ${machines.length} online</div>${fleetSectionCard("cooling","Cooling")}${fleetLightingCard()}${fleetSectionCard("screens","Screens")}<div class="cards">` + machines.map(m =>
    `<div class="card click" onclick='pick(${JSON.stringify(m.Name)})'>
      <div class="row"><span class="dot ${state(m)}"></span><span class="cname">${esc(m.Name)}</span>${badge(m)}<span class="cmeta">${esc(seen(m))}</span></div>
      ${stats(m)}${banners(m)}</div>`).join("") + "</div>";
}
function machinePage(m){
  const d = detail && detail.summary && detail.summary.Name === m.Name ? detail.status : null;
  const meta = [m.MachineName, m.Platform, m.Version && m.Version.split("+")[0] && `v${m.Version.split("+")[0].split(".")[0]}`].filter(Boolean).join(" · ");
  let html = `<h1>${esc(m.Name)}${badge(m)}</h1><div class="sub">${esc(seen(m))}${meta ? " · " + esc(meta) : ""}</div>
    ${m.Online ? `<div class="seg" style="margin:0 0 14px 2px"><a class="openfull" href="/m/${encodeURIComponent(m.Name)}/">Open full controls →</a></div>` : ""}
    <div class="card">${stats(m, true)}${banners(m)}
      <div class="sub" style="margin:12px 0 0">Fans follow ${esc(m.Source || "—")}${m.SourceTemperatureC != null ? ` at ${m.SourceTemperatureC.toFixed(1)} °C` : ""} · ${esc(m.Mode || "")}</div>
      ${caps(m.Capabilities)}</div>${machineSectionCard("cooling","Cooling",m)}${machineLightingCard(m)}${machineSectionCard("screens","Screens",m)}`;
  if (d && d.Warnings && d.Warnings.length) html += `<div class="card" style="margin-top:14px"><b>Warnings</b><ul class="warn">${d.Warnings.map(w => `<li>${esc(w)}</li>`).join("")}</ul></div>`;
  if (d && d.Devices && d.Devices.length) html += `<div class="card" style="margin-top:14px"><b>Devices</b><table><tr><th>Device</th><th>Family</th><th>RPM</th><th>Duty</th></tr>` +
    d.Devices.filter(x => !x.IsScreen).map(x => `<tr><td>${esc(x.Name)}${x.IsPump ? " (pump)" : ""}</td><td>${esc(x.Family)}</td><td>${x.Rpm ?? "—"}</td><td>${x.AppliedDutyPercent}%</td></tr>`).join("") + "</table></div>";
  return html;
}
function render(){
  sidebar();
  if (editing) return; // never redraw under a slider being dragged
  const m = machines.find(x => x.Name === selected);
  document.getElementById("main").innerHTML = m ? machinePage(m) : overview();
}
document.addEventListener("pointerdown", e => { if (e.target.closest && e.target.closest(".light")) editing = true; });
document.addEventListener("pointerup", () => setTimeout(() => editing = false, 400));
document.addEventListener("focusin", e => { if (e.target.type === "color" || e.target.type === "number") editing = true; });
document.addEventListener("focusout", e => { if (e.target.type === "number") setTimeout(() => editing = false, 300); });
document.addEventListener("change", e => { if (e.target.type === "color") editing = false; });
function pick(name){ selected = name; detail = null; history.replaceState(null, "", name ? "#" + encodeURIComponent(name) : "#"); render(); refresh(); }
async function refresh(){
  try {
    machines = await (await fetch("/api/fleet/machines")).json();
    settings = await (await fetch("/api/fleet/settings")).json();
    if (selected) detail = await (await fetch("/api/fleet/machines/" + encodeURIComponent(selected))).json();
    render();
  } catch { document.getElementById("alldot").className = "dot bad"; }
}
selected = location.hash.length > 1 ? decodeURIComponent(location.hash.slice(1)) : null;
refresh(); setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}
