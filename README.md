# DeviceMaster

A custom Windows application that replaces Corsair iCUE, Lian Li L-Connect, and Turzx vendor
software: fan/pump control, RGB, and custom LCD dashboards over raw HID/WinUSB/serial —
no vendor software or services at runtime.

## Supported hardware

| Family | Devices | Status |
|---|---|---|
| Corsair iCUE LINK | System Hub (`1B1C:0C3F`), pump/res LCD module (`1B1C:0C43`) | fan control + telemetry working; RGB/LCD planned |
| Lian Li UNI FAN SL V3 wireless | TX/RX dongles (`0416:8040/8041`), per-fan LCD nodes (`1CBE:0005`) | fan control + telemetry working; RGB/LCD planned |
| Turzx / Turing smart screens | 8.8" (`1A86:CA88`) and family | planned (Stage 5) |

Details and protocol notes: [docs/SUPPORTED-DEVICES.md](docs/SUPPORTED-DEVICES.md) ·
Roadmap: [docs/PLAN.md](docs/PLAN.md) · Protocol sources: [docs/REFERENCES.md](docs/REFERENCES.md)

## Quick start

Download `DeviceMaster` from the latest release (self-contained, no .NET required), or build
from source:

```powershell
dotnet build
dotnet test
```

```powershell
# enumerate devices + warn about vendor software still running
DeviceMaster.App.exe discover

# poll CPU/GPU/motherboard sensors (run elevated for CPU temps)
DeviceMaster.App.exe monitor --seconds 2 --count 5

# Corsair iCUE Link hubs: chain devices, RPMs, coolant temp
DeviceMaster.App.exe link status
DeviceMaster.App.exe link set --duty 65 --hold 10

# Lian Li SL V3 wireless fans
DeviceMaster.App.exe slv3 status
DeviceMaster.App.exe slv3 set --duty 65 --hold 10
```

(From source, prefix with `dotnet run --project src/DeviceMaster.App --`.)

## Safety (built for water-cooled systems — read before touching device code)

- Pump duty is hard-floored (50%) and any error state drives 100%.
- Sensor failure ⇒ fans and pump to 100%.
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
