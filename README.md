# DeviceMaster

A fully custom Windows application that replaces Corsair iCUE, Lian Li L-Connect, and the
Turzx vendor software: fan/pump control, RGB, and custom LCD dashboards over raw
HID/WinUSB/serial — no vendor software or services at runtime.

## Hardware targets

| Hardware | Interface | Status |
|---|---|---|
| 2× Corsair iCUE LINK System Hub (fans, pump, coolant temp) | HID `1B1C:0C3F` | identified |
| Corsair pump/res LCD ("XD5 ELITE LCD Pump") | HID `1B1C:0C43` | identified |
| Lian Li UNI FAN SL V3 wireless (11 fans, TX/RX pair) | WinUSB `0416:804x`, `1CBE:0005` | identified, protocol research pending |
| Turzx/Turing 8.8" smart screen | serial `1A86:CA88` (COM3) | identified |

Full inventory: [docs/DEVICES.md](docs/DEVICES.md) · Plan: [docs/PLAN.md](docs/PLAN.md) ·
Protocol sources: [docs/REFERENCES.md](docs/REFERENCES.md)

## Status: Stage 0 ✅

Scaffolding, device discovery, sensor polling, logging, safety primitives, tests.

```powershell
dotnet build
dotnet test

# enumerate devices + warn about vendor software still running
dotnet run --project src/DeviceMaster.App -- discover

# poll CPU/GPU/motherboard sensors (run elevated for CPU temps)
dotnet run --project src/DeviceMaster.App -- monitor --seconds 2 --count 5
```

## Safety (water-cooled system — read before touching device code)

- Pump duty is hard-floored at 50% and jumps to 100% on any error.
- Sensor failure ⇒ fans and pump to 100%.
- Devices revert to hardware-default curves if the app dies (hub fallback behaviour is
  verified as part of Stage 1, before any speed control ships).
- Nothing is ever written to a device that isn't positively identified by VID/PID
  (`KnownDeviceRegistry.IsWriteAllowed`).

## Solution layout

- `DeviceMaster.Core` — abstractions, device registry, discovery, safety, conflict checks
- `DeviceMaster.Sensors` — LibreHardwareMonitor wrapper
- `DeviceMaster.Devices.CorsairLink` / `.LianLi` / `.Turzx` — one project per protocol family
- `DeviceMaster.App` — host (console commands now; tray app in later stages)
- `tests/DeviceMaster.Core.Tests` — xunit
