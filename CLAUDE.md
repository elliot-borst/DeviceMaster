# DeviceMaster

Custom Windows app replacing Corsair iCUE, Lian Li L-Connect, and Turzx software — raw
HID/WinUSB/serial to the hardware. C#/.NET (`net9.0-windows`), Serilog, HidSharp, LHM.

## Non-negotiable safety rules

- Air cooling only: pump, coolant and pump/fan LCD support were removed (#96). Never drive a
  pump — liquid-cooling Link devices stay out of `LinkDeviceCatalog` (an unknown chain device
  makes the whole hub do-not-write) and a Lian Li wireless AIO never gets a PWM.
- Sensor read failure or implausible value (≤ 0 °C, > 115 °C) ⇒ fans to 100%; any error ⇒ 100%.
- Never write to a device unless `KnownDeviceRegistry.IsWriteAllowed(vid:pid)`. The registry
  in `DeviceMaster.Core` is the single source of truth for identification.
- Verify hub keepalive/hardware-fallback semantics before shipping any speed control.

## Stage discipline

Work proceeds in stages 0–6 (docs/PLAN.md). A stage must build, pass tests, and be exercised
on the real hardware before the next begins. Stage 0 complete 2026-07-06.

## Workflow

Every piece of work gets a GitHub issue (`gh issue create`) before or alongside the work
itself — features, fixes, chores, all of it. Reference the issue number in the commit(s).

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

1. Bump `<Version>` major in `Directory.Build.props` (single source for every build — Windows app,
   Linux agents and hub all report it;
   the UI shows it and the updater compares against the release tag) and update
   `MainWindow.VersionDate`.
2. `.\build-installer.ps1` → `dist\DeviceMaster-Setup.exe` (Inno Setup 6 required).
3. Commit + push (plain messages, authored as the repo owner — never AI attribution).
4. Linux build for the hosts' update helper (#110), on a Linux box with Docker (not the Unraid host):
   `scripts/update-helper/build-linux-release.sh <commit> <dir>` → `DeviceMaster-linux-x64.tar.gz` + `.sha256`.
5. `gh release create v<N> dist\DeviceMaster-Setup.exe <dir>/DeviceMaster-linux-x64.tar.gz <dir>/DeviceMaster-linux-x64.tar.gz.sha256 --title "DeviceMaster <N>" --notes ...`
   The Windows asset MUST be the setup exe (the in-app updater looks for an .exe asset with
   "setup" in the name); the Linux asset names are fixed (the update helper downloads them by name).
6. Update the fleet: the hub's ⇪ Update per machine (or `POST /api/fleet/machines/{name}/update`).
## GitHub issues: assign the owner

Every issue created on this repo is **assigned to the owner, `elliot-borst`, the moment it is created**
(owner rule, 2026-11-13), by every agent and every scheduled run: `gh issue create --assignee elliot-borst …`,
or with the plain API put `"assignees":["elliot-borst"]` in the create body. If you pick up or reopen an
issue that has no assignee, add it (`gh issue edit <n> --add-assignee elliot-borst`). A 10-minute sweep
(ServerStack `tools/assign-issues-to-owner.sh`) catches any that slip through; don't rely on it.
