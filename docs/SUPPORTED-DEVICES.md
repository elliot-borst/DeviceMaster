# Supported devices

Hardware DeviceMaster can talk to, and the protocol facts that matter. Device identification
is strictly by USB VID/PID (`KnownDeviceRegistry`) — unrecognized devices are never written to.

## Corsair iCUE LINK

| Device | VID:PID | Transport |
|---|---|---|
| iCUE LINK System Hub | `1B1C:0C3F` | Composite HID (command interface = the one with output reports) |
| Pump/res LCD module (XD5 Elite LCD family) | `1B1C:0C43` | Single HID interface, 1024-byte output reports |

- Hub protocol: 512-byte packets, software/hardware mode, endpoint open/read/write sequence.
  Speed + telemetry ported from FanControl.CorsairLink; RGB/LCD framing from OpenLinkHub.
- **RGB quirks (fw 3.10.636):** the hub answers the color-endpoint open (`0x0d 0x00` + `0x22`)
  with error `0x03` even in software mode, yet subsequent color writes succeed. Treat that
  handshake as best-effort (the vendor references never check it); only fail on the write.
  The LED enumeration (endpoint `0x20`) likewise reports every channel as disconnected on
  this firmware — LED counts must come from the device catalog (`LinkDeviceCatalog.LedCount`,
  values from OpenLinkHub's metadata), which is also what OpenLinkHub itself does (it reads
  `0x20` only to adjust Commander Duo counts). Mixed QX/RX chains additionally need a black
  reset frame + 40 ms wait before the first real color packet (OpenLinkHub `setDeviceColor`).
- Hardware-mode endpoint reads are rejected (error `0x03`) — enumeration and telemetry
  require software mode. Graceful exit must restore hardware mode (implemented).
- Chain devices are identified by (model, variant) bytes — see `LinkDeviceCatalog`.
  Model `0x0E` = XD5 Elite LCD display module as a chain device (no controllable speed).
- Pump-bearing chain devices (H-series, TITAN, XD5/XD6 family) are duty-floored and,
  until dedicated pump control ships, always driven at 100% in software mode.
- Coolant temperature is read from pump-bearing chain devices.

## Lian Li UNI FAN SL V3 wireless

| Device | VID:PID | Transport |
|---|---|---|
| SL V3 wireless TX dongle (control) | `0416:8040` | WinUSB, 64-byte packets, EP 0x01/0x81 |
| SL V3 wireless RX dongle (telemetry) | `0416:8041` | WinUSB |
| SL V3 per-fan LCD node | `1CBE:0005` | WinUSB bulk (LCD streaming only — Stage 5) |

- Windows binds these to WINUSB with device interface GUID
  `{1D4B2365-4749-48EA-B38A-7C6FDDDD7E26}`; DeviceMaster uses its own dependency-free
  WinUSB interop (no libusb).
- Control model: RF commands are 240-byte payloads chunked into 4× 64-byte USB packets via
  the TX dongle; device list (MACs, RPMs, PWM, fan counts) polled from the RX dongle.
- Bring-up order that works: probe master MAC/channel (`0x11`), TX reset (`0x11 0x08`) +
  500 ms, RX queries (`0x10 0x01 0x04 0x34/0x37/0x30`), then GetDev.
- **Dongle firmware 16 quirk:** GetDev with page count 1 is ignored; the poll escalates
  page counts (1→4) and caches the first that answers.
- Fan PWM is 0–255 with a 14% firmware floor for nonzero values; PWM value 6 = motherboard
  sync mode. PWM must be re-sent at least once per second (plus a 1 Hz master-clock
  heartbeat) — fans revert to firmware defaults when traffic stops, which makes them
  fail-safe on application crash.
- Classic wired Uni Hubs (VID `0CF2`) are recognized in the registry but not implemented.

## ASUS Aura motherboard RGB

| Device | VID:PID | Transport |
|---|---|---|
| Aura LED controller (mainboard class) | `0B05:19AF` | HID, 65-byte reports, `0xEC` prefix |

- Protocol ported from OpenRGB's `AuraMainboardController`: firmware query `0x82`, config
  table `0xB0` (onboard LED count at `[0x1B]`, ARGB header count at `[0x02]`), init
  `0x52 0x53 0x00 0x01`, effect select `0x35` + effect color `0x36`, direct `0x40`
  (20 LEDs/packet, `0x80` apply bit), commit `0x3F 0x55` (persists across reboot).
- Boards without onboard LEDs (config `[0x1B]` = 0) still use the mainboard protocol —
  never the `0x3B` addressable-only variant, even when only ARGB headers exist.

## RGB DRAM (ENE controllers — Klevv/Essencore, GSkill, Geil, …)

- ENE (Aura-compatible) controllers found by SMBus probing, not brand: unconfigured chips
  answer at `0x77` and are remapped one per stick (registers `0x80F8`/`0x80F9`) to free
  candidate addresses; a chip is confirmed by registers `0xA0..0xAF` reading `0x00..0x0F`.
  DeviceMaster fingerprints **before** the remap writes (stricter than the reference).
- ENE registers are 16-bit, tunnelled over SMBus (`0x00` = set pointer word, high byte
  first; `0x81` read; `0x01` write; `0x03` block ≤ 3 bytes). Colors are stored **R,B,G**
  per LED; static = mode `1` at `0x8021`, apply `0x80A0=0x01`, flash save `0x80A0=0xAA`.
  Version string at `0x1000` picks the register generation ("AUDA…" ⇒ colors at `0x8160`).
- Transport: RAMSPDToolkit's SMBus transactions over LibreHardwareMonitor's PawnIO driver
  (AMD FCH exposes two ports; both are scanned). SPD EEPROM addresses are never written.
  All transactions hold the `Global\Access_SMBUS.HTP.Method` mutex.

## NVIDIA GPU RGB (board-partner controllers)

- Board partner identified via `NvAPI_GPU_GetPCIIdentifiers` (PCI subsystem vendor).
  ASUS cards (subvendor `0x1043`) carry the same ENE controller on GPU I2C port 1 at
  address `0x67` — driven through `NvAPI_I2CWriteEx/ReadEx` (user mode, no extra driver).
  ASUS GPU firmware ("AUMA0-E6K5-11xx") stores its LED count at config offset `0x03`.
- Other partners (MSI/Gigabyte/Zotac/Palit/PNY/NVIDIA FE) are detected and named but not
  yet driven; each needs its own controller port when such a card is available.

## Motherboard fan headers + GPU fans

- LibreHardwareMonitor `Control` sensors: SuperIO chips (Nuvoton NCT67xx on ASUS AM5) and
  NVIDIA coolers, written as percent via `Control.SetSoftware`, restored to BIOS/driver
  automatic control via `SetDefault` on exit.
- **Requires administrator + the PawnIO kernel driver** (pawnio.eu, installed system-wide;
  LHM 0.9.6 no longer bundles WinRing0). Without either, header control is skipped.
- Safety: SuperIO has no hardware fallback — a killed process leaves the last duty in
  place — so header/GPU duties are floored at 30% (`SafetyLimits.HeaderMinimumDutyPercent`)
  and every touched control is restored on shutdown.

## Turzx / Turing-family smart screens

| Device | VID:PID | Transport |
|---|---|---|
| 8.8" smart screen — control port | `1A86:CA88` | USB serial (`usbser`), device serial string `CT88INCH` |
| 8.8" smart screen — **data port** | `0525:A4A7` (Linux `g_serial`) | USB serial CDC-ACM; also seen as serial `20080411` / `1D6B:0121` / `1D6B:0106` |

- **Two serial ports (this was the whole trick).** The 8.8" panel enumerates *two* COM ports.
  The `1A86:CA88` `CT88INCH` port is only a standby/control endpoint — it never answers the
  protocol and stalls on a frame write. A second Linux gadget-serial port (the panel SoC
  re-enumerated as `g_serial`, identified by `0525:A4A7` / serial `20080411` / `1D6B:0121|0106`)
  is the **data endpoint** that speaks the protocol and replies to `HELLO` with e.g.
  `chs_88inch.dev1_rom1.90`. DeviceMaster drives the data port; the data port's generic gadget
  VID:PID is not write-allowed on its own — it is only opened when the `CA88` control port is
  co-present (that is the positive identification). Windows often labels the data port
  "PI USB to Serial" (it looks like a Raspberry Pi gadget) — do not dismiss it.
- If only the control port is present, opening then closing it wakes the SoC and the data port
  enumerates; DeviceMaster does this automatically and waits for the data port to appear.
- Protocol is turing-smart-screen-python `lcd_comm_rev_c.py`, the `REV_8INCH` sub-revision
  (verified byte-for-byte against a vendor Bus Hound capture). Commands are padded to a multiple
  of 250 bytes; a full frame is BGRA pixel data with a `0x00` inserted after every 249 bytes,
  wrapped `PRE_UPDATE_BITMAP → START_DISPLAY_BITMAP → DISPLAY_BITMAP_8INCH → payload →
  QUERY_STATUS`. The panel is 480×1920 native (portrait); DeviceMaster renders landscape
  1920×480 content and rotates it on. Baud is nominal (CDC transfers at USB speed).
- **The ~3.7 MB frame must be written in paced ~24,900-byte chunks (≈1 ms apart), never one
  blast** — a single large write stalls the CDC endpoint ("semaphore timeout"). Do not enlarge
  the driver's `WriteBufferSize`. A full frame takes ~2.3 s; the push runs on a dedicated worker
  thread and never blocks the 1 Hz fan/pump loop. Partial/differential updates (rev_c
  `UPDATE_BITMAP`, or the vendor's command `0xCC`/204) remain a future optimisation.
- Controlled from the **Turzx** side-menu page: Off / On (metrics) / Black / White, a metric
  picker, a brightness slider, and a landscape orientation toggle.

## Sensors

- LibreHardwareMonitor for CPU/GPU/motherboard (elevation required for CPU temperatures).
- Coolant temperature comes from the iCUE Link chain (see above).

---

Maintainers: machine-specific inventories (serials, MACs, chain maps of a given rig) belong
in `local/MACHINE-NOTES.md`, which is git-ignored — keep this file generic.
