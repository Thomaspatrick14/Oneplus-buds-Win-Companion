# OnePlus Buds Pro 3 — Windows Controller (.NET)

A minimal Windows desktop app to control **OnePlus Buds Pro 3** over Bluetooth Classic from a PC, without the HeyMelody app.

> Python CLI version: [oneplus_buds3_pro_python](https://github.com/nic0manz/oneplus_buds3_pro_python)

---

## Screenshots

<p align="center">
  <img src="screenshot1.PNG" alt="Main window" width="360"/>
  &nbsp;&nbsp;
  <img src="screenshot2.PNG" alt="Custom EQ" width="360"/>
</p>

---

## Features

- **ANC** — Active Noise Cancellation, Transparency, Off; level control (High / Moderate / Low / Auto)
- **EQ presets** — Balanced, Bold, Serenade, Bass, DynAudio
- **Custom EQ** — create, select and delete custom 6-band EQ profiles stored on the earphones
- **BassWave™** — toggle and adjust bass enhancement (−5 to +5)
- **Dual Connection** — works perfectly in "Dual Connection" mode (connected to the phone and the PC at the same time)
- **Wear detection** — shows whether the earbuds are worn and, like the phone app, disables ANC controls (greyed out) while they're out of your ears
- **Battery** — live display for Left, Right and Case
- **Auto-connect** — connects to the first paired OnePlus Buds device on startup
- **Auto-reconnect** — silently reconnects when earbuds are put back in the case
- **System tray** — minimize to tray, restore with double-click

---

## Requirements

- Windows 10 / 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- OnePlus Buds Pro 3 paired via Windows Bluetooth settings

---

## Usage

1. Pair your earbuds in Windows Bluetooth settings
2. Run `OnePlusBudsPro3.exe`
3. The app auto-detects and connects to the first paired OnePlus Buds device
4. If auto-detect fails, select the device from the dropdown or type/paste the MAC address manually

---

## How it works

The Buds Pro 3 expose their control protocol over **Bluetooth Classic RFCOMM, channel 15** — the same serial channel the official HeyMelody app uses on Android. There is no public API: the protocol here was reverse-engineered by decompiling the HeyMelody APK and capturing live packet traces.

### Connection

The app opens a raw `AF_BTH` socket directly to the earbuds' MAC address (no WinRT / GATT involved) and performs a short handshake (`HELLO` + `REGISTER`). Because the earbuds support **dual connection**, this works while the phone stays connected too — the PC simply becomes a second controller.

After connecting, the app queries the current state once (ANC, EQ, BassWave, wear, custom EQ profiles, battery) so the UI reflects reality immediately.

### Live updates (no polling)

A dedicated background thread continuously reads the socket and splits the byte stream into frames. Whenever a setting changes — from this app, from the phone, or via a touch gesture on the earbuds — the earbuds **push** a notification, which the reader decodes and applies to the UI in real time. The only thing actively polled is the battery level, which isn't pushed.

Outgoing commands are confirmed by the earbuds' **ack** packet (a status byte; `0` = success), so the UI shows a real ✓ rather than an optimistic guess.

### Frame format

Every packet, in both directions, has the same shape:
```
AA <len> 00 00 <cmd_lo> <cmd_hi> <seq> <plen_lo> <plen_hi> [payload]
```
- `AA` — start byte; `len` — number of bytes that follow
- `cmd16` = function code, little-endian (e.g. fn 1030 = `0x0406` → bytes `06 04`)
- `cmd_hi` encodes the packet kind:
  - `01` request · `81` reply to a request
  - `04` set command · `84` set acknowledgement (with a status byte)
  - `02` / `05` unsolicited change notification
- `seq` — rolling sequence number · `plen` — payload length (little-endian) · `payload` — feature-specific bytes

### Key commands

| Feature | Get | Set | Notes |
|---|---|---|---|
| ANC mode / level | `0C 01` | `04 04` | mode + level; live state pushed as `04 02` (16-bit mask) |
| EQ preset | `0F 01` | `06 04` | Balanced / Bold / Serenade / Bass / DynAudio |
| Custom EQ list | `22 01` | `18 04` | create / select / delete 6-band profiles |
| BassWave value (−5…+5) | `24 01` | `1B 04` | |
| BassWave on/off | `0D 01` | `03 04` | feature id `0x1D`; read via `getFeatureSwitchStatus` (fn 269) |
| Wear status | `09 01` | — | `getEarBudsStatus`; live changes pushed as `04 02` |
| Battery | `06 01` | — | left / right / case, with charging state |

> ⚠️ Reverse-engineered and tested only against OnePlus Buds Pro 3. Other models use different function codes and payloads, so this won't work with them unmodified.

---

## Build

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

Produces a single `OnePlusBudsPro3.exe` in `out/`. Requires .NET 8 Desktop Runtime on the target machine.

---

## Project structure

```
OnePlusBudsPro3/
├── BudsConnection.cs    # Bluetooth socket, packet builders, response decoders
├── MainWindow.xaml      # UI layout (WPF)
├── MainWindow.xaml.cs   # UI logic
├── AboutWindow.xaml / AboutWindow.xaml.cs
├── App.xaml / App.xaml.cs
└── app.ico
```

---

## Credits

Created by Nico with help from AI-assisted coding tools.
