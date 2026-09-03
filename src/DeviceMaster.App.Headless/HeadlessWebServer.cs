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
<style>
:root {
  --bg:#0b0e16; --card:#141a2b; --card2:#171d33; --line:#232a45; --line2:#323c5e;
  --text:#e6ecfb; --dim:#94a0c2; --faint:#646f93; --accent:#79b0ff; --accent2:#a9c8ff;
  --good:#5fe0c0; --warn:#ffd34d; --danger:#ff5d5d; --ink:#0a1228; --inset:#0f1322; --tile:#16203a;
  --grad:linear-gradient(90deg,#22d3ee,#a855f7);
}
* { box-sizing:border-box; margin:0; }
html,body { height:100%; }
body { background:var(--bg); color:var(--text); font:14px/1.45 "Segoe UI",system-ui,sans-serif; display:flex; }
.mono { font-family:Consolas,monospace; }
/* ---- sidebar ---- */
aside { width:212px; flex:none; border-right:1px solid var(--line); padding:20px 14px; position:sticky; top:0; height:100vh; display:flex; flex-direction:column; }
.brand { font-size:16px; font-weight:600; padding:0 12px 2px; }
.brandsub { font-size:11px; color:var(--faint); padding:0 12px 18px; }
.navbtn { display:flex; align-items:center; gap:10px; width:100%; background:transparent; border:0; color:var(--dim);
  font:13.5px "Segoe UI",sans-serif; padding:10px 12px; margin:2px 0; border-radius:10px; cursor:pointer; text-align:left; }
.navbtn .glyph { width:26px; text-align:center; font-size:15px; color:var(--faint); }
.navbtn:hover { background:var(--card2); color:var(--text); }
.navbtn.active { background:var(--card2); color:var(--text); box-shadow:inset 0 0 0 1px var(--line2); }
.navbtn.active .glyph { color:var(--accent); }
.aside-foot { margin-top:auto; padding:12px; font-size:11.5px; color:var(--faint); display:flex; align-items:center; gap:8px; }
.dot { width:8px; height:8px; border-radius:50%; background:var(--faint); flex:none; }
.dot.ok { background:var(--good); } .dot.bad { background:var(--danger); }
/* ---- main ---- */
main { flex:1; padding:26px 28px 30px 24px; min-width:0; }
.page { display:none; } .page.active { display:block; }
h1 { font-size:17px; font-weight:600; margin-bottom:4px; }
.pagedesc { color:var(--dim); font-size:12.5px; margin-bottom:18px; }
.card { background:var(--card); border:1px solid var(--line); border-radius:15px; padding:20px 18px; margin-bottom:16px; }
.cardhead { display:flex; align-items:center; gap:12px; margin-bottom:15px; }
.tile { width:34px; height:34px; border-radius:10px; background:var(--tile); border:1px solid var(--line2);
  display:flex; align-items:center; justify-content:center; color:var(--accent); font-size:16px; flex:none; }
.cardtitle { font-size:14px; font-weight:600; }
.cardsub { font-size:11.5px; color:var(--faint); }
.grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(168px,1fr)); gap:12px; }
.stat { background:var(--card2); border:1px solid var(--line); border-radius:12px; padding:13px 14px; }
.stat .k { font-size:11px; text-transform:uppercase; letter-spacing:.4px; color:var(--dim); margin-bottom:5px; }
.stat .v { font-size:24px; font-weight:600; font-family:Consolas,monospace; }
.stat .u { font-size:12px; color:var(--faint); margin-left:3px; }
.stat .sub { font-size:11.5px; color:var(--faint); margin-top:3px; min-height:14px; }
.v.good { color:var(--good); } .v.warn { color:var(--warn); } .v.bad { color:var(--danger); }
/* banner */
.banner { display:none; border-radius:12px; padding:13px 16px; margin-bottom:16px; font-size:13.5px; border:1px solid; }
.banner.bad { display:block; background:rgba(255,93,93,.1); border-color:var(--danger); color:var(--danger); }
.banner.warn { display:block; background:rgba(255,211,77,.08); border-color:var(--warn); color:var(--warn); }
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
.toggle input:checked + .tk { background:rgba(121,176,255,.25); border-color:var(--accent); }
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
.pill.fan { color:var(--accent); border-color:rgba(121,176,255,.4); }
.pill.pump { color:var(--good); border-color:rgba(95,224,192,.4); }
.pill.screen { color:#d9a6ff; border-color:rgba(217,166,255,.45); }
/* toast */
#toast { position:fixed; right:22px; bottom:22px; background:var(--card2); border:1px solid var(--line2); border-radius:10px;
  padding:11px 16px; font-size:13px; color:var(--text); opacity:0; transform:translateY(8px); transition:.2s; pointer-events:none; }
#toast.show { opacity:1; transform:none; }
#toast.err { border-color:var(--danger); color:var(--danger); }
@media (max-width:760px){ aside{width:64px} .brand,.brandsub,.navbtn .lbl,.aside-foot span{display:none} .navbtn{justify-content:center} }
</style>
</head>
<body>
<aside>
  <div class="brand">DeviceMaster</div>
  <div class="brandsub">headless · unraid</div>
  <button class="navbtn active" data-page="dashboard"><span class="glyph">⌂</span><span class="lbl">Dashboard</span></button>
  <button class="navbtn" data-page="cooling"><span class="glyph">❄</span><span class="lbl">Cooling</span></button>
  <button class="navbtn" data-page="lighting"><span class="glyph">◈</span><span class="lbl">Lighting</span></button>
  <button class="navbtn" data-page="screens"><span class="glyph">▣</span><span class="lbl">Screens</span></button>
  <button class="navbtn" data-page="devices"><span class="glyph">⚙</span><span class="lbl">Devices</span></button>
  <div class="aside-foot"><div class="dot" id="dot"></div><span id="foot">connecting…</span></div>
</aside>
<main>

  <!-- ============ DASHBOARD ============ -->
  <section class="page active" id="page-dashboard">
    <h1>Dashboard</h1>
    <div class="pagedesc">Live loop status — auto-refreshes every 2 s.</div>
    <div class="banner bad" id="failsafe">⚠ Failsafe active — sensor read failed, hardware driven to 100 % duty.</div>
    <div class="banner warn" id="warnbox" style="display:none"></div>
    <div class="grid" style="margin-bottom:16px">
      <div class="stat"><div class="k">Coolant</div><div class="v" id="coolant">—</div><div class="sub" id="coolantSub"></div></div>
      <div class="stat"><div class="k">CPU</div><div class="v" id="cpu">—</div><div class="sub">core temperature</div></div>
      <div class="stat"><div class="k">GPU</div><div class="v" id="gpu">—</div><div class="sub" id="gpuSub"></div></div>
      <div class="stat"><div class="k">Target duty</div><div class="v" id="duty">—</div><div class="sub" id="dutySub"></div></div>
    </div>
    <div class="card">
      <div class="cardhead"><div class="tile">▤</div><div><div class="cardtitle">GPU</div><div class="cardsub">4090 Strix — via host sensor file</div></div></div>
      <div class="grid">
        <div class="stat"><div class="k">Load</div><div class="v" id="gpuLoad">—</div></div>
        <div class="stat"><div class="k">Power</div><div class="v" id="gpuPower">—</div></div>
        <div class="stat"><div class="k">VRAM</div><div class="v" id="vram">—</div></div>
        <div class="stat"><div class="k">Mode</div><div class="v" id="mode" style="font-size:16px">—</div><div class="sub" id="modeSub"></div></div>
      </div>
    </div>
    <div class="card">
      <div class="cardhead"><div class="tile">▣</div><div><div class="cardtitle">Devices</div><div class="cardsub" id="devCount">…</div></div></div>
      <table><thead><tr><th>Hub</th><th>Ch</th><th>Device</th><th>RPM</th><th>Type</th></tr></thead>
      <tbody id="devices"></tbody></table>
    </div>
  </section>

  <!-- ============ COOLING ============ -->
  <section class="page" id="page-cooling">
    <h1>Cooling</h1>
    <div class="pagedesc">Fan and pump control. Changes apply within ~1 s (config hot-reload).</div>
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
    <h1>Lighting</h1>
    <div class="pagedesc">Hub RGB (70 addressable LEDs across both hubs).</div>
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
    <h1>Screens</h1>
    <div class="pagedesc">Pump LCD (XD6 ELITE 480×480). Frames re-send every 10 s so the panel never reverts.</div>
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
    <h1>Devices</h1>
    <div class="pagedesc"><span id="devCount2">…</span> — raw channel telemetry from both hubs.</div>
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
let cfg = null, lastApplied = 0;

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
function initControls(){
  const c = cfg.Control;
  $("mode").value = c.Mode;
  $("fanDuty").value = c.ManualDutyPercent; $("fanDutyVal").textContent = c.ManualDutyPercent + " %";
  $("pumpDuty").value = c.PumpDutyPercent;   $("pumpDutyVal").textContent = c.PumpDutyPercent + " %";
  if ([0,2].includes(c.Source)) $("source").value = c.Source;
  $("rgbOn").checked = c.RgbEnabled; $("rgbOnEcho").textContent = c.RgbEnabled ? "on" : "off";
  const hx = h => ("0"+h.toString(16)).slice(-2);
  $("rgbColor").value = "#"+hx(c.RgbR)+hx(c.RgbG)+hx(c.RgbB);
  $("rgbBright").value = c.RgbBrightness; $("rgbBrightVal").textContent = c.RgbBrightness + " %";
  $("lcdMode").value = c.LcdScreens; $("lcdMetric").value = c.PumpScreenMetric;
  $("lcdBright").value = c.LcdBrightness; $("lcdBrightVal").textContent = c.LcdBrightness + " %";
  updateRgbPreview(); refreshControlNotes(); renderCurve();
}
function apply(patch){
  if (!cfg) return;
  const t = Date.now(); if (t - lastApplied < 300) return; lastApplied = t; // debounce slider spam
  fetch("/config.json", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify(patch) })
    .then(r => r.json()).then(j => {
      if (!j.ok) throw new Error(j.error || "rejected");
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
    $("failsafe").style.display = s.FailsafeActive ? "block" : "none";
    const warn = (s.Warnings||[]).filter(w => !String(w).includes("LED registry"));
    $("warnbox").style.display = warn.length ? "block" : "none";
    $("warnbox").textContent = warn.join(" · ");
    $("coolant").textContent = s.CoolantTemperatureC != null ? f(s.CoolantTemperatureC) + "" : "—";
    $("coolant").className = "v" + (s.CoolantTemperatureC > 45 ? " bad" : s.CoolantTemperatureC > 35 ? " warn" : "");
    $("coolantSub").textContent = "°C · loop sensor";
    $("cpu").textContent = s.CpuTemperatureC != null ? s.CpuTemperatureC + "" : "—";
    $("cpu").className = "v" + (s.CpuTemperatureC > 85 ? " bad" : s.CpuTemperatureC > 70 ? " warn" : "");
    $("gpu").textContent = s.GpuTemperatureC != null ? s.GpuTemperatureC + "" : "—";
    $("gpu").className = "v" + (s.GpuTemperatureC > 80 ? " bad" : s.GpuTemperatureC > 65 ? " warn" : "");
    $("gpuSub").textContent = "°C · 4090 Strix";
    $("duty").textContent = s.TargetDutyPercent + "%";
    $("duty").className = "v" + (s.FailsafeActive ? " bad" : "");
    $("dutySub").textContent = MODES[s.Mode] + (s.Mode === 2 ? " · " + s.SourceName : "");
    $("gpuLoad").textContent = s.GpuLoadPercent != null ? s.GpuLoadPercent + "%" : "—";
    $("gpuPower").textContent = s.GpuPowerW != null ? f(s.GpuPowerW,0) + " W" : "—";
    $("vram").textContent = s.VramUsedGb != null ? f(s.VramUsedGb,1) + " GB" : "—";
    $("mode").textContent = MODES[s.Mode];
    $("modeSub").textContent = s.FailsafeActive ? "FAILSAFE" : (s.Warnings||[]).length ? "warnings" : "nominal";
    const pump = (s.Devices||[]).find(d => d.IsPump);
    $("pumpRpm").textContent = pump && pump.Rpm != null ? pump.Rpm + " rpm" : "—";
    $("devCount").textContent = deviceCounts(s);
    $("devCount2").textContent = deviceCounts(s);
    $("devices").innerHTML = deviceRows(s, false);
    $("devices2").innerHTML = deviceRows(s, true);
    if (cfg) {
      // don't stomp the user while they're dragging — only sync values when not focused
      if (document.activeElement !== $("fanDuty")) $("fanDuty").value = cfg.Control.ManualDutyPercent;
      if (document.activeElement !== $("pumpDuty")) $("pumpDuty").value = cfg.Control.PumpDutyPercent;
    }
  }).catch(() => { $("dot").className = "dot bad"; $("foot").textContent = "dashboard offline"; });
}
tick(); setInterval(tick, 2000);
fetch("/config.json").then(r => r.json()).then(c => { cfg = c; initControls(); }).catch(() => {});
</script>
</body>
</html>
""";
}