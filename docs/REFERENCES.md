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

## Lian Li UNI FAN SL V3 wireless (`0416:8040/8041` TX/RX, `1CBE:0005` fan nodes) — ✅ SOLVED

Protocol research completed 2026-07-06. Two MIT-licensed implementations exist; no USB
capture needed. (OpenRGB/liquidctl have nothing for these IDs — the classic `0CF2`
`LianLiUniHubController` does not apply.)

- **sgtaziz/lian-li-linux** (Rust, MIT, actively maintained) — primary. Full L-Connect 3
  replacement; README lists UNI FAN SL V3 (LCD/LED) wireless as tested: fan control, RGB,
  per-fan 400×400 LCD. Key files under `crates/`: `lianli-devices/src/wireless/*`
  (fan_speed.rs, rgb.rs, discovery.rs, bind.rs), `crypto.rs`, `slv3_lcd.rs`,
  `lianli-transport/src/usb.rs`.
- **phstudy/uni-wireless-sync** (Python, MIT) — independently confirms every packet layout;
  `src/uwscli/tinyuz.py` has a ~100-line pure-software "literal-only" tinyuz encoder the fan
  firmware accepts (port to C# for RGB instead of binding the C library).

Architecture (confirmed against our enumeration):
- **TX `0416:8040` = all control output** (fan PWM, RGB, bind, heartbeat): 64-byte WinUSB
  writes to EP 0x01 (read EP 0x81, interface 0). RF commands are 240-byte payloads split
  into 4× 60-byte chunks (`[0]=0x10, [1]=chunk_idx, [2]=channel, [3]=rx_type, [4..]=chunk`).
- **RX `0416:8041` = telemetry**: device list with MACs, RPMs (4× u16 BE at offset 28 of each
  42-byte record), current PWM, fan counts, effect index; also motherboard-PWM readback.
- **`1CBE:0005` fan nodes = per-fan LCD streaming only** (JPEG over USB bulk, 512-byte
  DES-CBC headers, key/IV `"slv3tuzx"`, cmd 0x65 push / 0x0E brightness / 0x0D rotate).
  Fan speed/RGB do NOT go through these.
- Fan PWM payload: `0x12 0x10`, device MAC [2-7], master MAC [8-13], rx_type [14],
  channel [15], seq [16], 4 PWM bytes [17-20]. PWM value 6 = motherboard-sync mode;
  SLV3 minimum duty 14% (clamp nonzero below 0.14×255).
- **Safety-relevant:** fans revert to their default speed if traffic stops — PWM must be
  re-sent every ≤1 s, plus a 1 Hz master-clock heartbeat (`0x12 0x14`). This means SL V3
  fans fail safe on app crash by design.
- RGB: raw per-LED frames (SLV3 = 40 LEDs/fan), tinyuz-compressed, chunked 220 bytes,
  firmware stores and loops the animation (send once, not per frame).
- Library: LibUsbDotNet or WinUSB P/Invoke — HidSharp cannot open these (vendor WinUSB class).

## Turzx 8.8" screen (`1A86:CA88`, COM3, serial id `CT88INCH`)

- **mathoudebine/turing-smart-screen-python** — primary. Its model detection maps USB
  serial/PID to protocol revision; find the 8.8" class (revision family for `CA88`/
  `CT88INCH`) and port init/orientation/bitmap-push commands.
- **tedd/Tedd.TuringScreen** (C#) — C# framing conventions, likely 3.5"-oriented; adapt.

## Sensor stack

- **LibreHardwareMonitorLib** — CPU/GPU/motherboard sensors (elevation required for CPU).
