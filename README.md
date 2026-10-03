# Nocturne Bridge

Free companion app for **Nocturne Deck**, the hi-fi music widget for the CORSAIR XENEON EDGE (iCUE) by **GM Edge Labs**.

iCUE widgets only receive the song title and artist. Nocturne Bridge adds everything else:

- **Pick your player** — Spotify, YouTube / any browser, AIMP (also classic versions), VLC, foobar2000, Apple Music, TIDAL, Deezer and more
- **Album art**
- **Real 48-band spectrum and VU/PPM meters** — whole PC or only the selected player
- **Play state, track position and tap-to-seek**
- **Volume knob, Mute and 100%**
- **Playlist, track select, shuffle and repeat** for AIMP, VLC and foobar2000
- **Mixer** — volume per program and the output device

## Download

➡️ **[Download NocturneBridge.zip](../../releases/latest/download/NocturneBridge.zip)** (latest release)

## Setup

1. Unzip into a folder you keep, e.g. `Documents\Nocturne Deck\Nocturne Bridge`.
2. Double-click `NocturneBridge.exe`.
3. If Windows shows **"Windows protected your PC"**, click **More info**, then **Run anyway** (see below).
4. A small bar-graph icon appears next to the clock. The first time, it asks once whether it should start automatically with Windows. You can change your answer at any time.

Right-click the tray icon for **Start automatically** (off until you say Yes), **Show status page** and **Exit**.

## VLC and foobar2000

AIMP needs nothing. These two need one setting in the player, once.

**VLC** (it does not report to Windows at all, so without this it is not a source):

1. VLC → **Tools → Preferences**, bottom left **Show settings: All**.
2. **Interface → Main interfaces**: tick **Web**.
3. **Interface → Main interfaces → Lua**: under **Lua HTTP** type any **Password**.
4. **Save**, close VLC and start it again.

The bridge reads the port and that password from VLC's own settings file (`%APPDATA%\vlc\vlcrc`) and talks to VLC on `127.0.0.1` only. VLC then appears as a source with title, position, cover, playlist, track select, shuffle and repeat.

**foobar2000** (it already is a source; this adds playlist, track select, shuffle / repeat and the audio details):

1. Install the **Beefweb Remote Control** component (`foo_beefweb`, foobar2000 1.6 or newer) from the foobar2000 components page.
2. Restart foobar2000. Leave its port at **8880**.

foobar2000 has one "playback order", so Shuffle and Repeat replace each other there.

Only if you changed a port, or set a Beefweb user and password, put a `NocturneBridge.ini` next to `NocturneBridge.exe`:

```ini
[vlc]
port=8080
password=your VLC web password
; enabled=off

[foobar2000]
port=8880
user=
password=
; enabled=off
```

`Show status page` (tray icon) tells you under `vlc` and `foobar2000` whether each one is connected, and why not.

## About the Windows message

Windows SmartScreen shows this for new programs from small, independent makers without a paid code-signing certificate. It is a notice about an unknown publisher, not a virus finding.

- Listens on `http://localhost:8977` only — nothing outside your PC can reach it, and it never connects to the internet. The only connections it opens itself go to VLC and foobar2000 on this same PC (`127.0.0.1`), and only after you switched those on in the player.
- No account, no ads, no tracking, no installer, no admin rights.
- Audio is analysed live for the spectrum and never recorded or stored.
- The full source is in this repository: [`NocturneBridge.cs`](NocturneBridge.cs).
- Build it yourself: download the source, run `Build-it-yourself.cmd` (uses the C# compiler that ships with Windows). A program you build yourself shows no warning.
- Check the download: `certutil -hashfile NocturneBridge.exe SHA256` and compare with `SHA256.txt` in the release.

## What it writes on your PC

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `NOCTURNE DECK Bridge` (only while "Start automatically" is ticked; nothing is written unless you answer Yes to the one-time question or tick it)
- `HKCU\Software\NocturneDeckBridge` (first-run flag)

## Why some virus scanners flag it

The program is small, new and has no paid code-signing certificate, so a few scanners that guess from behaviour ("machine learning" and "heuristic" verdicts, not named malware) may flag it. It closes an older copy of itself when you update, keeps a local web server on `localhost`, and can start with Windows, which are things such scanners watch for. Please check the source, build it yourself with `Build-it-yourself.cmd`, or compare the SHA256.

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
| `/toggle?app=&what=&set=` | repeat / shuffle (AIMP, VLC, foobar2000): flip, or `set=on` / `set=off` |
| `/playlist?app=&since=` | the playlist that player plays from, with the index of the playing track (read-only): AIMP 4/5, VLC, foobar2000. Sources that can do it carry `"playlist": true` in `/sessions` |
| `/jump?app=&index=` | play that track of the playlist |
| `/mixer` | every program that plays sound: volume, mute, live level; and the output devices |
| `/mixer/set?app=&volume=&mute=` | volume 0–100 and mute on/off/toggle for one program |
| `/output?set=` | make an output device the default |
| `/art?app=` | album art image |
| `/levels`, `/levels/stream` | meters and 48 spectrum bands (JSON / Server-Sent Events) |
| `/debug` | status page |

---

Nocturne Deck and Nocturne Bridge are independent products by GM Edge Labs and are not affiliated with Corsair, Elgato, Microsoft, AIMP, Spotify or any media service.
