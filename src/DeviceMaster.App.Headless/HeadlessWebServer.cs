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

    public HeadlessWebServer(HeadlessLoop loop, int port, Action<string> log)
    {
        _loop = loop;
        _port = port;
        _log = log;
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
        ctx.Response.StatusCode = path switch
        {
            "/" or "/index.html" => 200,
            "/status.json" => 200,
            _ => 404,
        };

        if (ctx.Response.StatusCode != 200)
        {
            Write(ctx, "text/plain; charset=utf-8", "not found");
            return;
        }

        if (path == "/status.json")
        {
            var json = JsonSerializer.Serialize(_loop.Status, new JsonSerializerOptions { WriteIndented = false });
            Write(ctx, "application/json; charset=utf-8", json);
        }
        else
        {
            Write(ctx, "text/html; charset=utf-8", DashboardHtml);
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

    /// <summary>Single-page dashboard: auto-refreshes /status.json every 2 s.</summary>
    private const string DashboardHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>DeviceMaster</title>
<style>
  :root { --bg:#0e1116; --card:#161b23; --line:#232b37; --txt:#dfe6ef; --dim:#8b98a9;
          --purple:#8000ff; --ok:#3fb96f; --warn:#e5b341; --bad:#e5534b; }
  * { box-sizing:border-box; margin:0; }
  body { background:var(--bg); color:var(--txt); font:14px/1.45 system-ui, sans-serif; padding:20px; }
  h1 { font-size:18px; font-weight:600; } h1 small { color:var(--dim); font-weight:400; }
  .row { display:flex; flex-wrap:wrap; gap:12px; margin-top:14px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:14px 16px; flex:1 1 150px; min-width:140px; }
  .card .k { color:var(--dim); font-size:12px; text-transform:uppercase; letter-spacing:.06em; }
  .card .v { font-size:26px; font-weight:650; margin-top:4px; }
  .card .s { color:var(--dim); font-size:12px; margin-top:2px; }
  table { width:100%; border-collapse:collapse; margin-top:6px; }
  th, td { text-align:left; padding:6px 10px; border-top:1px solid var(--line); font-size:13px; }
  th { color:var(--dim); font-weight:500; font-size:12px; text-transform:uppercase; letter-spacing:.05em; }
  .panel { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:14px 16px; margin-top:14px; }
  .panel h2 { font-size:13px; color:var(--dim); text-transform:uppercase; letter-spacing:.06em; margin-bottom:4px; }
  .badge { display:inline-block; padding:2px 10px; border-radius:99px; font-size:12px; font-weight:600; }
  .badge.ok { background:rgba(63,185,111,.15); color:var(--ok); }
  .badge.bad { background:rgba(229,83,75,.18); color:var(--bad); }
  #failsafe { display:none; margin-top:14px; padding:12px 16px; border-radius:10px;
              background:rgba(229,83,75,.14); border:1px solid var(--bad); color:var(--bad); font-weight:600; }
  .warn { color:var(--warn); font-size:13px; margin-top:4px; }
  .dot { display:inline-block; width:9px; height:9px; border-radius:50%; background:var(--ok); margin-right:6px; }
  .dot.down { background:var(--bad); }
  .purple { color:var(--purple); }
</style>
</head>
<body>
  <h1>DeviceMaster <small id="updated">· connecting…</small></h1>
  <div id="failsafe">FAILSAFE ACTIVE — fans at 100 % (sensor unavailable)</div>
  <div id="warnings"></div>
  <div class="row" id="temps"></div>
  <div class="row">
    <div class="card"><div class="k">Mode</div><div class="v" id="mode">—</div><div class="s" id="modeSub"></div></div>
    <div class="card"><div class="k">Target fan duty</div><div class="v" id="duty">—</div><div class="s">curve → RPM below</div></div>
    <div class="card"><div class="k">GPU load</div><div class="v" id="gpuLoad">—</div><div class="s" id="gpuPower"></div></div>
    <div class="card"><div class="k">VRAM</div><div class="v" id="vram">—</div><div class="s">of the card's total</div></div>
  </div>
  <div class="panel">
    <h2>Devices <span class="purple" id="leds"></span></h2>
    <table>
      <thead><tr><th>Hub</th><th>Channel</th><th>Device</th><th>RPM</th><th>Duty</th><th>Type</th></tr></thead>
      <tbody id="devices"></tbody>
    </table>
  </div>
<script>
const $ = id => document.getElementById(id);
const f = (v, d = 0) => (v == null ? "—" : Number(v).toFixed(d));
async function tick() {
  let s;
  try { s = await (await fetch("status.json")).json(); }
  catch { $("updated").textContent = "· no data"; $("updated").classList.add("down"); return; }
  $("updated").textContent = "· updated " + new Date().toLocaleTimeString();
  $("failsafe").style.display = s.FailsafeActive ? "block" : "none";
  $("warnings").innerHTML = (s.Warnings || []).map(w => `<div class="warn">⚠ ${w}</div>`).join("");
  $("temps").innerHTML = [
    ["Coolant", s.CoolantTemperatureC, "°C"], ["CPU", s.CpuTemperatureC, "°C"], ["GPU", s.GpuTemperatureC, "°C"]
  ].map(([k, v, u]) => `<div class="card"><div class="k">${k}</div><div class="v">${f(v, 1)}<small style="font-size:14px"> ${u}</small></div></div>`).join("");
  $("mode").textContent = s.SourceName || "—";
  $("modeSub").textContent = s.SourceTemperatureC != null ? "source " + f(s.SourceTemperatureC, 1) + " °C" : "";
  $("duty").textContent = f(s.TargetDutyPercent) + "%";
  $("gpuLoad").textContent = s.GpuLoadPercent != null ? f(s.GpuLoadPercent) + "%" : "—";
  $("gpuPower").textContent = s.GpuPowerW != null ? f(s.GpuPowerW, 0) + " W" : "";
  $("vram").textContent = s.VramUsedGb != null ? f(s.VramUsedGb, 1) + " GB" : "—";
  const rows = (s.Devices || []).map(d => `<tr>
      <td>${(d.HubSerial || "").slice(0, 8)}…</td><td>${d.Channel}</td><td>${d.Name || "—"}</td>
      <td>${d.Rpm == null ? "—" : d.Rpm}</td>
      <td>${d.AppliedDutyPercent == null ? "" : d.AppliedDutyPercent + "%"}</td>
      <td>${d.IsPump ? "pump" : "fan"}</td></tr>`).join("");
  $("devices").innerHTML = rows;
  const fans = (s.Devices || []).filter(d => !d.IsPump).length;
  const pumps = (s.Devices || []).filter(d => d.IsPump).length;
  $("leds").textContent = fans + " fans · " + pumps + " pump" + (pumps === 1 ? "" : "s");
}
tick(); setInterval(tick, 2000);
</script>
</body>
</html>
""";
}
