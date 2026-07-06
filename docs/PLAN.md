# DeviceMaster — architecture and staged plan

A custom Windows application that fully replaces Corsair iCUE, Lian Li L-Connect, and the
Turzx vendor software: raw HID/WinUSB/serial to the hardware, no vendor services at runtime.

## Solution structure

```
DeviceMaster.sln
├── src/
│   ├── DeviceMaster.Core/               abstractions, device registry (VID/PID truth),
│   │                                    discovery, safety rules, conflict checker
│   ├── DeviceMaster.Sensors/            LibreHardwareMonitor wrapper (ISensorSource)
│   ├── DeviceMaster.Devices.CorsairLink/  iCUE Link hubs: fans, pump, coolant temp, RGB; LCD
│   ├── DeviceMaster.Devices.LianLi/     UNI FAN SL V3 wireless ecosystem (WinUSB)
│   ├── DeviceMaster.Devices.Turzx/      Turing-family 8.8" screen (serial)
│   ├── DeviceMaster.Rendering/          (added in Stage 4) shared display pipeline that
│   │                                    renders metric layouts to both the Corsair LCD
│   │                                    and the Turzx screen
│   └── DeviceMaster.App/                composition root/host — console now, tray app later
└── tests/
    └── DeviceMaster.Core.Tests/         registry, safety-clamp, and parsing tests
                                         (per-protocol packet-builder tests added per stage)
```

Rules:
- `Core` contains no protocol code and no vendor-specific I/O.
- Each protocol family is its own project; packet builders are pure static functions with
  unit tests; the I/O wrapper around them stays thin.
- `App` is only wiring/hosting, so the host model can change without touching logic.

## Host model: tray app vs Windows service (decision)

**Start as a single tray app** (auto-start at login, run elevated via a scheduled task so
LibreHardwareMonitor's kernel driver loads). Reasons:
- Crash safety does not depend on our process staying alive — it depends on hardware
  fallback behaviour (see safety section), which we must verify anyway.
- A service + IPC + separate UI triples the plumbing before any device code exists.
- LHM, HID, and rendering all work fine in an elevated user session.

Revisit a service split only if pre-login control or multi-user support becomes a real need.
`App` stays a pure composition root so the split stays cheap.

## Stages

- **Stage 0 — DONE (2026-07-06):** repo scaffolding, device enumeration (`discover`),
  LHM sensor polling (`monitor`), Serilog logging, safety primitives, unit tests.
- **Stage 1 — fan speed control.** Progress 2026-07-06:
  - ✅ iCUE Link protocol ported (handshake, chain enumeration, RPM/coolant-temp reads,
    fixed-duty writes) and verified on real hardware (`link status` / `link set`).
    Coolant temperature is read from the pump chain device. Dev-rig chain maps live in
    the git-ignored local/MACHINE-NOTES.md.
  - ✅ SL V3 wireless protocol research complete (REFERENCES.md) — fully documented in two
    MIT projects; fans need PWM re-sent every ≤1 s and revert to defaults otherwise
    (= fail-safe on crash by design).
  - ✅ SL V3 fan control implemented and verified (dependency-free WinUSB interop; TX/RX
    dongle driver; keepalive model). Firmware 16 GetDev page quirk documented in
    SUPPORTED-DEVICES.md.
  - ✅ Temperature curves + 1 Hz control loop (DeviceMaster.Control): coolant/CPU/GPU
    sources, linear-interpolated curves, manual mode, sensor-failure ⇒ 100% failsafe,
    write-on-change + periodic refresh for Link hubs, per-tick keepalive for SL V3.
    Verified live across both families simultaneously (v2). Desktop app (DeviceMaster.Ui)
    runs the loop with mode/source/duty controls, persisted config, and auto-updates.
  - ⬜ Link hub crash-fallback verification: no keepalive exists in either reference; the
    hub appears to hold last-written duties in software mode. Graceful exit restores
    hardware mode (implemented & verified, including from the UI). Pending: kill-process
    test to observe whether the hub ever self-reverts; until proven, the control loop
    treats "last write" as persistent — another reason pump stays at 100% whenever we are
    in software mode.
  - ⬜ Per-family/per-channel curves and an in-app curve editor (currently one global
    curve; Stage 6 territory).
- **Stage 2 — RGB static colors** on both families: ✅ shipped v12 (2026-07-06). Corsair:
  LED counts from endpoint 0x20, interleaved RGB buffer to endpoint 0x22 (handle 0),
  508-byte chunks. SL V3: TinyUZ-compressed one-frame effect over RF (0x12 0x20), stored
  and looped by fan firmware, refreshed every 60 s. Lighting card in the app (toggle +
  swatches). Effects/animations remain for Stage 6.
- **Stage 3 — pump speed control** via the Link hub (pump-bearing chain devices), floor-clamped.
- **Stage 4 — Corsair LCD rendering** (`DeviceMaster.Rendering` is born): static image first,
  then live metrics at a modest FPS. OpenLinkHub LCD framing, 1024-byte HID chunks.
- **Stage 5 — Turzx 8.8"** via the same rendering pipeline, serial protocol from
  turing-smart-screen-python / Tedd.TuringScreen.
- **Stage 6 — UI polish**, effects, profiles, tray UX.

A stage is done when it builds, its tests pass, and it has been exercised against the real
hardware.

## Non-negotiable safety rules (water-cooled loop)

Implemented in `DeviceMaster.Core.Safety` and enforced at the lowest write layer:

1. **Pump floor:** pump duty is never written below `SafetyLimits.PumpMinimumDutyPercent`
   (50%). Any error state ⇒ 100%.
2. **Sensor failure ⇒ failsafe:** if a curve's temperature source fails to read — including
   *implausible* values (LHM returns 0.0 °C for CPU when not elevated; treat ≤ 0 °C or
   > 115 °C as failure) — fans and pump go to 100%.
3. **Hardware fallback on crash/exit:** before shipping Stage 1, verify from
   FanControl.CorsairLink sources + live testing how the Link hub behaves when the host stops
   talking (keepalive interval, revert-to-hardware-mode timeout), and design writes so that
   "DeviceMaster died" leaves devices on hardware-default curves. Same question for the SL V3
   RX once its protocol is understood.
4. **Write gate:** no bytes are ever written to a device unless
   `KnownDeviceRegistry.IsWriteAllowed(vid:pid)` — positively identified, support-planned
   hardware only. Discovery itself is strictly read-only.
5. **Conflict check at startup:** warn if Corsair/Lian Li/Turing software is running
   (`ConflictingSoftwareChecker`), since two writers on one device is undefined behaviour.

## Sensor sources

- LHM (CPU/GPU temps, motherboard fans) — requires elevation for CPU temps.
- Coolant temperature comes from the Link hub's own sensor once Stage 1 lands — it is a
  first-class `ISensorSource`, usable as a curve input like any other.
