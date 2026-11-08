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
| Local LLM status (Strata `/metrics`, read-only) | — | **yes** (model, live speed, slots; also the GPU figures) |
| Lian Li SL V3 / RAM ENE RGB / PresentMon | yes | not ported |

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
- **Safety:** SuperIO has no hardware fallback, so a killed process leaves the last duty in place;
  duties never go below `SafetyLimits.HeaderMinimumDutyPercent` (30 %, rounded up), sensor failure
  drives 100 %, and SIGTERM/mode Off hand every header back to its original `pwmN_enable` mode.
  Run under a restart policy.
- **Clock:** the dashboard shows the container's local time; set `TZ` (the image includes tzdata).

