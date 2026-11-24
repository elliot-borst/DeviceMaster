# DeviceMaster

A custom Windows application that replaces Corsair iCUE, Lian Li L-Connect, and Turzx vendor
software: fan control, RGB, and a Turzx LCD dashboard over raw HID/WinUSB/serial —
no vendor software or services at runtime. Air cooling only: pumps, coolant sensors and
pump/fan LCD panels are not supported (a Link chain with a pump on it is left to the hub).

## Supported hardware

| Family | Devices | Status |
|---|---|---|
| Corsair iCUE LINK | System Hub (`1B1C:0C3F`) with fans on the chain | fan control, telemetry and RGB |
| Lian Li UNI FAN SL V3 wireless | TX/RX dongles (`0416:8040/8041`) | fan control, telemetry and RGB (the per-fan LCDs are not driven) |
| Turzx / Turing smart screens | 8.8" (`1A86:CA88`) | live metrics dashboard |

Details and protocol notes: [docs/SUPPORTED-DEVICES.md](docs/SUPPORTED-DEVICES.md) ·
Roadmap: [docs/PLAN.md](docs/PLAN.md) · Protocol sources: [docs/REFERENCES.md](docs/REFERENCES.md)

## Install

Download **DeviceMaster-Setup.exe** from the
[latest release](https://github.com/elliot-borst/DeviceMaster/releases/latest) and run it —
installs per-user (no admin needed), optional desktop/start-with-Windows shortcuts,
uninstaller included. The app shows its version, checks for updates on launch, and offers
one-click **Download & install** when a new version ships (whole-number versions: 1, 2, 3…).

## Build from source

```powershell
dotnet build
dotnet test
.\build-installer.ps1   # produces dist\DeviceMaster-Setup.exe (requires Inno Setup 6)
```

```powershell
# enumerate devices + warn about vendor software still running
DeviceMaster.App.exe discover

# poll CPU/GPU/motherboard sensors (run elevated for CPU temps)
DeviceMaster.App.exe monitor --seconds 2 --count 5

# Corsair iCUE Link hubs: chain devices, RPMs, temps
DeviceMaster.App.exe link status
DeviceMaster.App.exe link set --duty 65 --hold 10

# Lian Li SL V3 wireless fans
DeviceMaster.App.exe slv3 status
DeviceMaster.App.exe slv3 set --duty 65 --hold 10
```

(From source, prefix with `dotnet run --project src/DeviceMaster.App --`.)

## Fan control

Every fan follows one two-point temperature curve (CPU, GPU, or the hotter of the two). There is
no separate fixed-duty mode: for a constant speed, drag both points to the same duty. Settings
saved in the old Fixed mode load as a curve flat at that duty, so the fans keep their speed.

## Safety (read before touching device code)

- Sensor failure or an implausible temperature ⇒ every fan to 100% (a flat curve too); any error
  state drives 100%.
- Pumps are never driven: a Link chain carrying a pump or other liquid-cooling device is not
  written to at all, and a Lian Li wireless AIO is never sent a duty.
- Devices revert to hardware-default behaviour if the app dies (verified per family;
  SL V3 fans revert by design when the keepalive stops, Link hubs are restored to
  hardware mode on exit).
- Nothing is ever written to a device that isn't positively identified by VID/PID
  (`KnownDeviceRegistry.IsWriteAllowed`).

## Solution layout

- `DeviceMaster.Core` — abstractions, device registry, discovery, safety, conflict checks
- `DeviceMaster.Sensors` — LibreHardwareMonitor wrapper
- `DeviceMaster.Devices.CorsairLink` / `.LianLi` / `.Turzx` — one project per protocol family
- `DeviceMaster.App` — host (console commands now; tray app in later stages)
- `tests/DeviceMaster.Core.Tests` — xunit (protocol packet builders/parsers are pure and tested)
