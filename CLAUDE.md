# DeviceMaster

Custom Windows app replacing Corsair iCUE, Lian Li L-Connect, and Turzx software — raw
HID/WinUSB/serial to the hardware. C#/.NET (`net9.0-windows`), Serilog, HidSharp, LHM.

## Non-negotiable safety rules (water-cooled loop!)

- Pump duty never below `SafetyLimits.PumpMinimumDutyPercent` (50%); any error ⇒ 100%.
- Sensor read failure or implausible value (≤ 0 °C, > 115 °C) ⇒ fans/pump to 100%.
- Never write to a device unless `KnownDeviceRegistry.IsWriteAllowed(vid:pid)`. The registry
  in `DeviceMaster.Core` is the single source of truth for identification.
- Verify hub keepalive/hardware-fallback semantics before shipping any speed control.

## Stage discipline

Work proceeds in stages 0–6 (docs/PLAN.md). A stage must build, pass tests, and be exercised
on the real hardware before the next begins. Stage 0 complete 2026-07-06.

## Layout

- `src/DeviceMaster.Core` — abstractions, discovery, safety, device registry. No protocol code.
- `src/DeviceMaster.Sensors` — LibreHardwareMonitor wrapper.
- `src/DeviceMaster.Devices.{CorsairLink,LianLi,Turzx}` — one project per protocol family.
- `src/DeviceMaster.App` — composition root (console commands now, tray app later).
- `tests/` — xunit. Protocol packet builders must be pure static functions with tests.

## Hardware truth

docs/DEVICES.md records what is physically attached (scan 2026-07-06). Notable: TWO iCUE Link
hubs; Lian Li is the SL V3 *wireless* ecosystem (WinUSB, protocol unresearched — NOT classic
Uni Hub); Corsair LCD self-reports "XD5 ELITE LCD Pump"; Turzx is an 8.8" panel on COM3.
docs/REFERENCES.md maps each device to the open-source implementation we port from.

## Commands

- Build/test: `dotnet build` / `dotnet test` (repo root)
- Discovery: `dotnet run --project src/DeviceMaster.App -- discover`
- Sensors:  `dotnet run --project src/DeviceMaster.App -- monitor --seconds 2 --count 5`
  (CPU temps require an elevated terminal)
