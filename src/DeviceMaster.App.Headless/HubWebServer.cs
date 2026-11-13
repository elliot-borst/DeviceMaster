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

    public HubWebServer(FleetHub hub, int port, Action<string> log, IReadOnlyList<string> binds)
    {
        _hub = hub;
        _log = log;
        foreach (var host in binds.Count > 0 ? binds : ["127.0.0.1"])
        {
            _listener.Prefixes.Add($"http://{host}:{port}/");
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "hub web" };
    }

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
        if (ctx.Request.HttpMethod != "GET")
        {
            Send(ctx, 405, "text/plain; charset=utf-8", "read-only");
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
.row { display:flex; align-items:center; gap:10px; }
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
@media (max-width:760px) {
  body { display:block; }
  aside { width:auto; height:auto; position:static; border-right:0; border-bottom:1px solid var(--line); padding:12px; }
  .brand { padding-bottom:8px; }
  main { padding:16px; }
  .stats { grid-template-columns:repeat(2,1fr); }
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
function overview(){
  const online = machines.filter(m => m.Online).length;
  return `<h1>Servers</h1><div class="sub">${online} of ${machines.length} online</div><div class="cards">` + machines.map(m =>
    `<div class="card click" onclick='pick(${JSON.stringify(m.Name)})'>
      <div class="row"><span class="dot ${state(m)}"></span><span class="cname">${esc(m.Name)}</span><span class="cmeta">${esc(seen(m))}</span></div>
      ${stats(m)}${banners(m)}</div>`).join("") + "</div>";
}
function machinePage(m){
  const d = detail && detail.summary && detail.summary.Name === m.Name ? detail.status : null;
  const meta = [m.MachineName, m.Platform, m.Version && m.Version.split("+")[0] && `v${m.Version.split("+")[0].split(".")[0]}`].filter(Boolean).join(" · ");
  let html = `<h1>${esc(m.Name)}</h1><div class="sub">${esc(seen(m))}${meta ? " · " + esc(meta) : ""}</div>
    <div class="card">${stats(m, true)}${banners(m)}
      <div class="sub" style="margin:12px 0 0">Fans follow ${esc(m.Source || "—")}${m.SourceTemperatureC != null ? ` at ${m.SourceTemperatureC.toFixed(1)} °C` : ""} · ${esc(m.Mode || "")}</div>
      ${caps(m.Capabilities)}</div>`;
  if (d && d.Warnings && d.Warnings.length) html += `<div class="card" style="margin-top:14px"><b>Warnings</b><ul class="warn">${d.Warnings.map(w => `<li>${esc(w)}</li>`).join("")}</ul></div>`;
  if (d && d.Devices && d.Devices.length) html += `<div class="card" style="margin-top:14px"><b>Devices</b><table><tr><th>Device</th><th>Family</th><th>RPM</th><th>Duty</th></tr>` +
    d.Devices.filter(x => !x.IsScreen).map(x => `<tr><td>${esc(x.Name)}${x.IsPump ? " (pump)" : ""}</td><td>${esc(x.Family)}</td><td>${x.Rpm ?? "—"}</td><td>${x.AppliedDutyPercent}%</td></tr>`).join("") + "</table></div>";
  return html;
}
function render(){
  sidebar();
  const m = machines.find(x => x.Name === selected);
  document.getElementById("main").innerHTML = m ? machinePage(m) : overview();
}
function pick(name){ selected = name; detail = null; history.replaceState(null, "", name ? "#" + encodeURIComponent(name) : "#"); render(); refresh(); }
async function refresh(){
  try {
    machines = await (await fetch("/api/fleet/machines")).json();
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
