# DeviceMaster on Linux (headless / Docker)

DeviceMaster on Linux runs the **headless control loop** — the same 1 Hz policy, device
sessions, and safety primitives as the Windows app, with the tray/WPF layer removed. It is
designed to run in a Docker container on a server.

## What runs where

| Part | Windows app | Headless (Linux) |
|---|---|---|
| iCUE LINK hubs (fans + pump: duty, telemetry, RGB) | yes | **yes** (HidSharp → hidraw) |
| Pump / radiator LCD (JPEG frames, brightness) | yes | **yes** (same HID session) |
| GPU RGB via ENE controller (ASUS boards, i2c 0x67) | yes | **yes** (raw i2c-dev, repeated starts) |
| CPU/GPU temperature | LHM / nvidia | kernel hwmon (k10temp/coretemp) + nvidia-smi |
| Turzx 8.8" screen (system dashboard: local LLM, GPU + fans, CPU + RAM + cooler) | yes | **yes** (`/dev/ttyACM*` found via sysfs; SkiaSharp renderer) |
| ASUS AURA mainboard LED controller (static / rainbow) | yes | **yes** (HidSharp → hidraw) |
| Motherboard fan headers (Nuvoton NCT67xx Super I/O) | yes (LHM) | **yes** (hwmon `pwmN`; only headers spinning at start-up; never below 30 %; BIOS curve restored on stop / mode Off) |
| RGB DRAM via ENE controllers (chipset SMBus) | yes | **yes** (`/dev/i2c-N` SMBus ioctls on PIIX4/I801 adapters) |
| Local LLM status (Strata `/metrics`, read-only) | — | **yes** (model, live speed, slots; also the GPU figures) |
| Lian Li SL V3 / PresentMon | yes | not ported |

## Projects

- `src/DeviceMaster.Platform.Linux` — libc P/Invoke for i2c-dev (`I2cDev`), an `ISmBus`
  implementation on `/dev/i2c-N` (`LinuxI2cSmBus`, uses repeated starts because the nvidia
  i2c adapter rejects SMBus ioctls), and `GpuI2cLocator` (finds the GPU's i2c node from
  `/sys/bus/i2c/devices`).
- `src/DeviceMaster.Sensors.Linux` — `Hwmon` (walks `/sys/class/hwmon`), `NvidiaSmi`
  (thin wrapper + pure line parser), `LinuxSensorSource` (`ISensorSource`; throws when no
  temperature source is readable — the loop treats that as the failsafe trigger).
- `src/DeviceMaster.App.Headless` — the CLI and `HeadlessLoop` (the 1 Hz daemon).
- `src/DeviceMaster.Lcd.Skia` — LCD frame rendering with SkiaSharp (the same math as the
  Windows `LcdMetricRenderer`/`LcdFrames`, but System.Drawing is Windows-only on .NET 9).
  Referenced by the headless app only; the Windows app keeps its System.Drawing renderers.

All of it reuses the shared device sessions (`LinkHub`, `CorsairLcdDevice`, `EneRgbDevice`)
and safety code (`SafetyGuard`, `SensorValidity`, `KnownDeviceRegistry`) from the Windows
projects — no protocol code is duplicated.

## Which build is running?

`GET /status.json` carries `Version` (the informational version, with the source commit the
SDK stamps in when building from a git checkout, e.g. `1.0.0+e92828b…`) and `Commit` (short
sha); the startup log line repeats it. Build from a git clone so the commit is recorded.

## Building

```sh
# Linux or Windows build host (cross-targeting is on by default for the container image):
dotnet build -p:EnableWindowsTargeting=true
dotnet test  -p:EnableWindowsTargeting=true

# Container image (self-contained, linux-x64 by default):
docker build -t devicemaster:headless .
```

The shared projects target `net9.0-windows` (compile-time API surface only). The headless
executable never calls Windows APIs and runs on Linux unchanged — this is intentional so the
whole solution stays one TFM family.

## CLI

```
devicemaster discover                          enumerate hubs, pump LCD, GPU i2c, sensors
devicemaster status                            hub tree with RPMs/temps; restores hardware mode
devicemaster speed --duty 65 [--pump-duty 80] [--hold 10]
devicemaster rgb --hex 8000FF [--off] [--gpu]  # --gpu also drives the ENE chip
devicemaster ene [--hex 8000FF] [--persist] [--i2c /dev/i2c-10]
devicemaster lcd <off|black|white|metrics> [--metric COOLANT|CPU_TEMP|GPU_TEMP|FAN_DUTY|PUMP_DUTY|PUMP_RPM]
    [--hold 10] [--brightness 80]
devicemaster loop [--config /config/config.json]   # the daemon
devicemaster web [--config /config/config.json] --port 27004 [--bind 100.x.y.z --bind 127.0.0.1]
    # --bind (repeatable) limits the dashboard to those addresses, e.g. tailnet IP + localhost
devicemaster dashboard-preview [--busy] [--out dashboard.png]
    # renders the Turzx 8.8" dashboard with sample figures to a PNG (layout check, no panel needed)
    # control loop + built-in web dashboard (page at /, data at /status.json)
```

Every one-shot command opens the hubs in software mode, does its thing, and **restores
hardware mode on exit** — the hubs keep working (with their built-in curves) when the
process leaves.

## Running in Docker

```sh
docker run -d --name devicemaster \
    --privileged \
    --restart unless-stopped \
    -p 192.168.86.5:27004:27004 \
    -v /dev:/dev \
    -v /run/udev:/run/udev:ro \
    -v /host/appdata/devicemaster:/config \
    -v /usr/bin/nvidia-smi:/usr/bin/nvidia-smi:ro \
    devicemaster:headless web --port 27004
```

- The `web` mode runs the control loop plus a small dashboard (plain HTTP — reached over the
  tailnet, which is encrypted end-to-end, or the trusted LAN). The page auto-refreshes
  /status.json every 2 s: temps, curve/duty, failsafe banner, GPU load/power/VRAM, and the
  per-channel RPM table. `loop` mode is unchanged for dashboard-less setups.

- `--privileged` (or `--device /dev/hidraw*` + `--device /dev/i2c-*`) is required: hidraw
  for the hubs/LCD, i2c-dev for the GPU ENE chip.
- **`-v /run/udev:/run/udev:ro` is required on most hosts**: HidSharp enumerates HID
  devices through libudev, which reads the udev database (`/run/udev/data`). A container
  without its own udev sees the `/dev/hidraw*` nodes but enumerates **zero devices**
  without this mount.
- Config lives at `DEVICEMASTER_CONFIG` (default `/config/config.json`). On first run a
  defaults file is written; the loop **re-reads the file on change** — edit it live and the
  next tick picks it up (no restart for duty/RGB/LCD changes).
- SIGTERM/SIGINT are handled in-process: the loop stops writing and restores every hub to
  hardware mode before exiting (~1 s).
- An optional status snapshot is written to `StatusFile` every `StatusFileEverySeconds` —
  JSON with mode, temperatures, per-channel RPMs, active failsafe flag, warnings.

## Configuration (headless block)

Same JSON shape as the Windows app's `Control` section (mode, source, `CurvePoints`,
manual duty, pump duty, RGB, LCD), plus:

| Field | Meaning |
|---|---|
| `GpuPciAddress` | PCI address of the GPU whose i2c bus has the ENE chip (e.g. `"01:00.0"`); null = first NVIDIA adapter |
| `I2cDevice` | explicit `/dev/i2c-N` (wins over `GpuPciAddress`) |
| `NvidiaSmiPath` | path to nvidia-smi (default `/usr/bin/nvidia-smi`) |
| `GpuSensorFile` | file holding one nvidia-smi CSV row, refreshed externally (host cron) — used when nvidia-smi cannot run in the container; stale rows ignored |
| `GpuSensorFileStaleSeconds` | sensor-file freshness window (default 120) |
| `GpuRgbEnabled` | drive the ENE chip (default true when the chip is found) |
| `StatusFile` / `StatusFileEverySeconds` | status snapshot for external dashboards |
| `Trace` | packet-level hub traffic logging |

## Safety (same rules as the Windows app — see CLAUDE.md)

- Pump duty is hard-floored at 50%; any write error ⇒ pump 100%.
- Sensor read failure or implausible temperature (≤ 0 °C or > 115 °C) ⇒ fans and pump to 100%.
- Writes only go to devices the `KnownDeviceRegistry` identifies (VID/PID gate).
- A hub session that starts failing is dropped (it may have lost power) and reopened on a
  later tick; while a hub is absent, the remaining hubs keep being controlled.
- On clean stop (SIGTERM) and on `Mode = Off` every hub is returned to hardware mode.

## Caveats

- **Two drivers, one hub — never both.** A hub driven by iCUE/openlinkhub-style software at
  the same time will get its LED registry interleaved and can go dark. Stop the other driver
  before starting the loop (and vice versa).
- **Cold boot ≠ USB unplug for iCUE LINK hubs.** The hubs take PCIe power from the PSU; USB
  is data only. After a hub power event, restart the loop (it re-enumerates automatically
  within ~30 s, but a container restart is the clean path).
- The ENE chip's color state is **volatile in RAM** but the chip also keeps the last saved
  effect in **non-volatile flash**. The loop re-applies the color at container start and
  persists to flash after the color has settled (flash endurance is respected: one write per
  settled change, not per tick).
- **GPU temperature in the container**: host driver libraries (`libnvidia-ml`) are not
  guaranteed to load under a container's glibc — on Unraid (Slackware) builds, nvidia-smi
  fails to load them in a Debian container. The supported pattern is `GpuSensorFile`: a host
  cron writes one `nvidia-smi --query-gpu=... --format=csv,noheader,nounits` row into the
  config volume every minute; the loop reads it (freshness-checked) and falls back to
  `NvidiaSmiPath` if the file is missing/stale.
- **Which bus:** ASUS boards keep the ENE chip on the GPU's *first* NVIDIA adapter
  ("NVIDIA i2c adapter 1", e.g. `/dev/i2c-7`), not whichever node sorts first. The loop drives GPU
  RGB only on ASUS boards (PCI subsystem vendor `0x1043`) and takes the first of the GPU's adapters,
  in adapter order, whose `0x67` passes the read-only ENE fingerprint. `I2cDevice` overrides it.
- **The GPU's ENE i2c bus may be unusable at the driver level.** Some NVIDIA driver
  builds (observed with 610.57.04) reject every transaction on their own i2c-dev adapter
  with `EINVAL` — including trivial 1-byte writes, and OpenRGB fails identically. The loop
  logs `no ENE controller at 0x67` and continues without GPU RGB; the chip's flash-persisted
  color remains on the card. There is no software workaround on the host; it is a driver
  limitation (a different driver build may expose the bus).
  Note: before #21 DeviceMaster's own raw-transfer code sent the wrong ioctl (`I2C_TENBIT`
  instead of `I2C_RDWR`), which also fails with `EINVAL`, so earlier "unusable bus" sightings
  should be rechecked with a current build.
- LCD frames render through SkiaSharp (`DeviceMaster.Lcd.Skia`); the image ships
  `fonts-dejavu-core` because the base image has no fonts.
- `nvidia-smi` is not in the image. Without a GPU temperature source, GPU source
  temperature is simply unavailable (failsafe engages if you selected GPU as the curve
  source — use Coolant or CPU).

## Air-cooled boxes: motherboard fans, Aura, Turzx, local LLM

Config keys (see `HeadlessConfig`): `MotherboardFans` (default true), `AuraEnabled` (default true),
`StrataUrl` + `StrataKeyFile` (env-style file with `API_KEY=`; only `GET /metrics` is ever sent),
`DisplayName` (shown on the screen). The Turzx screen follows `Control.TurzxScreen`
(`Unmanaged` / `Off` / `Metrics`), `TurzxBrightness` and `TurzxRotation` (180 = mounted upside down).
Use `Control.Source` `Cpu` (or `Gpu`) when there is no coolant sensor.

- **Fan driver:** load the kernel's Super I/O driver (`modprobe nct6775`, persist with
  `/etc/modules-load.d/nct6775.conf`). Only headers with a spinning fan at start-up are controlled.
  Newer MSI boards use the out-of-tree `nct6687` driver instead and need one extra option: see
  [MSI boards: system fans need `msi_fan_brute_force=1`](#msi-boards-system-fans-need-msi_fan_brute_force1).
- **Safety:** SuperIO has no hardware fallback, so a killed process leaves the last duty in place;
  duties never go below `SafetyLimits.HeaderMinimumDutyPercent` (30 %, rounded up), sensor failure
  drives 100 %, and SIGTERM/mode Off hand every header back to its original `pwmN_enable` mode.
  Run under a restart policy.
- **Clock:** the dashboard shows the container's local time; set `TZ` (the image includes tzdata).


## MSI boards: system fans need `msi_fan_brute_force=1`

Read this before trying anything else on an MSI board's fan headers (#85). It took several attempts to
find; this is the cause and the fix.

**Applies to:** MSI boards with the Nuvoton NCT6687D(-R) embedded controller, driven on Linux by the
out-of-tree [`nct6687` driver](https://github.com/Fred78290/nct6687d) (hwmon name `nct6687`, often
installed as a package or plugin, e.g. the Unraid "nct6687-driver" plugin). The driver's README lists
the affected `msi_alt1` families: B840 / B850 / X870 / X870E / Z890. Verified on an MSI MAG X870E
TOMAHAWK WIFI (MS-7E59), BIOS 2.AC4, driver build 20261019, kernel 6.18, 2026-11-18.

**Symptoms**
- The CPU fan header (`pwm1`) takes a duty, but every system-fan header (`pwm3`, `pwm5`, …) refuses:
  the write to `pwmN` or `pwmN_enable` fails with `Input/output error` (EIO).
- `dmesg` shows `nct6687: The device rejected new configuration as invalid`, once per refused write
  (75 of them in the case above).
- DeviceMaster logs `motherboard fans: pwmN does not take a duty (… Input/output error …) — handing it
  back to the BIOS`, and the dashboard notes those headers "run on the board's own BIOS curve".
  Before #85 that note said the board "doesn't accept software speed control", which was misleading.
- Without the fix, some MSI boards instead send every fan to about 60 % (duty 154/255) once one is set
  (nct6687d issue #148).

**Cause.** On these boards the EC doesn't take a plain manual duty for a system fan; it runs each
system fan from a 7-point curve and rejects a configuration it considers invalid. Since nct6687d PR
#190 the driver checks each write and returns EIO when the EC refuses it. `msi_fan_brute_force=1`
makes the driver write the requested duty to all 7 curve points (a flat curve), which the EC
accepts. The driver saves the original curve first and restores it when the header goes back to
automatic (`pwmN_enable` = 2), on driver unload, or when its fan-control watchdog expires. So
DeviceMaster's hand-back to the BIOS curve still works. The CPU and pump headers have their own manual
mode and don't need the option. **This is not a DeviceMaster bug:** DeviceMaster was right to hand
the refused headers back to the BIOS; it just couldn't drive them without the driver option.

**Fix**
1. Stop DeviceMaster, so nothing writes to the chip while the driver reloads.
2. Reload the driver with the option, then check `pwm1`…`pwm8` exist again:
   `rmmod nct6687 && modprobe nct6687 msi_fan_brute_force=1`.
   This driver build doesn't expose the option under `/sys/module/nct6687/parameters`, so prove it
   with a write instead.
3. Test one header: `echo 1 > pwmN_enable; echo 128 > pwmN` should succeed with no new "rejected"
   lines in `dmesg`. `fanN_input` changes gradually: the EC ramps slowly (measured on MS-7E59: about 1/255 of
   the duty range per second, so a big change takes a minute or more; `pwmN` reads the ramping value). Then
   `echo 2 > pwmN_enable` hands it back to the BIOS, and the rpm climbs back.
4. Start DeviceMaster. Its log shows no "does not take a duty" lines, the dashboard lists the system
   fans as controlled, and the BIOS-curve note is gone.

**Make it survive a reboot**
- Plain Linux: `/etc/modprobe.d/nct6687.conf` containing `options nct6687 msi_fan_brute_force=1`.
- **Unraid:** `/etc` is rebuilt at every boot, so put that file in **`/boot/config/modprobe.d/nct6687.conf`**
  on the flash drive. `rc.modules.local` copies every file in `/boot/config/modprobe.d` into
  `/etc/modprobe.d` at boot, before plugins load the driver. Copy it to `/etc/modprobe.d` as well to
  apply it without a reboot. Check with `modprobe -c | grep nct6687`.
- Undo: delete the file and reload the driver (or reboot); the headers go back to the BIOS curve.

**Pitfalls**
- Don't force `fan_config=msi_alt1` on older MSI boards (B650/B660/X670/Z690/Z790): they use the
  `default` map, and `msi_alt1` reads zero rpm on every system fan there. `auto` (the default) picks
  correctly; check `/sys/module/nct6687/parameters/fan_config`.
- `manual=1` is about voltage labels, not fans; it doesn't help.
- Unloading the driver while DeviceMaster runs makes its writes fail. The fans keep running on the
  EC's own curves, but stop DeviceMaster first, and start it again afterwards.
- DeviceMaster checks each duty write by reading `pwmN` back; before #86 it judged this slow ramp as
  "does not take a duty" and kept handing the headers back (dashboard: fewer fans under control plus a
  warning per header). Since #86 a read-back that is moving towards the duty counts as following it.
- After an S3 resume the EC may write its own curves back while `pwmN_enable` still reads 1
  (nct6687d PR #228). DeviceMaster rewrites duties regularly, so it recovers on its next refresh.
- Upstream PR #228 (a draft as of 2026-11) would control system fans through their own manual bits,
  which would make `msi_fan_brute_force` unnecessary. Re-check after a driver update.

**Sources:** the [nct6687d README](https://github.com/Fred78290/nct6687d) (module parameters, the
supported-board table, the "Loading `msi_fan_brute_force` parameter fails" troubleshooting entry), and
these pages under github.com/Fred78290/nct6687d/:
- issue #148: MSI B850/X870E Tomahawk; brute force fixes case-fan control; the 60 % behaviour;
- issue #224: X870E Carbon; the EIO plus "rejected new configuration as invalid", fixed by brute force;
- PR #190: write verification returning EIO; curve save and restore; tested on MS-7E59;
- PR #228: draft, the EC's register-level behaviour.

## Automations on the fleet hub (#83)

An agent or a scheduled job (e.g. "blackout at 23:00, lights on at 07:00") can change fleet settings
and power without a signed-in tailnet user, using a bearer token. Add to `hub.json`:

```json
"Automations": [ { "Name": "claudecode", "TokenFile": "/tokens/automation-claudecode" } ]
```

- The token file holds one random token of 32+ characters (e.g. `openssl rand -hex 32`). The hub
  never logs it; a missing, short or unreadable file just skips that automation.
- Requests send `Authorization: Bearer <token>` to the hub page's address (normally loopback on the
  hub machine). They get the same reach as a signed-in user: `PUT /api/fleet/settings/<section>`,
  `PUT /api/fleet/machines/<name>/<section>`, `POST /api/fleet/machines/<name>/power`, and a machine's
  page (`POST /m/<name>/config.json`). Peer endpoints stay peer-token only.
- Every change is audited as `<Name> (automation)`. A standby hub forwards the write, and that name,
  to the active hub, so only the hub the automation talks to needs the token.
- Revoke: delete the token file (or the entry) and restart the hub.
- Lighting writes replace the whole lighting section: read `GET /api/fleet/settings` first and send
  every lighting field back with your changes (`RgbOff` = lights out, `Blackout` = movie mode).

A machine's own settings API also accepts `rgbOff` (bool) now; during blackout it sets what leaving
blackout restores.

## Fleet hub failover (#68)

The fleet hub (`devicemaster hub --config hub.json`) can run on several machines at once. One
is **active** (pushes fleet settings, adopts changes made on a machine, sends power actions,
keeps the audit log); the others are **standby** (they poll the agents for their own pages and
forward every write to the active hub). Add to each hub's `hub.json`:

```json
{
  "Self": "server-a",
  "Priority": 1,
  "PeerTokenFile": "/tokens/hub-peer",
  "PeerPort": 27081,
  "Peers": [
    { "Name": "server-b", "Url": "http://100.x.y.z:27081", "Priority": 2 },
    { "Name": "server-c", "Url": "http://100.x.y.w:27081", "Priority": 3 }
  ],
  "Agents": [ … same list on every hub … ]
}
```

- **Election:** the reachable hub with the lowest `Priority` that has been up for 60 s (or is
  already active) is active. A hub that stops answering heartbeats for 15 s is down, so the next
  one takes over; when it comes back it takes over again after 60 s up (no flapping during a
  reboot). A freshly started group waits 60 s before anyone claims.
- **State:** the active hub pushes fleet state and the audit log to every standby (on change and
  every 30 s); a hub becoming active first pulls the newest state a peer has. Every change is made
  by the active hub only.
- **Peer API** on `PeerPort`: `GET /api/hub/v1/heartbeat`, `GET`/`PUT /api/hub/v1/state`, plus
  forwarded writes. Every request needs `Authorization: Bearer <peer token>`; the token file holds
  the same random string on every hub. Each hub also needs every agent's token.
- Without `Peers` a hub is a lone hub and always active (the previous behaviour).

### On Windows (the app)

The Windows app runs the same hub when `%LOCALAPPDATA%\DeviceMaster\hub\hub.json` exists (same
format; relative `TokenFile`/`PeerTokenFile` paths resolve against that folder; state in
`fleet-state.json` beside it). The page listens on `127.0.0.1:27080` (put `tailscale serve` in front
for a tailnet address), the peer API on `PeerPort`, and the app adds a firewall rule allowing that
port from Tailscale addresses only. Give it the highest priority number: it is the last resort.
