# Hardware inventory

Result of the USB/HID/serial enumeration on the target machine, 2026-07-06 (Windows 11,
AMD Ryzen 7 9800X3D, NVIDIA RTX 5090). Re-run any time with:
`dotnet run --project src/DeviceMaster.App -- discover`

## Corsair iCUE LINK

| Device | VID:PID | Count | Transport | Serials |
|---|---|---|---|---|
| iCUE LINK System Hub | `1B1C:0C3F` | **2** | Composite HID (usbccgp) | `16EEFF3840C5F252B24349A5FC29F7B7`, `C9FB9CB3C3F9B251B0C47D1252CEC44A` |
| Pump/res LCD module | `1B1C:0C43` | 1 | Single HID (HidUsb) | `A9STT538001226` |

- Each hub exposes two HID interfaces: `MI_00` in=513/out=513 (command channel — matches
  FanControl.CorsairLink's 512-byte packets + report id) and `MI_01` in=33/out=0
  (notification/input channel).
- The LCD self-reports product string **"XD5 ELITE LCD Pump"** (owner believed XD7 — reconcile;
  possibly an XD5 Elite LCD unit, or shared firmware identity). Report sizes in=512 **out=1024**
  feature=32 match OpenLinkHub's LCD image framing (1024-byte chunks).
- It is a **direct USB device**, not reached through the Link hub — Stage 4 talks to it directly.

### Link chain map (live enumeration via our protocol port, 2026-07-06, both hubs fw 3.10.636)

| Hub | Channel | Device | Notes |
|---|---|---|---|
| `16EEFF38…` | 1, 2, 3, 14 | RX MAX RGB Fan ×4 | ch1 and ch14 never report RPM (investigate — possibly stacked/secondary fans in a magnetized group); ch2/ch3 report ~1200 RPM |
| `C9FB9CB3…` | 13 | XD5 Elite LCD display module | chain model `0x0E` (identified via OpenLinkHub lsh.go type 14); excluded from speed writes |
| `C9FB9CB3…` | 14 | Pump (model `0x19`, "XD6" in FanControl's catalog) | **coolant temperature sensor** (23.6 °C at scan) + pump RPM (~2180 hw mode); LCD USB device says "XD5 ELITE LCD Pump", owner says XD7 — physical identity still to reconcile, treatment identical (pump) |

- Hardware-mode endpoint reads are rejected (error `0x03`) — chain enumeration and telemetry
  require software mode. Verified live: fixed-duty writes work (fans 65% → RPM ramp observed),
  pump channel force-held at 100%, `EnterHardwareMode` cleanly returns the hub to its own curves.

## Lian Li — UNI FAN SL V3 *wireless* ecosystem (not a classic Uni Hub!)

| Device | VID:PID | Count | Driver |
|---|---|---|---|
| SLV3TX (wireless transmitter) | `0416:8040` | 1 | WinUSB |
| SLV3RX (wireless receiver) | `0416:8041` | 1 | WinUSB |
| SLV3 fan node (one per fan) | `1CBE:0005` | **11** | WinUSB |

- VID `0416` = Nuvoton, `1CBE` = TI/Luminary — Lian Li did not use its classic `0CF2` VID here.
- Five `1A86:8091` (WCH) generic USB hubs also enumerate — almost certainly the internal
  daisy-chain fabric that fans the 11 fan nodes out.
- Fan node serials: `0B913822D5160A66`, `14E3F709651F17E6`, `522AEAB205160E66`,
  `53E95B0A651F17E2`, `6D1AA9A035160E66`, `871409A155160F66`, `9343DC2025170C66`,
  `944B446125170C66`, `A2DA4E8B421F14E6`, `BCF30D23E7160A66`, `CAABC42305170B66`.
- **Protocol risk:** OpenRGB's Uni Hub controllers target the classic `0CF2` HID hubs and do
  NOT apply. All SL V3 devices are WinUSB, so this layer needs LibUsbDotNet/WinUSB, and the
  protocol must be researched in Stage 1 (current OpenRGB dev branch, other OSS projects, or a
  USBPcap capture of L-Connect before/if it is reinstalled in a VM).
- WinUSB bindings survived the L-Connect uninstall (verified 2026-07-06).

### SL V3 wireless fan group map (live telemetry via our protocol port, 2026-07-06)

TX dongle master MAC `54440e7a4ee0`, RF channel 8, dongle firmware 16. Four bound groups
(3+3+2+3 = the 11 fans), all idling at PWM 160/255 (~63%, the firmware default curve):

| Group MAC | Fans | RX type |
|---|---|---|
| `6f1a107a4ee0` | 3 | 5 |
| `c929107a4ee0` | 3 | 6 |
| `562a107a4ee0` | 2 | 1 |
| `5f00117a4ee0` | 3 | 4 |

- Fan-type byte `0x19` (25) ⇒ SL V3 **LCD** models — matches the 11 per-fan LCD USB nodes.
- Verified live: 80% command ramped all groups ~1220 → ~1510 RPM; releasing the keepalive
  returns fans to firmware defaults within seconds (crash-safe by design).
- **Firmware 16 quirk:** the RX ignores GetDev with page count 1 — only answers from 2 up.
  Bring-up order that works: TX reset (`11 08`) + 500 ms, RX queries (`10 01 04 34/37/30`),
  then GetDev with escalating page counts.

## Turzx smart screen

| Device | VID:PID | Port | Device serial |
|---|---|---|---|
| Turzx/Turing-family 8.8" screen | `1A86:CA88` | COM3 (usbser) | `CT88INCH` |

- `CT88INCH` ⇒ 8.8-inch panel. Match against turing-smart-screen-python's model detection in
  Stage 5 to pick the right protocol revision.

## Present but out of scope (never written to)

- `0525:A4A7` "PI USB to Serial" on COM4 — looks like a Raspberry Pi in gadget-serial mode;
  assumed unrelated to this project.
- `0B05:19AF` ASUS Aura LED controller (motherboard RGB).
- Keyboards/mice (`05AC`, `373B`), game controllers (`231D`), Bluetooth, generic hubs.

## Vendor software status

iCUE and L-Connect were uninstalled 2026-07-06. `discover` now reports **no conflicting
services or processes** (previously running: iCUE, CorsairCpuIdService, iCUEUpdateService,
L-Connect-Service, L-Connect-Service-Watcher). The startup check stays in the app permanently.
