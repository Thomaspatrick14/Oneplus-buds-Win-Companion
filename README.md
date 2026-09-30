# OnePlus Buds — Windows Companion (.NET)

A native, lightweight Windows desktop companion application to control and monitor **OnePlus Buds 4** and **OnePlus Buds Pro 3** earphones over Bluetooth Classic RFCOMM from a PC, without requiring an Android phone or the HeyMelody app.

> [!NOTE]
> **Special Thanks & Origin**: This application is built upon the pioneering reverse-engineering foundation created by **[Nico (@nic0manz)](https://github.com/nic0manz/oneplus_buds3_pro_dotnet)** for the OnePlus Buds Pro 3.

Maintained and rewritten by [Thomaspatrick14](https://github.com/Thomaspatrick14/Oneplus-buds-Win-Companion) with AI assistance.

<p align="left">
  <a href="https://github.com/Thomaspatrick14/Oneplus-buds-Win-Companion/releases/latest">
    <img src="https://img.shields.io/badge/Download_Latest_Release-v1.1.1-blue?style=for-the-badge&logo=windows" alt="Download v1.1.1" />
  </a>
</p>

---

## Unified Multi-Device Support

This application automatically adapts its interface and feature set based on the connected device:

| Feature | OnePlus Buds 4 Profile | OnePlus Buds Pro 3 Profile |
|---|---|---|
| **Activation** | Default / Selected device name does not contain "3 Pro" | Selected device name contains `"3 Pro"` |
| **Noise Control Modes** | **Noise Cancellation**, **Adaptive**, **Transparency**, **Off** | **ANC**, **Transparency**, **Off** |
| **ANC Levels** | High, Moderate, Low, Auto | High, Mid, Low, Auto |
| **Sound Master EQ** | Balanced, Clear Vocals, Bass | Balanced, Bold, Serenade, Bass, DynAudio |
| **BassWave™** | Supported (−5 to +5 slider & toggle) | Supported |
| **Custom 6-Band EQ** | Supported (Slots, custom name, flash memory) | Supported |
| **Earbud Controls (Gestures)** | Full Left & Right customization (at the bottom) | Pro 3 defaults |
| **Live Battery & In-Ear Wear** | Left, Right, Case battery & optical wear status | Left, Right, Case battery & optical wear status |

---

## Screenshots

<p align="center">
  <img src="screenshot1.PNG" alt="OnePlus Buds Noise & Sound Controls" width="45%" />
  &nbsp;&nbsp;
  <img src="screenshot2.PNG" alt="OnePlus Buds EQ & Earbud Controls" width="45%" />
</p>

---

## Key Features

- **Dynamic Dual-Model Profile Switching**:
  - Automatically activates the correct protocol mapping and UI layout depending on whether you connect a **OnePlus Buds 4** or **OnePlus Buds Pro 3**.
  - Dropdown device selector lets you switch between paired devices effortlessly.
- **Noise Control & Live Two-Way Sync**:
  - Full mode switching: **Noise Cancellation**, **Adaptive**, **Transparency**, and **Off**.
  - Level selection: **High**, **Moderate**, **Low**, and **Auto**.
  - **Live Earbud Synchronization**: When ANC is toggled via the physical stem pinch on either earbud, the Windows app detects the hardware gesture event and updates the active mode on screen instantly.
- **Sound Master EQ**:
  - Buds 4 presets: **Balanced**, **Clear Vocals**, and **Bass**.
  - Buds Pro 3 presets: **Balanced**, **Bold**, **Serenade**, **Bass**, and **DynAudio**.
- **BassWave™**:
  - Dynamic low-frequency enhancement toggle with continuous adjustment slider (−5 to +5).
- **Custom 6-Band EQ**:
  - Interactive 6-band equalizer (62 Hz, 250 Hz, 1 kHz, 4 kHz, 8 kHz, 16 kHz).
  - Create, apply, select, and delete custom profiles stored in the earbuds' onboard memory.
- **Earbud Controls (Gestures)**:
  - Conveniently placed as the last section for a clean, streamlined layout.
  - Independent customization for **Left** and **Right** earbuds with quick-switch tabs.
  - **Media Controls (When not on a call)**:
    - **Single-tap**: `Play / Pause`, `None`
    - **Double-tap**: `Next track`, `Previous track`, `Play / Pause`, `Voice Assistant`, `None`
    - **Triple-tap**: `Voice Assistant`, `Previous track`, `Next track`, `None`
    - **Slide**: `Volume control`, `None`
    - **Touch & hold**: `Noise control`, `Voice Assistant`, `None`
  - **Call Controls (When on a call)**:
    - **Double-tap**: `Answer / End call`, `None`
    - **Long touch & hold**: `Decline call`, `None`
- **In-Ear Optical Wear Detection**:
  - Real-time wear status (`Worn` / `Not worn`). Noise control automatically switches to Off when earbuds are removed.
- **Live Battery Levels**:
  - Real-time battery percentages for Left earbud, Right earbud, and Charging Case.
- **Modern Borderless Dark Theme**:
  - Fully integrated with Windows Desktop Window Manager (`DWMWA_USE_IMMERSIVE_DARK_MODE` and `WindowChrome`) for seamless dark borders without white title bars.
- **Dual Connection / Multipoint**:
  - Fully compatible with Dual Connection mode (connects to PC and smartphone simultaneously).
- **Live Bluetooth Packet Monitor**:
  - Built-in live RFCOMM packet sniffer (`Log` button in the title bar) to inspect raw hex frames, filter traffic, and test custom commands.
- **Connection & Disconnection Notifications**:
  - Instant System Tray balloon notifications showing full battery status (Left, Right, Case) whenever your earbuds connect or disconnect from Windows.
- **Auto-Connect, System Tray & Startup**:
  - Automatically connects on startup and reconnects if Bluetooth drops. Minimizes cleanly to the notification tray.
  - Option to launch minimized on Windows startup conveniently located inside the **About** (`?`) dialog.
- **Single Instance & Clean Termination**:
  - Global mutex prevents multiple running instances; cleanly terminates all threads on exit without leaving lingering background processes.

---

## Requirements

- Windows 10 / Windows 11 (64-bit)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- OnePlus Buds 4 or OnePlus Buds Pro 3 paired in Windows Bluetooth Settings

---

## Usage

1. Pair your **OnePlus Buds 4** or **OnePlus Buds Pro 3** in Windows (`Settings` → `Bluetooth & devices` → `Add device`).
2. Run [`OnePlusBuds.exe`](file:///c:/Users/up650/Documents/vsc/oneplus_buds3_pro_dotnet/OnePlusBuds.exe).
3. The app will automatically detect your paired earbuds and connect.
4. If you have multiple devices paired, choose your model from the dropdown (selecting "3 Pro" switches to the Buds Pro 3 layout).

---

## How It Works

The OnePlus Buds family exposes its proprietary control protocol over **Bluetooth Classic RFCOMM, channel 15** — the same serial communication protocol utilized by the official HeyMelody mobile application.

### Handshake & Initialization

The application establishes an `AF_BTH` socket directly to the earphone's Bluetooth MAC address:
1. `HELLO` handshake: `AA 07 00 00 00 01 23 00 00 12`
2. `REGISTER` handshake: `AA 0C 00 00 00 85 41 05 00 00 B5 50 A0 69`
3. Full state query: pulls current ANC state, EQ preset, BassWave values, wear state, custom EQ list, and battery.

### Live Push Protocol & Gesture Sync

A dedicated background thread continuously reads incoming RFCOMM packets:
- **Phone / App Changes**: External changes carry `cmd 0x05`, triggering a throttled full-state synchronization.
- **Stem Pinch / Touch Gestures**: Hardware touch gestures on the stems push event frames (`cmd 04 02` with tag `0xF1`). The application immediately handles these events and refreshes the ANC state after the prompt tone transition (350ms delay compensation).
- **Heartbeat Polling**: A gentle 4-second background timer keeps battery and ANC state synchronized even over long listening sessions.

### Protocol Reference

| Feature | Query (Cmd) | Set (Cmd) | Details |
|---|---|---|---|
| **Earbud Gestures** | `08 01` | `08 04` | 18 gesture mappings across Left & Right (`[side] [cat] [gid] [aid]`) |
| **ANC Mode & Level** | `0C 01` | `0C 04` | Mode (ANC / Adaptive / Trans / Off) & Level (High / Mid / Low / Auto) |
| **Sound Master EQ** | `0F 01` | `0F 04` | Presets (`0x00`=Balanced, `0x01`=Clear Vocals / Bold, `0x02`=Bass...) |
| **BassWave™ Enable** | `0D 01` | `0D 04` | Feature Switch ID `0x1D` (Fn 269) |
| **BassWave™ Value** | `24 01` | `24 04` | Signed range (−5 to +5) |
| **Custom EQ Profiles** | `22 01` | `22 04` | Read, create, select, and delete 6-band user profiles |
| **In-Ear Wear Detection** | `09 01` | — | Optical sensor states (Left, Right, Case); pushed as `04 02` |
| **Battery Status** | `06 01` | — | Left, Right, and Case levels with charging flags (`06 81`) |

---

## Building from Source

Prerequisites: [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

### Build Single-File Release Executable
```powershell
dotnet publish OnePlusBuds4/OnePlusBuds4.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

The resulting standalone executable `OnePlusBuds.exe` is generated in `out/` and root.

---

## Project Structure

```
OnePlusBuds/
├── OnePlusBuds.sln          # Visual Studio / VS Code C# Dev Kit solution
├── OnePlusBuds.exe          # Compiled single-file Windows executable
├── OnePlusBuds4/
│   ├── BudsConnection.cs    # RFCOMM socket, protocol framing, packet encoders & decoders
│   ├── MainWindow.xaml      # Modern borderless dark WPF UI (dual-profile dynamic layout)
│   ├── MainWindow.xaml.cs   # UI event dispatching, gesture/ANC sync & tray integration
│   ├── PacketLogWindow.xaml # Real-time Bluetooth RFCOMM packet monitor UI
│   ├── PacketLogWindow.xaml.cs # Live log capture, filtering, and custom packet transmitter
│   ├── AboutWindow.xaml     # About modal and attribution
│   ├── AboutWindow.xaml.cs  # About window logic
│   ├── App.xaml / App.xaml.cs # Application entrypoint & single-instance Mutex
│   ├── app.ico              # Application icon
│   └── OnePlusBuds4.csproj  # Build definition, single-file publish & metadata
```

---

## What's New in v1.1.1

- **Boot Auto-Connect & Continuous Background Watcher**:
  - Resolved an issue where starting minimized on Windows boot with offline earbuds left the app dormant. The reconnect watcher now runs continuously in the background upon login.
- **Instant Hardware PnP Arrival Hook (`WM_DEVICECHANGE`)**:
  - Listens for Windows PnP device notifications (`DBT_DEVNODES_CHANGED` / `DBT_DEVICEARRIVAL`) to connect the millisecond Windows detects earbud connection.
- **Multi-Tier Bluetooth Connection Detection**:
  - Iterates all physical Bluetooth radio handles (`BluetoothFindFirstRadio`) with a fallback to Windows Multimedia Audio Endpoints (`winmm.dll`).
- **Explorer Shell Taskbar Re-anchoring (`TaskbarCreated`)**:
  - Ensures the tray icon and its balloon notifications remain anchored if Windows Explorer finishes loading after startup.

---

## What's New in v1.1.0

- **Connection & Disconnection Notifications**:
  - System Tray balloon notifications showing live battery levels for Left, Right, and Case whenever earphones connect or disconnect.
- **Relocated Startup Settings**:
  - Moved the "Start minimized with Windows" checkbox into the **About** (`?`) window to keep the main device card clean.
- **Clean Background Process Handling**:
  - Single-instance mutex and deterministic cleanup ensure zero leftover zombie processes on close/exit.
- **Single Balloon Debounce**:
  - Debounced notification dispatch to prevent duplicate popups upon telemetry reception.

---

## Credits & Acknowledgments

- **Original Project**: Forked from [nic0manz/oneplus_buds3_pro_dotnet](https://github.com/nic0manz/oneplus_buds3_pro_dotnet) created by **Nico**.
- **Rewritten for OnePlus Buds 4 & Unified Profiles**: Maintained and rewritten by **[Thomaspatrick14](https://github.com/Thomaspatrick14/Oneplus-buds-Win-Companion)**.
- **AI Assistance**: Reverse engineering, protocol discovery, dual-model architecture, gesture implementation, and modern UI enhancements were developed with **AI assistance**.

---

## License

This project is licensed under the same open terms as the original repository. Provided for personal and educational use. Not affiliated with or endorsed by OnePlus or Oppo.
