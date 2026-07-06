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
- **RGB quirk (fw 3.10.636):** the hub answers the color-endpoint open (`0x0d 0x00` + `0x22`)
  with error `0x03` even in software mode, yet subsequent color writes succeed. Treat that
  handshake as best-effort (the vendor references never check it); only fail on the write.
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

## Turzx / Turing-family smart screens

| Device | VID:PID | Transport |
|---|---|---|
| 8.8" smart screen | `1A86:CA88` | USB serial (`usbser`), device serial string `CT88INCH` |

- Planned for Stage 5 via the shared rendering pipeline; protocol ported from
  turing-smart-screen-python / Tedd.TuringScreen (revision selected by USB serial string).

## Sensors

- LibreHardwareMonitor for CPU/GPU/motherboard (elevation required for CPU temperatures).
- Coolant temperature comes from the iCUE Link chain (see above).

---

Maintainers: machine-specific inventories (serials, MACs, chain maps of a given rig) belong
in `local/MACHINE-NOTES.md`, which is git-ignored — keep this file generic.
