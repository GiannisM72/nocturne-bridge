# Nocturne Bridge

Free companion app for **Nocturne Deck**, the hi-fi music widget for the CORSAIR XENEON EDGE (iCUE) by **GM Edge Labs**.

iCUE widgets only receive the song title and artist. Nocturne Bridge adds everything else:

- **Pick your player** — Spotify, YouTube / any browser, AIMP (also classic versions), VLC, foobar2000, Apple Music, TIDAL, Deezer and more
- **Album art**
- **Real 48-band spectrum and VU/PPM meters** — whole PC or only the selected player
- **Play state, track position and tap-to-seek**
- **Volume knob, Mute and 100%**

## Download

➡️ **[Download NocturneBridge.zip](../../releases/latest/download/NocturneBridge.zip)** (latest release)

## Setup

1. Unzip into a folder you keep, e.g. `Documents\Nocturne Deck\Nocturne Bridge`.
2. Double-click `NocturneBridge.exe`.
3. If Windows shows **"Windows protected your PC"**, click **More info**, then **Run anyway** (see below).
4. A small bar-graph icon appears next to the clock. It starts automatically with Windows from now on.

Right-click the tray icon for **Start automatically**, **Show status page** and **Exit**.

## About the Windows message

Windows SmartScreen shows this for new programs from small, independent makers without a paid code-signing certificate. It is a notice about an unknown publisher, not a virus finding.

- Listens on `http://localhost:8977` only — nothing outside your PC can reach it, and it never connects to the internet.
- No account, no ads, no tracking, no installer, no admin rights.
- Audio is analysed live for the spectrum and never recorded or stored.
- The full source is in this repository: [`NocturneBridge.cs`](NocturneBridge.cs).
- Build it yourself: download the source, run `Build-it-yourself.cmd` (uses the C# compiler that ships with Windows). A program you build yourself shows no warning.
- Check the download: `certutil -hashfile NocturneBridge.exe SHA256` and compare with `SHA256.txt` in the release.

## What it writes on your PC

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `NOCTURNE DECK Bridge` (only while "Start automatically" is ticked)
- `HKCU\Software\NocturneDeckBridge` (first-run flag)

## Uninstall

Right-click the tray icon → untick **Start automatically** → **Exit**. Delete the folder.

## Requirements

- Windows 10 or 11 (per-player visualizer: Windows 10 version 2004 or newer)
- .NET Framework 4.8 (built into Windows 10/11)

## Local API (for the widget)

| Endpoint | Returns |
|---|---|
| `/sessions` | all players, the current one, volume |
| `/control?app=&cmd=` | `play`, `pause`, `toggle`, `stop`, `next`, `prev` |
| `/seek?app=&pos=` | jump to a position (seconds) |
| `/volume?app=&set=&mute=` | volume 0–100, mute on/off/toggle |
| `/toggle?app=aimp-remote&what=` | AIMP repeat / shuffle |
| `/art?app=` | album art image |
| `/levels`, `/levels/stream` | meters and 48 spectrum bands (JSON / Server-Sent Events) |
| `/debug` | status page |

---

Nocturne Deck and Nocturne Bridge are independent products by GM Edge Labs and are not affiliated with Corsair, Elgato, Microsoft, AIMP, Spotify or any media service.
