# Protocol reference map

Open-source implementations we port from (study, don't reinvent). Cloned copies go under a
git-ignored `refs/` folder when needed.

## Corsair iCUE LINK System Hub (`1B1C:0C3F`)

- **EvanMulawski/FanControl.CorsairLink** (C#) — primary for Stage 1/3.
  `ICueLinkHubDevice` + `LinkHubDataReader`: handshake, sub-device enumeration (gives us the
  per-hub fan map), coolant temp, RPM reads, duty writes, and the keepalive/refresh cadence.
  C# → direct port.
- **jurkovic-nikola/OpenLinkHub** (Go) — RGB addressing per Link channel (Stage 2) and LCD
  image framing (Stage 4).

## Corsair pump/res LCD (`1B1C:0C43`, self-reports "XD5 ELITE LCD Pump")

- **OpenLinkHub** `lcd/` — JPEG frames pushed over HID output reports in 1024-byte chunks
  (matches this device's out=1024). Confirm the exact header bytes for this PID in Stage 4.

## Lian Li UNI FAN SL V3 wireless (`0416:8040/8041` TX/RX, `1CBE:0005` fan nodes) — ⚠ open

The classic OpenRGB `LianLiUniHubController` (VID `0CF2`, HID) does **not** cover this
hardware. All SL V3 devices are WinUSB. Stage 1 research order:
1. Check OpenRGB master/dev branches and open MRs for "SL V3" / "SLV3" / wireless support.
2. Check OpenLinkHub ecosystem & GitHub at large for SLV3 (the TX/RX names come from the
   devices themselves, so they are searchable).
3. Fallback: USBPcap/Wireshark capture of L-Connect in a throwaway VM, protocol from traffic.
Library: LibUsbDotNet (or hand-rolled WinUSB P/Invoke) — HidSharp cannot open these.

## Turzx 8.8" screen (`1A86:CA88`, COM3, serial id `CT88INCH`)

- **mathoudebine/turing-smart-screen-python** — primary. Its model detection maps USB
  serial/PID to protocol revision; find the 8.8" class (revision family for `CA88`/
  `CT88INCH`) and port init/orientation/bitmap-push commands.
- **tedd/Tedd.TuringScreen** (C#) — C# framing conventions, likely 3.5"-oriented; adapt.

## Sensor stack

- **LibreHardwareMonitorLib** — CPU/GPU/motherboard sensors (elevation required for CPU).
