# Supported devices

Hardware DeviceMaster can talk to, and the protocol facts that matter. Device identification
is strictly by USB VID/PID (`KnownDeviceRegistry`) — unrecognized devices are never written to.

## Corsair iCUE LINK

| Device | VID:PID | Transport |
|---|---|---|
| iCUE LINK System Hub | `1B1C:0C3F` | Composite HID (command interface = the one with output reports) |

- **Fans only.** Pump, coolant and pump-LCD support was removed: liquid-cooling chain devices
  (H-series/TITAN AIOs, XC7/XG3 water blocks, XD5/XD6 pumps, the XD5 Elite LCD module) are
  deliberately absent from `LinkDeviceCatalog`, so a hub whose chain carries one counts as
  having unrecognized devices and is never written to (it stays on its hardware curve). The
  pump/res LCD (`1B1C:0C43`) is no longer in the device registry, so nothing writes to it.
- Hub protocol: 512-byte packets, software/hardware mode, endpoint open/read/write sequence.
  Speed + telemetry ported from FanControl.CorsairLink; RGB from OpenLinkHub.
- **RGB quirks (fw 3.10.636):** the hub answers the color-endpoint open (`0x0d 0x00` + `0x22`)
  with error `0x03` even in software mode, yet subsequent color writes succeed. Treat that
  handshake as best-effort (the vendor references never check it); only fail on the write.
  The LED enumeration (endpoint `0x20`) likewise reports every channel as disconnected on
  this firmware — LED counts must come from the device catalog (`LinkDeviceCatalog.LedCount`,
  values from OpenLinkHub's metadata), which is also what OpenLinkHub itself does (it reads
  `0x20` only to adjust Commander Duo counts). Mixed QX/RX chains additionally need a black
  reset frame + 40 ms wait before the first real color packet (OpenLinkHub `setDeviceColor`).
- **LED registry (endpoint `0x1E`) — the hub's flash-persisted enrollment table.** Layout
  (read via the color handle, data type `0x0D 0x00`): `[6]` = slot count (max channel + 1),
  then per channel either `0x00` (no LED device) or `0x01` + the device's LED command code
  (read back live: RX MAX RGB fan = `0x19`). The color stream is consumed
  strictly by registry slot in ascending channel order — **a channel missing from the
  registry never lights**, regardless of what the color buffer carries, and entries with a
  wrong/zeroed command code don't light either.
- **Validate every read made through the color handle by its response data type**
  (`0x1E` registry = `0x0D 0x00`, `0x1D` LED count table = `0x0C 0x00`). The hub can answer
  with a foreign or stale packet, and its response buffers carry stale content after the
  status region — never interpret trailing bytes. Responses echo the command byte at raw
  offset 3, which is a usable desync check. An unvalidated registry read that got misparsed
  and **written back to flash progressively erased the hub's own LED enrollment** until the
  whole chain went dark (Aug 2026 incident) — so registry writes additionally require two
  consecutive reads agreeing on the same change and are rate-limited (`LedRegistryPlanner`).
- **LED power pulse (`0x15 0x01`)** makes the hub re-register its LED devices. On fw
  3.10.636 it returns error `0x07` when sent standalone, yet the same command succeeds
  immediately after an `0x1E` registry write. Channels erased from the registry do NOT
  re-enroll from pulses alone — recovery is an explicit registry write of the catalog
  command codes: `link ledreg --hub <serial-prefix> --channels <list>` (never writes zero
  or invented codes, then paints per-channel test colors for visual confirmation).
- **Color writes are ACKed with no readback of the painted state.** A chain device that
  resets its own LED controller after a link blip silently reverts to its firmware-default
  effect while the rest of the chain keeps its colors, and nothing in the protocol reveals
  this. The control loop therefore re-streams the current frame every 20 s
  (`ControlLoop.HubRgbRefreshMs`). Fan link blips manifest as tach loss, which the loop
  detects and heals (LED-power pulse + repaint when the tach returns). (A blipped pump was
  once seen ignoring re-streams until a full colour-path rebuild — `LinkHub.InvalidateColorPath`
  — but pumps are no longer driven.)
- Hardware-mode endpoint reads are rejected (error `0x03`) — enumeration and telemetry
  require software mode. Graceful exit must restore hardware mode (implemented).
- Chain devices are identified by (model, variant) bytes — see `LinkDeviceCatalog`. Anything
  not in the catalog (including every liquid-cooling model) aborts all speed writes to that hub.

## Lian Li UNI FAN SL V3 wireless

| Device | VID:PID | Transport |
|---|---|---|
| SL V3 wireless TX dongle (control) | `0416:8040` | WinUSB, 64-byte packets, EP 0x01/0x81 |
| SL V3 wireless RX dongle (telemetry) | `0416:8041` | WinUSB |

- The LCD fans' per-fan screen nodes (`1CBE:0005`) are not supported and not in the device
  registry — nothing writes to them (the fans themselves are driven through the dongles). A
  wireless AIO (device type 10/11) is never sent a PWM: pump control is not supported.

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
- Effect channels (`0x35`): onboard zone first, then one per ARGB header — except on
  `AULA3-AR32` firmware, where headers take channels `0..n-1` and onboard takes `n` (OpenRGB).
- The `0x36` effect-color mask is 16 bits: at most 16 effect slots (onboard LEDs + one per
  header) are addressable; zones beyond that are reported, not silently dropped.
- The headless daemon publishes the firmware name, raw config table and zone map in
  `/status.json` (`RgbDiagnostics`) — start there when a header doesn't light.

## MSI Mystic Light motherboard RGB (761-byte protocol) — not yet verified on hardware

| Device | VID:PID | Transport |
|---|---|---|
| Mystic Light controller (X870 / B850 / Z890 boards) | `0DB0:0076` | HID feature reports `0x50` / `0x51` |

- Protocol facts from OpenRGB's `MSIMysticLight761Controller`: one setup report `0x50`
  (290 bytes, 18 × 16-byte zone rows; JARGB 1–3 and JAF in direct mode, the rest cleared)
  per open, then one colour report `0x51` per header (727 bytes: `51 09 hdr0 hdr1 00 00 F0`
  + 240 × R,G,B; `hdr0` 0x04 JARGB with `hdr1` = header 0–2, 0x08 JAF).
- Headers driven: **JARGB 1–3 and JAF**. The 12 V JRGB header and onboard LEDs are not
  addressed by this protocol (OpenRGB leaves them off too).
- Nothing is written to controller flash: after a power cycle the board shows its BIOS/vendor
  effect until DeviceMaster runs. Static colours are re-sent every 30 s; the protocol has no
  built-in effects, so Rainbow is painted by DeviceMaster one step per control tick.
- `0DB0:0076` is shared by older Mystic Light generations (112/162/185-byte reports). Writes
  need all three: registry entry, the board's DMI name on the tested 761 list, and a report
  descriptor with the 761 layout (and no legacy-size `0x52` report). Anything else is skipped.

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
- Linux (headless): kernel SMBus ioctls (`I2C_SLAVE` + `I2C_SMBUS`) on the chipset adapters
  (`SMBus PIIX4 adapter …` / `SMBus I801 adapter …`) — these drivers are SMBus-only and reject
  `I2C_RDWR`. Plain `I2C_SLAVE` (never `_FORCE`), so addresses bound by `ee1004`/`spd5118`
  (the SPD EEPROMs) are refused by the kernel. Detection is the shared `EneDramScan`.

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
- **MSI boards with the NCT6687D(-R) EC (B840/B850/X870/X870E/Z890) on Linux:** system-fan headers
  refuse software duty (EIO, `dmesg`: "The device rejected new configuration as invalid") unless the
  out-of-tree `nct6687` driver is loaded with `msi_fan_brute_force=1`; the CPU header works either way.
  Verified on MAG X870E TOMAHAWK WIFI (MS-7E59). Full cause, fix, persistence (incl. Unraid) and
  sources: docs/LINUX.md → "MSI boards: system fans need `msi_fan_brute_force=1`" (#85).

## Turzx 8.8" V1.x (USB `1CBE:0088`, product "TURZX1.0") — Linux ✅, Windows ✅ v133 (#102)

- The newer hardware revision of the 8.8" panel: **one vendor-specific USB interface** (bulk EP 0x01 OUT /
  0x81 IN, 512-byte packets, USB 2.0 high speed) instead of the CA88 control port + gadget-serial data port
  below. Windows binds it to WinUSB (interface GUID `{88BAE032-5A81-49F0-BC3D-A4FF138216D6}`).
- **VID 1CBE is shared** (TI Luminary; Lian Li SL V3 fans use it too): DeviceMaster only opens a device whose
  product string is `TURZX1.0`.
- Protocol (`TurzxUsbProtocol`, ported from phstudy/turing-smart-screen-cli, MIT):
  - A 512-byte command block: a 500-byte header (cmd, 0, `1A 6D`, LE ms-since-local-midnight, payload)
    **DES-CBC encrypted, key = IV = `slv3tuzx`, PKCS7** (504 bytes), then zeros, then `A1 1A` in clear.
  - One 512-byte reply per command: byte 0 echoes the command, byte 1 = 200 on success.
  - Commands: SYNC 10 (firmware string at reply[8..40]), brightness 14 (0–100), JPEG frame 101
    (big-endian length at header bytes 8..11, the data appended after the block in the same write,
    1 MB max), stop video 111.
  - DES is implemented in managed code (`DesCbc`): OpenSSL 3 only has DES in its legacy provider.
- Verified 2026-11-20 (Linux, firmware `turzx_0001_0015`): the dashboard as a ~65 KB JPEG takes ~10 ms per
  frame (the serial revision needs ~2.3 s). Never reset the device in software; one reference project saw
  that stall the whole xHCI controller.
- Windows (v133): WinUSB (`WinUsbTurzxTransport`). The device must be opened with `FILE_FLAG_OVERLAPPED`, or
  `WinUsb_Initialize` fails with "The handle is invalid"; synchronous pipe calls still work on that handle. The app
  shows the gaming layout **CPU | FPS | GPU** (`TurzxDashboardFeed`, Skia renderer) at ~1 frame/s; verified with
  `DeviceMaster.App turzx usb` on firmware `turzx_0001_0010` (~21 ms per ~73 KB frame). Exit the app first:
  WinUSB lets one program hold the panel.
- Linux: usbfs, no libusb; needs root, a udev rule, or a privileged container with `/dev` mounted. Test with
  `devicemaster turzx-usb [--no-llm] [--brightness N] [--seconds N] [--flip]` while the loop leaves the
  screen Unmanaged.

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
  the driver's `WriteBufferSize`. A full frame takes ~2.3 s over this Full-Speed link; the push
  runs on a dedicated worker thread and never blocks the 1 Hz fan loop.
- **Partial updates** (rev_c `UPDATE_BITMAP`, `0xcc`) keep a live dashboard refreshing in ~1 s:
  after the first full frame each frame is diffed against the last in native row-major space and
  only the changed pixel spans are sent — each span is `[3-byte BE address = row*480 + col]
  [2-byte BE width][BGRA run]`, NULL-joined every 249 bytes with an `ef 69` trailer, under the
  header `cc ef 69 00 | (rawLen+2) BE3 | 00 00 00 | count BE4`. A full frame is re-sent every ~30
  partials (and whenever >50 % of the frame changes) to heal any region the panel dropped.
- Controlled from the **Turzx** side-menu page: Off / On, a brightness slider, and a landscape
  orientation toggle. **On** shows a fixed dashboard — a large FPS reading centred, with CPU and
  GPU telemetry rows (name, usage, temperature, memory GB, power W). The chip name is coloured
  (CPU red, GPU green by default), all other figures white.
- **FPS** is measured with **Intel PresentMon** (downloaded once to `%LocalAppData%\DeviceMaster`,
  or reused from a StarMaster install) — it captures the full present pipeline over ETW, so it
  reads every renderer including Vulkan and DX12 (a DXGI-only capture misses those). PresentMon is
  launched via `cmd … > csv` (it needs a console) and the `msBetweenPresents` column is tailed;
  FPS is the foreground process's rolling ~60-frame average. The app runs elevated, so no extra
  elevation is needed; the ETW session name is scoped so it never collides with StarMaster's
  PresentMon. Shows a dash when nothing is rendering.

## Sensors

- LibreHardwareMonitor for CPU/GPU/motherboard (elevation required for CPU temperatures).
- Fan curves follow CPU, GPU, or the hotter of the two (Hottest). A config that still says
  `Coolant` loads as Hottest with its Hottest curve.

---

Maintainers: machine-specific inventories (serials, MACs, chain maps of a given rig) belong
in `local/MACHINE-NOTES.md`, which is git-ignored — keep this file generic.
