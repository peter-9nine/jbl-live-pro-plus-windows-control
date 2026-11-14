# Live Pro+ Control

A Windows desktop app to control **JBL LIVE PRO+ TWS** earbuds — the features of the
*JBL Headphones* Android app, on your PC.

It talks to the earbuds directly over Bluetooth LE using the protocol the official app uses
(documented in [`docs/PROTOCOL.md`](docs/PROTOCOL.md)). Only this model is supported.

## Features

- **Noise control** — Off, Noise Cancelling, Ambient Aware.
- **Smart Audio & Video** — Normal, Audio Mode, Video Mode. Windows loses the earbuds' audio
  while the earbuds apply the new mode; the app reconnects it automatically.
- **Equalizer** — the model's presets (Off, Jazz, Vocal, Bass) plus a custom 10-band curve
  you edit by dragging the points.
- **Gestures** — single, double and triple tap, independently for each earbud.
- **Find My Buds** — makes an earbud beep (with a hearing-safety warning first).
- **Check My Best Fit** — ear-tip seal test with the result per earbud.
- **Battery** — left, right and charging case, plus a battery-usage view for the session.
- **Settings** — auto pause, auto power off, start with Windows, close-to-tray behavior.
- **System tray** — quick access to modes, equalizer and battery.
- **Safe volume** — when the earbuds' audio reconnects, Windows volume is capped (30 % by
  default) for a few seconds, because the earbuds re-apply their own saved volume on reconnect.

## Requirements

- Windows 10 (version 2004 or newer) or Windows 11 with Bluetooth LE.
- .NET Framework 4.8 (included in Windows 10 1903+ and Windows 11).
- The earbuds paired with the PC. Close the JBL app on your phone while using this app:
  the earbuds accept only one control connection at a time.

## Install

Download `LiveProPlusControl-vX.Y.Z-win.zip` from
[Releases](../../releases), extract it and run `LiveProPlusControl.exe`. No installation needed.

## Build

```
dotnet build LiveProPlusControl.sln -c Release
dotnet publish src/LiveProPlusControl/LiveProPlusControl.csproj -c Release -p:DebugType=none -o publish
```

Requires the .NET SDK (8 or newer) on Windows; the project targets .NET Framework 4.8 and uses
the WinRT Bluetooth APIs through `Microsoft.Windows.SDK.Contracts`.

## Project layout

```
src/LiveProPlusControl/
  Bluetooth/   BLE connection (JblDevice) and the earbuds' protocol (Protocol)
  Audio/       Windows audio endpoints (Core Audio) and the audio guard
  Core/        device state (DeviceController), settings, battery log, logging
  UI/          windows, tray icon and custom controls
  Assets/      app icon and product images
docs/          protocol notes
```

Settings are stored in `%AppData%\LiveProPlusControl\settings.ini` and a diagnostic log in
`%LocalAppData%\LiveProPlusControl\log.txt` (reset on every start).

## Disclaimer

This is an unofficial, community project. It is not affiliated with, endorsed by or supported
by JBL or HARMAN International. JBL, LIVE PRO+ and the JBL logo are trademarks of HARMAN
International Industries; the app icon, logo and product images in `Assets/` belong to their
owners. Use at your own risk.
