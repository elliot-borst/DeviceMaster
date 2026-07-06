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

Keep the repo generic — other people will install this. Supported hardware and protocol
facts live in docs/SUPPORTED-DEVICES.md. Machine-specific inventory (serials, MACs, chain
maps of the dev rig) goes ONLY in local/MACHINE-NOTES.md, which is git-ignored — never
commit rig-specific identifiers or name the owner's hardware in code, docs, or commits.
docs/REFERENCES.md maps each device family to the open-source implementation we port from.

## Commands

- Build/test: `dotnet build` / `dotnet test` (repo root)
- Discovery: `dotnet run --project src/DeviceMaster.App -- discover`
- Sensors:  `dotnet run --project src/DeviceMaster.App -- monitor --seconds 2 --count 5`
  (CPU temps require an elevated terminal)
- Fan control CLI: `... -- link status|set`, `... -- slv3 status|set`

## Releasing (whole-number versions: 1, 2, 3, …)

1. Bump `<Version>` major in `src/DeviceMaster.Ui/DeviceMaster.Ui.csproj` (single source;
   the UI shows it and the updater compares against the release tag) and update
   `MainWindow.VersionDate`.
2. `.\build-installer.ps1` → `dist\DeviceMaster-Setup.exe` (Inno Setup 6 required).
3. Commit + push (plain messages, authored as the repo owner — never AI attribution).
4. `gh release create v<N> dist\DeviceMaster-Setup.exe --title "DeviceMaster <N>" --notes ...`
   The release asset MUST be the setup exe (the in-app updater looks for an .exe asset with
   "setup" in the name); do not ship zips.
