namespace DeviceMaster.App.Headless;

/// <summary>
/// The fleet's wall display (#49): one full-screen page, no scrolling, no controls — every machine,
/// storage across the fleet, containers and downloads. Built for a spare screen running a kiosk
/// browser; everything scales with the viewport.
/// </summary>
public static class HubDisplayPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Home · DeviceMaster</title>
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'%3E%3Cdefs%3E%3ClinearGradient id='g' x1='0' y1='1' x2='1' y2='0'%3E%3Cstop offset='0' stop-color='%234f8cff'/%3E%3Cstop offset='.5' stop-color='%23d06cd8'/%3E%3Cstop offset='1' stop-color='%23ff7a45'/%3E%3C/linearGradient%3E%3C/defs%3E%3Crect x='2' y='2' width='60' height='60' rx='14' fill='url(%23g)'/%3E%3C/svg%3E">
<style>
:root { --bg:#0b0d11; --card:#151820; --line:#252a34; --text:#eef0f4; --dim:#a0a6b3; --faint:#6b7180;
  --good:#34d399; --warn:#fbbf24; --bad:#ef4444; --a1:#4f8cff; --a2:#d06cd8; --a3:#ff7a45; }
* { box-sizing:border-box; margin:0; }
html,body { height:100%; overflow:hidden; background:var(--bg); color:var(--text); font-family:"Segoe UI",system-ui,sans-serif; user-select:none; -webkit-tap-highlight-color:transparent; }
body { display:grid; grid-template-rows:auto 1.15fr 1fr; gap:1.6vh; padding:2vh 2vw; height:100vh; }
header { display:flex; align-items:center; gap:2vw; white-space:nowrap; } /* one row: title, lights, then date + time together */
header .title { font-size:3.2vh; font-weight:700; background:linear-gradient(90deg,var(--a1),var(--a2),var(--a3)); -webkit-background-clip:text; color:transparent; }
header .when { margin-left:auto; display:flex; align-items:baseline; gap:1vw; }
header .clock { font-size:4.4vh; font-weight:700; font-variant-numeric:tabular-nums; } header .clock small { font-size:2.2vh; font-weight:600; color:var(--dim); margin-left:.3vw; }
header .date { font-size:2.2vh; color:var(--dim); }
.row { display:grid; gap:1.4vw; min-height:0; }
.machines { grid-template-columns:repeat(var(--n,4), minmax(0,1fr)); }
.lower { grid-template-columns:1.3fr 0.8fr 1.1fr; }
.card { background:var(--card); border:1px solid var(--line); border-radius:1.6vh; padding:1.8vh 1.4vw; min-width:0; min-height:0; display:flex; flex-direction:column; overflow:hidden; }
.card.off .big, .card.off .sub, .card.off .name span:last-child { opacity:.45; }
.card.bad { border-color:#5b1d20; box-shadow:0 0 0 1px #5b1d20 inset; }
.name { display:flex; align-items:center; gap:.8vw; font-size:2.6vh; font-weight:700; }
.dot { width:1.4vh; height:1.4vh; border-radius:50%; background:var(--faint); flex:none; }
.dot.ok { background:var(--good); box-shadow:0 0 1.2vh var(--good); } .dot.warn { background:var(--warn); } .dot.bad { background:var(--bad); }
.sub { font-size:1.6vh; color:var(--faint); margin-top:.3vh; }
.big { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:.8vw; margin-top:auto; }
.k { font-size:1.4vh; font-weight:600; letter-spacing:.06em; text-transform:uppercase; color:var(--faint); }
.v { font-size:4.6vh; font-weight:700; line-height:1.1; white-space:nowrap; font-variant-numeric:tabular-nums; }
.v small { font-size:2vh; color:var(--dim); font-weight:600; }
.alert { font-size:1.5vh; color:var(--warn); margin-top:.6vh; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.alert.bad { color:#fca5a5; }
h2 { font-size:1.6vh; font-weight:600; letter-spacing:.08em; text-transform:uppercase; color:var(--faint); margin-bottom:1.2vh; }
.disks { display:flex; flex-direction:column; gap:1.1vh; overflow:hidden; }
.disk .lbl { display:flex; font-size:1.7vh; color:var(--dim); } .disk .lbl b { color:var(--text); font-weight:600; margin-right:.5vw; } .disk .lbl span { margin-left:auto; font-variant-numeric:tabular-nums; }
.bar { height:1.1vh; border-radius:1vh; background:#0b0d11; border:1px solid var(--line); overflow:hidden; margin-top:.4vh; }
.bar i { display:block; height:100%; background:linear-gradient(90deg,var(--a1),var(--a2)); }
.bar i.warn { background:var(--warn); } .bar i.bad { background:var(--bad); }
.hero { font-size:9vh; font-weight:800; line-height:1; font-variant-numeric:tabular-nums; }
.hero small { font-size:3vh; color:var(--dim); font-weight:600; }
.list { font-size:1.6vh; color:var(--dim); margin-top:1vh; overflow:hidden; }
.speed { font-size:7vh; font-weight:800; line-height:1; background:linear-gradient(90deg,var(--a1),var(--a2),var(--a3)); -webkit-background-clip:text; color:transparent; font-variant-numeric:tabular-nums; }
.dl { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:.8vw; margin-top:auto; }
.ell { white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.heat { margin:auto 0 1.5vh; } .heat .hv { font-size:7vh; font-weight:800; line-height:1.05; font-variant-numeric:tabular-nums; }
/* controls: touch-sized */
.btn { background:#0b0d11; border:1px solid var(--line); color:var(--dim); border-radius:99px; padding:.9vh 1.2vw; font:600 1.7vh "Segoe UI",system-ui,sans-serif; cursor:pointer; }
.btn:active { transform:scale(.97); } .btn.on { background:linear-gradient(90deg,var(--a1),var(--a2),var(--a3)); color:#fff; border-color:transparent; }
.btn.danger { color:#fca5a5; border-color:#5b1d20; } .btn.go { color:var(--good); border-color:#1f5a46; }
.pw { margin-left:auto; font-size:1.6vh; padding:.6vh 1vw; }
.lights { display:flex; align-items:center; gap:.6vw; margin-left:2vw; }
.bri { display:inline-flex; align-items:center; gap:.4vw; color:var(--dim); font-size:2vh; margin-left:.6vw; }
.bri input { width:11vw; accent-color:var(--a2); height:3vh; } .bri b { font-size:1.6vh; min-width:3.2em; color:var(--text); }
.sw { width:3.4vh; height:3.4vh; border-radius:1vh; cursor:pointer; display:inline-flex; align-items:center; justify-content:center; font-weight:900; font-size:2.2vh; line-height:1; }
.sw.on { box-shadow:0 0 0 3px var(--bg), 0 0 0 6px var(--text); transform:scale(1.15); } /* selected: a tick + a white ring, visible on every colour */
.modal { position:fixed; inset:0; background:rgba(0,0,0,.65); display:none; align-items:center; justify-content:center; z-index:9; }
.modal .box { background:var(--card); border:1px solid var(--line); border-radius:2vh; padding:3vh 3vw; min-width:34vw; text-align:center; }
.modal h3 { font-size:3vh; margin-bottom:1vh; } .modal p { font-size:1.8vh; color:var(--dim); margin-bottom:3vh; }
.modal .btns { display:flex; gap:1vw; justify-content:center; } .modal .btn { font-size:2.2vh; padding:1.4vh 2.4vw; }
.toast { position:fixed; left:50%; bottom:3vh; transform:translateX(-50%); background:var(--card); border:1px solid var(--line); border-radius:99px; padding:1.2vh 2vw; font-size:1.8vh; display:none; z-index:8; }
.stale { position:fixed; right:2vw; bottom:1.2vh; font-size:1.4vh; color:var(--bad); display:none; }
</style>
</head>
<body>
<header><span class="title">DeviceMaster</span>
  <span class="lights" id="lights"></span><span class="when"><span class="date" id="date"></span><span class="clock" id="clock">--:--</span></span></header>
<section class="row machines" id="machines"></section>
<section class="row lower">
  <div class="card"><h2>Storage</h2><div class="disks" id="disks"></div></div>
  <div class="card"><h2>Containers</h2><div id="docker"></div></div>
  <div class="card"><h2>Downloads</h2><div id="sab" style="display:flex;flex-direction:column;flex:1;min-height:0"></div></div>
</section>
<div class="stale" id="stale">hub not answering</div>
<div class="modal" id="menu"><div class="box"><h3 id="menuTitle"></h3><p>A clean shutdown or restart, the same as from its own menu.</p>
  <div class="btns"><button class="btn" onclick="closeModal()">Cancel</button><button class="btn" id="menuReboot">↻ Reboot</button><button class="btn danger" id="menuOff">⏻ Shut down</button></div></div></div>
<div class="modal" id="modal"><div class="box"><h3 id="mTitle"></h3><p id="mText"></p>
  <div class="btns"><button class="btn" onclick="closeModal()">Cancel</button><button class="btn danger" id="mGo">Confirm</button></div></div></div>
<div class="toast" id="toast"></div>
<script>
const $ = id => document.getElementById(id);
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"})[c]);
const deg = v => v == null ? "—" : Math.round(v) + "°";
function up(s){ if (s == null) return "—"; const d = Math.floor(s/86400), h = Math.floor(s%86400/3600), m = Math.floor(s%3600/60); return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : `${m}m`; }
function bytes(b){ const u = ["B","KB","MB","GB","TB","PB"]; let i = 0; while (b >= 1024 && i < u.length-1) { b /= 1024; i++; } return (b >= 100 ? b.toFixed(0) : b.toFixed(1)) + " " + u[i]; }
function tick(){ // date beside the time, both on the one header row
  const d = new Date(), parts = d.toLocaleTimeString([], {hour:"numeric", minute:"2-digit"}).split(/\s+/); // en-AU puts a narrow no-break space before "am"
  $("clock").innerHTML = esc(parts[0]) + (parts[1] ? `<small>${esc(parts[1])}</small>` : "");
  $("date").textContent = d.toLocaleDateString([], {weekday:"short", day:"numeric", month:"short"});
}
function heat(t, fans){ const p = Math.min(100, Math.max(0, (t - 20) / 70 * 100)), c = t >= 85 ? "bad" : t >= 75 ? "warn" : "";
  return `<div class="heat"><div class="k">Hottest</div><div class="hv">${Math.round(t)}°</div><div class="bar"><i class="${c}" style="width:${p.toFixed(1)}%"></i></div><div class="sub">${fans ? fans + " fan" + (fans === 1 ? "" : "s") + " under control" : ""}</div></div>`; }
/* ---- controls: power (with confirmation) and fleet lighting ---- */
const busy = {};
function powerButton(m){
  const n = JSON.stringify(m.Name).replace(/'/g, "&#39;");
  if (busy[m.Name]) return `<span class="btn pw" style="cursor:default">${busy[m.Name]}</span>`;
  if (m.Online) return `<button class="btn pw" onclick='powerMenu(${n})'>⏻ Power</button>`;
  return m.CanWake ? `<button class="btn pw go" onclick='askPower(${n}, "on")'>⏻ On</button>` : "";
}
function closeModal(){ $("modal").style.display = "none"; $("menu").style.display = "none"; }
function toast(text){ const t = $("toast"); t.textContent = text; t.style.display = "block"; clearTimeout(t._h); t._h = setTimeout(() => t.style.display = "none", 6000); }
function powerMenu(name){
  $("menuTitle").textContent = `Power · ${name}`;
  $("menuOff").onclick = () => { closeModal(); askPower(name, "off"); };
  $("menuReboot").onclick = () => { closeModal(); askPower(name, "reboot"); };
  $("menu").style.display = "flex";
}
const ACTIONS = {
  off:    { title: n => `Shut down ${n}?`, text: "A clean shutdown, the same as from its own menu. Fans hand back to the BIOS first.", go: "⏻ Shut down", cls: "danger", busy: "shutting down…", done: "shutting down" },
  reboot: { title: n => `Reboot ${n}?`, text: "A clean restart, the same as from its own menu. It comes back on its own.", go: "↻ Reboot", cls: "danger", busy: "rebooting…", done: "rebooting" },
  on:     { title: n => `Power on ${n}?`, text: "Sends a Wake-on-LAN packet across the network.", go: "⏻ Power on", cls: "go", busy: "waking…", done: "wake packet sent" },
};
function askPower(name, action){
  const a = ACTIONS[action];
  $("mTitle").textContent = a.title(name);
  $("mText").textContent = a.text;
  $("mGo").textContent = a.go;
  $("mGo").className = "btn " + a.cls;
  $("mGo").onclick = () => { closeModal(); power(name, action); };
  $("modal").style.display = "flex";
}
async function power(name, action){
  const a = ACTIONS[action];
  busy[name] = a.busy;
  try {
    const r = await fetch(`/api/fleet/machines/${encodeURIComponent(name)}/power`, { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ action }) });
    const b = await r.json().catch(() => ({}));
    toast(r.ok ? `${name}: ${b.how || a.done}` : `${name}: ${b.error || "failed (" + r.status + ")"}`);
    if (!r.ok) delete busy[name];
  } catch { delete busy[name]; toast(`${name}: hub not answering`); }
  setTimeout(() => delete busy[name], 120000); // the card shows the real state again within 2 min
  refresh();
}
const SWATCHES = /*PALETTE*/[]; // filled from Core ColorPalette when served: one colour list everywhere
let lighting = null, briBusy = false;
async function setLights(patch){
  const body = Object.assign({ RgbEnabled:true, RgbOff:false, RgbEffect:"Static", RgbR:128, RgbG:0, RgbB:255, RgbBrightness:100, RgbSpeed:3, Blackout:false },
    lighting || {}, patch, { managed:true });
  const r = await fetch("/api/fleet/settings/lighting", { method:"PUT", headers:{"Content-Type":"application/json"}, body: JSON.stringify(body) });
  toast(r.ok ? "Lighting set for every server" : ((await r.json().catch(() => ({}))).error || "Lighting not changed"));
  loadLights();
}
async function loadLights(){
  try {
    const s = await (await fetch("/api/fleet/settings", { cache:"no-store" })).json();
    const p = s.state && s.state.Profile, managed = s.state && (s.state.Managed || []).includes("Lighting");
    lighting = p ? { RgbEnabled:p.RgbEnabled, RgbOff:p.RgbOff, RgbEffect:p.RgbEffect, RgbR:p.RgbR, RgbG:p.RgbG, RgbB:p.RgbB, RgbBrightness:p.RgbBrightness, RgbSpeed:p.RgbSpeed, Blackout:p.Blackout } : null;
    const L = lighting || {}, cur = managed && !L.RgbOff && !L.Blackout && L.RgbEffect === "Static" ? [L.RgbR, L.RgbG, L.RgbB].map(v => (v ?? 0).toString(16).padStart(2, "0")).join("") : "";
    const rainbow = managed && !L.RgbOff && !L.Blackout && L.RgbEffect === "Rainbow", off = managed && (L.RgbOff || L.Blackout);
    if (briBusy) return; // don't rebuild the bar under a finger on the brightness slider
    const bri = L.RgbBrightness ?? 100;
    $("lights").innerHTML = SWATCHES.map(([h, n, ink]) => `<span class="sw${h === cur ? " on" : ""}" title="${n}" style="background:#${h};color:#${ink}"
        onclick='setLights({RgbEffect:"Static",RgbOff:false,Blackout:false,RgbR:${parseInt(h.slice(0,2),16)},RgbG:${parseInt(h.slice(2,4),16)},RgbB:${parseInt(h.slice(4,6),16)}})'>${h === cur ? "✓" : ""}</span>`).join("")
      + `<button class="btn${rainbow ? " on" : ""}" onclick='setLights({RgbEffect:"Rainbow",RgbOff:false,Blackout:false})'>Rainbow</button>`
      + `<button class="btn${off ? " on" : ""}" onclick='setLights({RgbOff:true})'>Off</button>`
      + `<span class="bri" title="Brightness">☀<input type="range" min="0" max="100" step="5" value="${bri}"
          onpointerdown="briBusy=true" oninput='this.nextElementSibling.textContent=this.value+"%"'
          onchange='briBusy=false; setLights({RgbBrightness:+this.value})'><b>${bri}%</b></span>`;
  } catch { /* the bar just stays as it was */ }
}
function machine(m){
  const state = !m.Online ? "off" : m.Failsafe ? "bad" : m.WarningCount ? "warn" : "ok";
  const hottest = [m.CpuTemperatureC, m.GpuTemperatureC].filter(x => x != null);
  const alert = !m.Online ? "" : m.Failsafe ? `<div class="alert bad">Failsafe: fans at 100 %</div>` : m.WarningCount ? `<div class="alert">${m.WarningCount} warning${m.WarningCount>1?"s":""}</div>` : "";
  return `<div class="card ${state === "off" ? "off" : state === "bad" ? "bad" : ""}">
    <div class="name"><span class="dot ${state}"></span><span class="ell">${esc(m.Name)}</span>${powerButton(m)}</div>
    <div class="sub">${m.Online ? "up " + up(m.UptimeSeconds) : "offline"}${m.Platform ? " · " + esc(m.Platform) : ""}</div>
    ${alert}
    ${m.Online && hottest.length ? heat(Math.max(...hottest), m.FanCount) : ""}
    <div class="big">
      <div><div class="k">CPU</div><div class="v">${deg(m.CpuTemperatureC)}</div></div>
      <div><div class="k">GPU</div><div class="v">${deg(m.GpuTemperatureC)}</div></div>
      <div><div class="k">Fans</div><div class="v">${m.FanDutyPercent == null ? "—" : m.FanDutyPercent + "<small>%</small>"}</div></div>
    </div></div>`;
}
function render(h){
  const ms = h.machines || [];
  $("machines").style.setProperty("--n", Math.max(ms.length, 1));
  $("machines").innerHTML = ms.map(machine).join("");
  const disks = ms.flatMap(m => (m.Storage || []).map(d => ({ m: m.Name, ...d }))).filter(d => d.TotalBytes > 0)
    .sort((a, b) => b.TotalBytes - a.TotalBytes).slice(0, 8);
  $("disks").innerHTML = disks.length ? disks.map(d => { const p = d.UsedBytes * 100 / d.TotalBytes;
    return `<div class="disk"><div class="lbl"><b>${esc(d.m)}</b>${esc(d.Name)}<span>${bytes(d.UsedBytes)} / ${bytes(d.TotalBytes)} · ${p.toFixed(0)}%</span></div>
      <div class="bar"><i class="${p >= 95 ? "bad" : p >= 85 ? "warn" : ""}" style="width:${p.toFixed(1)}%"></i></div></div>`; }).join("")
    : `<div class="list">No storage reported yet</div>`;
  const dk = ms.filter(m => m.Docker);
  $("docker").innerHTML = dk.length ? dk.map(m => `<div class="hero">${m.Docker.Running}<small> / ${m.Docker.Total}</small></div>
      <div class="sub">running on ${esc(m.Name)}</div>
      ${m.Docker.Stopped.length ? `<div class="list ell">Stopped: ${m.Docker.Stopped.slice(0, 6).map(esc).join(", ")}${m.Docker.Stopped.length > 6 ? "…" : ""}</div>` : `<div class="list">All containers running</div>`}`).join("")
    : `<div class="list">No container host reporting</div>`;
  const s = h.sabnzbd;
  if (!s) $("sab").innerHTML = `<div class="list">SABnzbd not configured</div>`;
  else if (!s.Reachable) $("sab").innerHTML = `<div class="list" style="color:var(--warn)">SABnzbd not answering</div>`;
  else {
    const active = s.Items > 0 && !s.Paused && s.KBps > 0;
    const speed = s.KBps >= 1024 ? (s.KBps / 1024).toFixed(1) + " MB/s" : Math.round(s.KBps) + " KB/s";
    $("sab").innerHTML = `<div class="speed">${active ? speed : s.Paused ? "Paused" : "Idle"}</div>
      <div class="list ell">${s.Current ? esc(s.Current) : s.Items ? "" : "Queue empty"}</div>
      <div class="dl"><div><div class="k">Queue</div><div class="v">${s.Items}</div></div>
        <div><div class="k">Left</div><div class="v" style="font-size:3.2vh">${esc(s.SizeLeft || "—")}</div></div>
        <div><div class="k">ETA</div><div class="v" style="font-size:3.2vh">${active ? esc(s.TimeLeft) : "—"}</div></div></div>`;
  }
}
async function refresh(){
  try { render(await (await fetch("/api/fleet/home", { cache:"no-store" })).json()); $("stale").style.display = "none"; }
  catch { $("stale").style.display = "block"; }
}
tick(); setInterval(tick, 1000);
refresh(); setInterval(refresh, 5000);
loadLights(); setInterval(loadLights, 5000); // a colour/brightness picked on any machine shows here within seconds
</script>
</body>
</html>
""";
}
