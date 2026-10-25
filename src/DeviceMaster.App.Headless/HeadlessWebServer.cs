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

    public HeadlessWebServer(HeadlessLoop loop, int port, Action<string> log, string configPath)
    {
        _loop = loop;
        _port = port;
        _log = log;
        _configPath = configPath;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
        _thread = new Thread(Run) { IsBackground = true, Name = "web" };
    }

    public void Start()
    {
        _listener.Start();
        _thread.Start();
        _log($"web dashboard on :{_port} (page /, data /status.json)");
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
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        var handled = false;
        switch ((method, path))
        {
            case ("GET", "/") or ("GET", "/index.html"):
                ctx.Response.StatusCode = 200;
                ctx.Response.AddHeader("Cache-Control", "no-store");
                Write(ctx, "text/html; charset=utf-8", DashboardHtml);
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

            var applied = ConfigPatcher.ApplyPatch(cfg, body, out var invalid);
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
    private const string DashboardHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>DeviceMaster</title>
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'%3E%3Cdefs%3E%3ClinearGradient id='g' x1='0' y1='1' x2='1' y2='0'%3E%3Cstop offset='0' stop-color='%234f8cff'/%3E%3Cstop offset='.5' stop-color='%23d06cd8'/%3E%3Cstop offset='1' stop-color='%23ff7a45'/%3E%3C/linearGradient%3E%3CclipPath id='d'%3E%3Cpath d='M11.5 12H32.50A20.0 20.0 0 0 1 32.50 52H11.5Z'/%3E%3C/clipPath%3E%3C/defs%3E%3Crect x='2' y='2' width='60' height='60' rx='14' fill='url(%23g)'/%3E%3Cg clip-path='url(%23d)' fill='%230f1115'%3E%3Crect x='11.50' y='12' width='8.00' height='40' rx='2.40'/%3E%3Crect x='22.50' y='12' width='8.00' height='40' rx='2.40'/%3E%3Crect x='33.50' y='12' width='8.00' height='40' rx='2.40'/%3E%3Crect x='44.50' y='12' width='8.00' height='40' rx='2.40'/%3E%3C/g%3E%3C/svg%3E">
<style>
:root {
  --bg:#0f1115; --card:#171a20; --card2:#1d2129; --line:#2a2f39; --line2:#3c4351;
  --text:#eceef2; --dim:#a0a6b3; --faint:#6b7180; --accent:#ff7a45; --accent2:#ffb08f; --cool:#4f8cff;
  --good:#34d399; --warn:#fbbf24; --danger:#ef4444; --ink:#2a0e02; --inset:#0c0e12; --tile:#1a1e26;
  --grad:linear-gradient(90deg,#4f8cff,#d06cd8,#ff7a45);
}
* { box-sizing:border-box; margin:0; }
html,body { height:100%; }
body { background:var(--bg); color:var(--text); font:14px/1.45 "Segoe UI",system-ui,sans-serif; display:flex; }
.mono { font-family:Consolas,monospace; }
/* ---- sidebar ---- */
aside { width:218px; flex:none; background:var(--card); border-right:1px solid var(--line); padding:18px 14px 16px; position:sticky; top:0; height:100vh; display:flex; flex-direction:column; }
.brandrow { display:flex; align-items:center; gap:11px; padding:0 4px 22px; }
.brandrow svg { width:44px; height:44px; flex:none; }
.brand { font-size:17px; font-weight:700; line-height:1.2; }
.brandsub { font-size:10.5px; color:var(--faint); }
.navbtn { display:flex; align-items:center; gap:10px; width:100%; background:transparent; border:0; color:var(--dim);
  font:13.5px "Segoe UI",sans-serif; padding:10px 12px; margin:2px 0; border-radius:10px; cursor:pointer; text-align:left; }
.navbtn .glyph { width:26px; font-size:15px; color:var(--dim); }
.navbtn:hover { background:var(--card2); }
.navbtn.active { background:var(--card2); color:var(--text); font-weight:600; }
.navbtn.active .glyph { color:var(--accent2); }
.aside-foot { margin-top:auto; padding:12px 4px 0; font-size:10.5px; color:var(--faint); display:flex; align-items:center; gap:8px; }
.dot { width:8px; height:8px; border-radius:50%; background:var(--faint); flex:none; }
.dot.ok { background:var(--good); } .dot.bad { background:var(--danger); }
/* ---- main ---- */
main { flex:1; padding:22px 26px 16px; min-width:0; }
.page { display:none; max-width:1460px; } .page.active { display:block; }
h1 { font-size:24px; font-weight:700; margin:0 0 18px 2px; }
h1 .sub { font-size:12.5px; font-weight:400; color:var(--faint); margin-left:12px; }
.dashstatus { font-size:14px; font-weight:600; margin:0 0 14px 2px; }
.dashstatus.warn { color:var(--warn); }
.card { background:var(--card); border:1px solid var(--line); border-radius:15px; padding:18px 20px; margin:0 12px 16px 0; }
.cardhead { display:flex; align-items:center; gap:12px; margin-bottom:15px; }
.tile { width:34px; height:34px; border-radius:10px; background:var(--tile); border:1px solid var(--line2);
  display:flex; align-items:center; justify-content:center; color:var(--accent); font-size:16px; flex:none; }
.cardtitle { font-size:15.5px; font-weight:600; }
.cardsub { font-size:11.5px; color:var(--faint); }
.grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(168px,1fr)); gap:12px; }
.tiles { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); margin-bottom:0; }
@media (max-width:1100px){ .tiles { grid-template-columns:repeat(2,minmax(0,1fr)); } }
/* CPU | GPU sections, each with four inset stats (MiniStat on the desktop) */
.procs { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); margin-top:4px; }
@media (max-width:1100px){ .procs { grid-template-columns:minmax(0,1fr); } }
.minis { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); }
.mini { background:var(--inset); border:1px solid var(--line); border-radius:11px; padding:11px 14px; margin-right:8px; min-width:0; }
.mini .k { font-size:10.5px; font-weight:600; color:var(--faint); }
.mini .v { font-size:24px; font-weight:700; margin:4px 0 2px; line-height:1.15; white-space:nowrap; }
.mini .sub { font-size:10.5px; color:var(--faint); min-height:14px; }
.stat { background:var(--card); border:1px solid var(--line); border-radius:14px; padding:14px 18px; margin:0 12px 12px 0; min-width:0; }
.stat .k { font-size:10.5px; font-weight:600; text-transform:uppercase; color:var(--faint); }
.stat .v { font-size:30px; font-weight:700; margin:4px 0 2px; line-height:1.15; white-space:nowrap; }
.stat .sub { font-size:10.5px; color:var(--faint); min-height:14px; }
/* "Fans right now": same pills as the desktop dashboard */
.fangrid { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:0 7px; }
@media (max-width:1100px){ .fangrid { grid-template-columns:repeat(2,minmax(0,1fr)); } }
.fanpill { display:flex; justify-content:space-between; gap:10px; background:var(--inset); border:1px solid var(--line); border-radius:10px;
  padding:8px 12px; margin-bottom:7px; font-size:11.5px; min-width:0; }
.fanpill .n { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.fanpill .r { color:var(--accent2); font-weight:600; white-space:nowrap; }
.fanpill .r.none { color:var(--faint); font-weight:400; }
.dashwarn { color:var(--warn); font-size:12px; margin:2px 0 2px 2px; }
.v.good { color:var(--good); } .v.warn { color:var(--warn); } .v.bad { color:var(--danger); }
/* banner */
.banner { display:none; border-radius:12px; padding:13px 16px; margin-bottom:16px; font-size:13.5px; border:1px solid; }
.banner.bad { display:block; background:rgba(239,68,68,.1); border-color:var(--danger); color:var(--danger); }
.banner.warn { display:block; background:rgba(251,191,36,.08); border-color:var(--warn); color:var(--warn); }
/* controls */
.row { display:flex; align-items:center; gap:14px; margin:11px 0; flex-wrap:wrap; }
.row label { min-width:118px; font-size:13px; color:var(--dim); }
.row .val { min-width:64px; text-align:right; font-family:Consolas,monospace; font-size:13.5px; }
select, input[type=color] { background:var(--card2); border:1px solid var(--line2); color:var(--text); border-radius:8px; padding:7px 10px; font-size:13px; }
input[type=range] { flex:1; max-width:340px; accent-color:var(--accent); height:22px; }
input[type=color] { padding:3px 5px; width:52px; height:36px; cursor:pointer; }
.toggle { position:relative; width:42px; height:24px; flex:none; }
.toggle input { opacity:0; width:0; height:0; }
.toggle .tk { position:absolute; inset:0; background:var(--card2); border:1px solid var(--line2); border-radius:12px; cursor:pointer; transition:.15s; }
.toggle .tk:before { content:""; position:absolute; width:16px; height:16px; border-radius:50%; background:var(--dim); left:3px; top:3px; transition:.15s; }
.toggle input:checked + .tk { background:rgba(255,122,69,.25); border-color:var(--accent); }
.toggle input:checked + .tk:before { transform:translateX(18px); background:var(--accent); }
.btn { background:var(--card2); border:1px solid var(--line2); color:var(--text); border-radius:10px; padding:8px 14px; font-size:12.5px; cursor:pointer; }
.btn:hover { opacity:.85; }
.btn.primary { background:var(--grad); color:var(--ink); font-weight:600; border:0; }
.note { font-size:12px; color:var(--faint); margin-top:8px; line-height:1.5; }
/* swatches */
.swatches { display:flex; gap:8px; flex-wrap:wrap; }
.swatch { width:30px; height:30px; border-radius:8px; border:2px solid var(--line2); cursor:pointer; }
.swatch.sel { border-color:var(--text); }
.preview { width:52px; height:34px; border-radius:9px; background:#8000ff; border:1px solid var(--line2); }
/* table */
table { width:100%; border-collapse:collapse; font-size:13px; }
th { text-align:left; color:var(--dim); font-weight:500; font-size:11.5px; text-transform:uppercase; letter-spacing:.4px; padding:7px 10px; border-bottom:1px solid var(--line); }
td { padding:8px 10px; border-bottom:1px solid var(--line); color:var(--text); }
tr:last-child td { border-bottom:0; }
td.dim { color:var(--dim); }
.pill { display:inline-block; padding:2px 9px; border-radius:9px; font-size:11px; border:1px solid var(--line2); color:var(--dim); }
.pill.fan { color:var(--accent); border-color:rgba(255,122,69,.4); }
.pill.pump { color:var(--good); border-color:rgba(52,211,153,.4); }
.pill.screen { color:var(--cool); border-color:rgba(79,140,255,.45); }
/* toast */
#toast { position:fixed; right:22px; bottom:22px; background:var(--card2); border:1px solid var(--line2); border-radius:10px;
  padding:11px 16px; font-size:13px; color:var(--text); opacity:0; transform:translateY(8px); transition:.2s; pointer-events:none; }
#toast.show { opacity:1; transform:none; }
#toast.err { border-color:var(--danger); color:var(--danger); }
@media (max-width:760px){ aside{width:64px} .brandrow div,.navbtn .lbl,.aside-foot span{display:none} .brandrow svg{width:36px;height:36px} .navbtn{justify-content:center} .tiles{grid-template-columns:repeat(2,minmax(0,1fr))} .fangrid{grid-template-columns:minmax(0,1fr)} }
</style>
</head>
<body>
<aside>
  <div class="brandrow"><svg viewBox="0 0 64 64" aria-hidden="true"><defs><linearGradient id="lg" x1="0" y1="1" x2="1" y2="0"><stop offset="0" stop-color="#4f8cff"/><stop offset=".5" stop-color="#d06cd8"/><stop offset="1" stop-color="#ff7a45"/></linearGradient><clipPath id="ld"><path d="M11.5 12H32.5A20 20 0 0 1 32.5 52H11.5Z"/></clipPath></defs><rect x="2" y="2" width="60" height="60" rx="14" fill="url(#lg)"/><g clip-path="url(#ld)" fill="#0f1115"><rect x="11.5" y="12" width="8.25" height="40" rx="2.48"/><rect x="22.75" y="12" width="8.25" height="40" rx="2.48"/><rect x="34" y="12" width="8.25" height="40" rx="2.48"/><rect x="45.25" y="12" width="8.25" height="40" rx="2.48"/></g></svg><div><div class="brand">DeviceMaster</div><div class="brandsub">cooling · RGB · screens</div></div></div>
  <button class="navbtn active" data-page="dashboard"><span class="glyph">⌂</span><span class="lbl">Dashboard</span></button>
  <button class="navbtn" data-page="cooling"><span class="glyph">❄</span><span class="lbl">Cooling</span></button>
  <button class="navbtn" data-page="lighting"><span class="glyph">◈</span><span class="lbl">Lighting</span></button>
  <button class="navbtn" data-page="screens"><span class="glyph">▣</span><span class="lbl">Screens</span></button>
  <button class="navbtn" data-page="devices"><span class="glyph">⚙</span><span class="lbl">Devices</span></button>
  <div class="aside-foot"><div class="dot" id="dot"></div><span id="foot">connecting…</span><span style="margin-left:auto">headless · unraid</span></div>
</aside>
<main>

  <!-- ============ DASHBOARD ============ -->
  <section class="page active" id="page-dashboard">
    <h1>Dashboard<span class="sub">the whole loop at a glance</span></h1>
    <div class="dashstatus" id="dashStatus">Connecting…</div>
    <div class="tiles">
      <div class="stat"><div class="k">Coolant</div><div class="v" id="coolant">—</div><div class="sub">loop water</div></div>
      <div class="stat"><div class="k">Pump</div><div class="v" id="pump">—</div><div class="sub" id="pumpSub"></div></div>
      <div class="stat"><div class="k">Fans</div><div class="v" id="fans">—</div><div class="sub" id="fansSub"></div></div>
      <div class="stat"><div class="k">Duty</div><div class="v" id="duty">—</div><div class="sub" id="dutySub"></div></div>
    </div>
    <div class="procs">
      <div class="card">
        <div class="cardhead"><div class="tile">▦</div><div><div class="cardtitle">CPU</div><div class="cardsub" id="cpuName">—</div></div></div>
        <div class="minis"><div class="mini"><div class="k">TEMPERATURE</div><div class="v" id="cpuTemp">—</div><div class="sub" id="cpuTempSub"></div></div><div class="mini"><div class="k">LOAD</div><div class="v" id="cpuLoad">—</div><div class="sub" id="cpuLoadSub"></div></div><div class="mini"><div class="k">POWER</div><div class="v" id="cpuPower">—</div><div class="sub" id="cpuPowerSub"></div></div><div class="mini"><div class="k">RAM</div><div class="v" id="cpuRam">—</div><div class="sub" id="cpuRamSub"></div></div></div>
      </div>
      <div class="card">
        <div class="cardhead"><div class="tile">▤</div><div><div class="cardtitle">GPU</div><div class="cardsub" id="gpuName">—</div></div></div>
        <div class="minis"><div class="mini"><div class="k">TEMPERATURE</div><div class="v" id="gpuTemp">—</div><div class="sub" id="gpuTempSub"></div></div><div class="mini"><div class="k">LOAD</div><div class="v" id="gpuLoad">—</div><div class="sub" id="gpuLoadSub"></div></div><div class="mini"><div class="k">POWER</div><div class="v" id="gpuPower">—</div><div class="sub" id="gpuPowerSub"></div></div><div class="mini"><div class="k">VRAM</div><div class="v" id="gpuVram">—</div><div class="sub" id="gpuVramSub"></div></div></div>
      </div>
    </div>
    <div class="card">
      <div class="cardhead"><div class="tile">✻</div><div><div class="cardtitle">Fans right now</div><div class="cardsub">every fan with its live speed</div></div></div>
      <div class="fangrid" id="fanGrid"></div>
    </div>
    <div id="dashWarnings"></div>
  </section>

  <!-- ============ COOLING ============ -->
  <section class="page" id="page-cooling">
    <h1>Cooling<span class="sub">Fan and pump control. Changes apply within ~1 s (config hot-reload).</span></h1>
    <div class="card">
      <div class="cardhead"><div class="tile">❄</div><div><div class="cardtitle">Fans</div><div class="cardsub" id="curveSrcLine"></div></div></div>
      <div class="row"><label for="mode">Mode</label>
        <select id="mode"><option value="0">Off (firmware rules)</option><option value="1">Manual</option><option value="2">Curve</option></select>
        <span class="val" id="modeEcho"></span></div>
      <div class="row"><label for="fanDuty">Fan duty</label>
        <input type="range" id="fanDuty" min="0" max="100" step="5"><span class="val" id="fanDutyVal">—</span></div>
      <div class="row"><label for="source">Curve source</label>
        <select id="source"><option value="0">Coolant (loop temp)</option><option value="2">GPU</option></select></div>
      <div class="note" id="fanNote"></div>
    </div>
    <div class="card">
      <div class="cardhead"><div class="tile">◉</div><div><div class="cardtitle">Pump</div><div class="cardsub">XD6 ELITE — duty floor 50 % (hardware safety)</div></div></div>
      <div class="row"><label for="pumpDuty">Pump duty</label>
        <input type="range" id="pumpDuty" min="50" max="100" step="5"><span class="val" id="pumpDutyVal">—</span></div>
      <div class="row"><label>RPM</label><span class="val" id="pumpRpm">—</span></div>
      <div class="note">Duty below 50 % is refused — low pump speed can starve the loop. Any sensor error drives pump and fans to 100 % (failsafe).</div>
    </div>
    <div class="card">
      <div class="cardhead"><div class="tile">〜</div><div><div class="cardtitle">Fan curve</div><div class="cardsub">temperature → duty (read-only; edit config.json to change)</div></div></div>
      <table><thead><tr><th>Temp</th><th>Duty</th></tr></thead><tbody id="curvePts"></tbody></table>
    </div>
  </section>

  <!-- ============ LIGHTING ============ -->
  <section class="page" id="page-lighting">
    <h1>Lighting<span class="sub">Hub RGB (70 addressable LEDs across both hubs).</span></h1>
    <div class="card">
      <div class="cardhead"><div class="tile">◈</div><div><div class="cardtitle">Hub RGB</div><div class="cardsub">applies to every LED on both hubs</div></div></div>
      <div class="row"><label>Enabled</label>
        <span class="toggle"><input type="checkbox" id="rgbOn"><span class="tk"></span></span>
        <span class="val" id="rgbOnEcho"></span></div>
      <div class="row"><label for="rgbColor">Colour</label>
        <input type="color" id="rgbColor">
        <div class="swatches" id="swatches"></div></div>
      <div class="row"><label for="rgbBright">Brightness</label>
        <input type="range" id="rgbBright" min="0" max="100" step="5"><span class="val" id="rgbBrightVal">—</span></div>
      <div class="row"><label>Preview</label><div class="preview" id="rgbPreview"></div><span class="val mono" id="rgbHex"></span></div>
    </div>
    <div class="card">
      <div class="cardhead"><div class="tile">▣</div><div><div class="cardtitle">GPU RGB (ENE)</div><div class="cardsub">4090 Strix</div></div></div>
      <div class="note">Driver-blocked at the hardware level (verified across three nvidia driver branches) — the purple on the card is the
      ENE chip's non-volatile flash (last saved effect) and cannot be changed from this stack. The OpenRGB boot hook stays in place
      and would take over automatically if a future driver exposes the bus.</div>
    </div>
  </section>

  <!-- ============ SCREENS ============ -->
  <section class="page" id="page-screens">
    <h1>Screens<span class="sub">Pump LCD (XD6 ELITE 480×480). Frames re-send every 10 s so the panel never reverts.</span></h1>
    <div class="card">
      <div class="cardhead"><div class="tile">▣</div><div><div class="cardtitle">Pump LCD</div><div class="cardsub">serial A9SST532000A1A</div></div></div>
      <div class="row"><label for="lcdMode">Mode</label>
        <select id="lcdMode">
          <option value="0">Unmanaged (panel's own screen)</option>
          <option value="1">Off</option>
          <option value="2">Black</option>
          <option value="3">White</option>
          <option value="4">Metrics</option>
        </select></div>
      <div class="row"><label for="lcdMetric">Metric</label>
        <select id="lcdMetric">
          <option value="0">Coolant temp</option>
          <option value="1">CPU temp</option>
          <option value="2">GPU temp</option>
          <option value="7">Pump RPM</option>
          <option value="8">Fan duty</option>
          <option value="9">Date</option>
          <option value="11">Fan RPM</option>
          <option value="12">Pump duty</option>
        </select></div>
      <div class="row"><label for="lcdBright">Brightness</label>
        <input type="range" id="lcdBright" min="0" max="100" step="5"><span class="val" id="lcdBrightVal">—</span></div>
    </div>
  </section>

  <!-- ============ DEVICES ============ -->
  <section class="page" id="page-devices">
    <h1>Devices<span class="sub"><span id="devCount2">…</span> — raw channel telemetry from both hubs.</span></h1>
    <div class="card">
      <table><thead><tr><th>Hub</th><th>Channel</th><th>Device</th><th>RPM</th><th>Duty</th><th>Type</th></tr></thead>
      <tbody id="devices2"></tbody></table>
    </div>
  </section>

</main>
<div id="toast"></div>
<script>
const $ = id => document.getElementById(id);
const MODES = ["Off","Manual","Curve"];
const f = (n,d=1) => n == null ? "—" : (Math.round(n*Math.pow(10,d))/Math.pow(10,d)).toString();
let cfg = null;

/* ---- nav ---- */
document.querySelectorAll(".navbtn").forEach(b => b.onclick = () => {
  document.querySelectorAll(".navbtn").forEach(x => x.classList.remove("active"));
  document.querySelectorAll(".page").forEach(x => x.classList.remove("active"));
  b.classList.add("active");
  $("page-" + b.dataset.page).classList.add("active");
});

/* ---- toast ---- */
function toast(msg, err=false){
  const t = $("toast"); t.textContent = msg; t.className = err ? "show err" : "show";
  clearTimeout(t._h); t._h = setTimeout(() => t.className = "", 2600);
}

/* ---- config load / apply ---- */
const hx = h => ("0"+h.toString(16)).slice(-2);
function initControls(){
  syncControls(); renderCurve();
}
/* Re-sync every control from the authoritative config (fetched on a timer), without
   stomping anything the user is focused on / dragging. */
function syncControls(){
  if (!cfg) return;
  const c = cfg.Control;
  const busy = id => document.activeElement === $(id) || $(id)._drag;
  if (!busy("fanDuty")) { $("fanDuty").value = c.ManualDutyPercent; $("fanDutyVal").textContent = c.ManualDutyPercent + " %"; }
  if (!busy("pumpDuty")) { $("pumpDuty").value = c.PumpDutyPercent; $("pumpDutyVal").textContent = c.PumpDutyPercent + " %"; }
  if (!busy("mode")) $("mode").value = c.Mode;
  if (!busy("source") && [0,2].includes(c.Source)) $("source").value = c.Source;
  if (!busy("rgbOn")) $("rgbOn").checked = c.RgbEnabled;
  if (!busy("rgbColor")) $("rgbColor").value = "#"+hx(c.RgbR)+hx(c.RgbG)+hx(c.RgbB);
  if (!busy("rgbBright")) { $("rgbBright").value = c.RgbBrightness; $("rgbBrightVal").textContent = c.RgbBrightness + " %"; }
  if (!busy("lcdMode")) $("lcdMode").value = c.LcdScreens;
  if (!busy("lcdMetric")) $("lcdMetric").value = c.PumpScreenMetric;
  if (!busy("lcdBright")) { $("lcdBright").value = c.LcdBrightness; $("lcdBrightVal").textContent = c.LcdBrightness + " %"; }
  updateRgbPreview();
  refreshControlNotes();
}
["fanDuty","pumpDuty","rgbBright","lcdBright"].forEach(id => {
  const el = $(id);
  el.addEventListener("pointerdown", () => el._drag = true);
  window.addEventListener("pointerup", () => el._drag = false);
});
function apply(patch){
  if (!cfg) return;
  // one POST per user action (sliders fire on release, colours are a single batched patch) —
  // no debounce: a dropped POST would leave the UI showing a value the server never got
  fetch("/config.json", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify(patch) })
    .then(r => r.json()).then(j => {
      if (!j.ok) throw new Error(j.error || "rejected");
      // re-sync the local copy from the server's CLAMPED values (pumpDuty=10 -> 50 etc.),
      // so enable/disable state reacts immediately (e.g. mode -> Manual unlocks the slider)
      const c = cfg.Control;
      (j.applied||[]).forEach(a => {
        const i = a.indexOf("="), k = a.slice(0, i), v = a.slice(i + 1);
        switch (k) {
          case "mode": c.Mode = +v; break;
          case "fanDuty": c.ManualDutyPercent = +v; break;
          case "pumpDuty": c.PumpDutyPercent = +v; break;
          case "source": c.Source = +v; break;
          case "rgbEnabled": c.RgbEnabled = v === "True"; break;
          case "rgbR": c.RgbR = +v; break;
          case "rgbG": c.RgbG = +v; break;
          case "rgbB": c.RgbB = +v; break;
          case "rgbBrightness": c.RgbBrightness = +v; break;
          case "lcdScreens": c.LcdScreens = +v; break;
          case "lcdBrightness": c.LcdBrightness = +v; break;
          case "pumpScreenMetric": c.PumpScreenMetric = +v; break;
        }
      });
      syncControls();
      const parts = (j.applied||[]).map(a => a.replace("="," = "));
      toast(parts.length ? "Applied: " + parts.join(", ") : "No change");
    })
    .catch(e => toast("Failed: " + e.message, true));
}

/* ---- controls wiring (fires on release / selection, not on every drag tick) ---- */
$("mode").onchange = e => apply({mode: +e.target.value});
$("fanDuty").onchange = e => apply({fanDuty: +e.target.value});
$("fanDuty").oninput  = e => $("fanDutyVal").textContent = e.target.value + " %";
$("pumpDuty").onchange = e => apply({pumpDuty: +e.target.value});
$("pumpDuty").oninput  = e => $("pumpDutyVal").textContent = e.target.value + " %";
$("source").onchange = e => apply({source: +e.target.value});
$("rgbOn").onchange = e => apply({rgbEnabled: e.target.checked});
$("rgbColor").onchange = e => { const v = e.target.value;
  apply({rgbR: parseInt(v.slice(1,3),16), rgbG: parseInt(v.slice(3,5),16), rgbB: parseInt(v.slice(5,7),16)}); };
$("rgbBright").onchange = e => apply({rgbBrightness: +e.target.value});
$("rgbBright").oninput  = e => $("rgbBrightVal").textContent = e.target.value + " %";
$("lcdMode").onchange = e => apply({lcdScreens: +e.target.value});
$("lcdMetric").onchange = e => apply({pumpScreenMetric: +e.target.value});
$("lcdBright").onchange = e => apply({lcdBrightness: +e.target.value});
$("lcdBright").oninput  = e => $("lcdBrightVal").textContent = e.target.value + " %";

/* ---- swatches ---- */
const SWATCHES = [["8000ff","Purple"],["ff00ff","Magenta"],["0066ff","Blue"],["00e5ff","Cyan"],["00ff66","Green"],["ff8800","Orange"],["ff2222","Red"],["ffffff","White"]];
SWATCHES.forEach(([hex,name]) => {
  const d = document.createElement("div");
  d.className = "swatch"; d.style.background = "#"+hex; d.title = name;
  d.onclick = () => { $("rgbColor").value = "#"+hex;
    apply({rgbR: parseInt(hex.slice(0,2),16), rgbG: parseInt(hex.slice(2,4),16), rgbB: parseInt(hex.slice(4,6),16)}); };
  $("swatches").appendChild(d);
});
function updateRgbPreview(){
  const v = $("rgbColor").value, b = $("rgbBright").value/100;
  const r = Math.round(parseInt(v.slice(1,3),16)*b), g = Math.round(parseInt(v.slice(3,5),16)*b), bl = Math.round(parseInt(v.slice(5,7),16)*b);
  $("rgbPreview").style.background = `rgb(${r},${g},${bl})`;
  $("rgbHex").textContent = v.toUpperCase();
}
$("rgbColor").oninput = updateRgbPreview; $("rgbBright").oninput = updateRgbPreview;

/* ---- live refresh ---- */
function deviceRows(s, withDuty){
  return (s.Devices||[]).map(d => {
    const type = d.IsPump ? "pump" : (d.IsScreen ? "screen" : "fan");
    let duty = "";
    if (withDuty) {
      if (type === "pump" && s.PumpDutyPercent != null) duty = s.PumpDutyPercent + "%";
      else if (type === "fan" && s.Mode !== 0) duty = s.TargetDutyPercent + "%";
    }
    return `<tr><td class="dim">${(d.HubSerial||"").slice(0,8)}…</td><td>${d.Channel}</td><td>${d.Name||"—"}</td>
      <td>${d.Rpm == null ? "—" : d.Rpm}</td>
      ${withDuty ? `<td class="dim">${duty}</td>` : ""}
      <td><span class="pill ${type}">${type}</span></td></tr>`;
  }).join("");
}
function deviceCounts(s){
  const ds = s.Devices||[];
  const fans = ds.filter(d => !d.IsPump && !d.IsScreen).length;
  const pumps = ds.filter(d => d.IsPump).length;
  const screens = ds.filter(d => d.IsScreen).length;
  return `${fans} fan${fans===1?"":"s"} · ${pumps} pump${pumps===1?"":"s"} · ${screens} screen${screens===1?"":"s"}`;
}
function renderCurve(){
  if (!cfg) return;
  const pts = (cfg.Control.CurvePoints||[]).map(p => `<tr><td>${f(p.TemperatureC,0)} °C</td><td>${p.DutyPercent} %</td></tr>`).join("");
  $("curvePts").innerHTML = pts || "<tr><td class='dim'>no points</td><td></td></tr>";
}
function refreshControlNotes(){
  if (!cfg) return;
  const m = cfg.Control.Mode;
  $("fanDuty").disabled = m !== 1;
  $("fanDuty").style.opacity = m === 1 ? 1 : .4;
  $("source").disabled = m !== 2;
  $("source").style.opacity = m === 2 ? 1 : .4;
  $("modeEcho").textContent = MODES[m];
  $("fanNote").textContent = m === 2 ? "Duty follows the curve from the selected source temperature."
    : m === 1 ? "Fixed duty applied to all fan channels."
    : "DeviceMaster leaves the hubs alone — their firmware rules apply.";
  $("rgbOnEcho").textContent = cfg.Control.RgbEnabled ? "on" : "off";
}
function tick(){
  fetch("/status.json").then(r => r.json()).then(s => {
    $("dot").className = "dot " + (s.Running ? "ok" : "bad");
    $("foot").textContent = s.Running ? "loop running" : "loop down";
    const devs = s.Devices||[];
    const pump = devs.find(d => d.IsPump);
    const fans = devs.filter(d => !d.IsPump && !d.IsScreen);
    const deg = (v, d) => v == null ? "—" : v.toFixed(d) + "°";
    if (!s.Running) { $("dashStatus").textContent = "Control off — devices follow their own hardware/firmware curves."; }
    else $("dashStatus").textContent = s.FailsafeActive ? `${s.SourceName} unavailable — FAILSAFE, all fans at 100%`
      : s.Mode === 1 ? `Manual — all fans at ${s.TargetDutyPercent}%`
      : s.Mode === 2 ? `Curve on ${s.SourceName} → fans ${s.TargetDutyPercent}%`
      : "Control off — devices follow their own hardware/firmware curves.";
    $("dashStatus").className = "dashstatus" + (s.FailsafeActive ? " warn" : "");
    $("coolant").textContent = deg(s.CoolantTemperatureC, 1);
    $("pump").textContent = pump && pump.Rpm != null ? pump.Rpm : "—";
    $("pumpSub").textContent = pump ? `rpm @ ${s.PumpDutyPercent}%` : "not detected";
    $("fans").textContent = `${fans.filter(x => x.Rpm != null).length}/${fans.length}`;
    const silent = fans.filter(x => x.Rpm == null).length;
    $("fansSub").textContent = silent ? `${silent} without rpm` : "all reporting";
    $("duty").textContent = s.TargetDutyPercent + "%";
    $("dutySub").textContent = s.Mode === 1 ? "manual" : "from curve";
    $("fanGrid").innerHTML = fans.map(x => `<div class="fanpill"><span class="n">Corsair · ${x.Name||"fan"}</span>`
      + `<span class="r${x.Rpm == null ? " none" : ""}">${x.Rpm == null ? "no rpm" : x.Rpm + " rpm"}</span></div>`).join("");
    $("dashWarnings").innerHTML = (s.Warnings||[]).filter(w => !String(w).includes("LED registry"))
      .map(w => `<div class="dashwarn">⚠&nbsp; ${String(w).replace(/</g,"&lt;")}</div>`).join("");
    // CPU and GPU sections: same figures and formatting as UpdateProcessorSections() on the desktop
    const pct = v => v == null ? "—" : Math.round(v) + "%", watts = v => v == null ? "—" : Math.round(v) + " W",
          gb = v => v == null ? "—" : v.toFixed(1) + " GB";
    $("cpuName").textContent = s.CpuName || "processor";
    $("cpuTemp").textContent = deg(s.CpuTemperatureC, 0); $("cpuTempSub").textContent = "package";
    $("cpuLoad").textContent = pct(s.CpuLoadPercent);     $("cpuLoadSub").textContent = "all cores";
    $("cpuPower").textContent = watts(s.CpuPowerW);       $("cpuPowerSub").textContent = "package";
    $("cpuRam").textContent = gb(s.RamUsedGb);            $("cpuRamSub").textContent = s.RamTotalGb != null ? `of ${Math.round(s.RamTotalGb)} GB` : "in use";
    $("gpuName").textContent = s.GpuName || "graphics card";
    $("gpuTemp").textContent = deg(s.GpuTemperatureC, 0); $("gpuTempSub").textContent = "core";
    $("gpuLoad").textContent = pct(s.GpuLoadPercent);     $("gpuLoadSub").textContent = "utilization";
    $("gpuPower").textContent = watts(s.GpuPowerW);       $("gpuPowerSub").textContent = "board";
    $("gpuVram").textContent = gb(s.VramUsedGb);          $("gpuVramSub").textContent = s.VramTotalGb != null ? `of ${Math.round(s.VramTotalGb)} GB` : "in use";
    $("pumpRpm").textContent = pump && pump.Rpm != null ? pump.Rpm + " rpm" : "—";
    $("devCount2").textContent = deviceCounts(s);
    $("devices2").innerHTML = deviceRows(s, true);
  }).catch(() => { $("dot").className = "dot bad"; $("foot").textContent = "dashboard offline"; });
}
tick(); setInterval(tick, 2000);
const pullConfig = () => fetch("/config.json").then(r => r.json()).then(c => { cfg = c; syncControls(); }).catch(() => {});
pullConfig(); setInterval(pullConfig, 5000);
fetch("/config.json").then(r => r.json()).then(c => { cfg = c; initControls(); }).catch(() => {});
</script>
</body>
</html>
""";
}