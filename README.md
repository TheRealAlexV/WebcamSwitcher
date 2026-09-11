# WebcamSwitcher

A Windows 11 desktop app that turns your USB webcams into **custom Media
Foundation virtual cameras**. Every configured camera gets its own always-on
virtual camera, and the active one is additionally exposed as a single
**switchable** virtual camera toggled by configurable **global hotkeys**. The
virtual cameras work as normal webcam inputs in Zoom, Teams, OBS, browsers, and
**NVIDIA Broadcast**.

## How it works

1. `WebcamSwitcher.App` (WPF, runs in your user session) captures **all
   configured cameras simultaneously in non-blocking share mode** — other apps
   can keep using the same cameras.
2. Every camera's frames are normalized to a fixed format (default
   **1920x1080 @ 30fps NV12**) and published to its **own** named pipe. Each
   physical camera therefore stays continuously available as its own virtual
   camera — switching the active camera never blanks the others.
3. The **active** camera's frames are additionally published to the switcher
   pipe. Hotkeys switch instantly which camera feeds it.
4. `WebcamSwitcher.Source` (native C++ COM media source, loaded by the Windows
   Frame Server) exposes virtual cameras that emit those frames.

```
 Cam A ─┬─▶ normalize ─▶ pipe p0 ─▶ "HD Pro Webcam C920 (WebcamSwitcher)"   always live
        └─┐
          ├─▶ pipe v1 ─▶ "WebcamSwitcher Virtual Camera" ─▶ Zoom / OBS / Broadcast
 Cam B ─┬─┘   (active camera only; hotkeys choose which camera feeds v1)
        └─▶ normalize ─▶ pipe p1 ─▶ "OsmoPocket3 (WebcamSwitcher)"           always live
```

### Virtual cameras

- **One passthrough virtual camera per configured camera**, named
  `"<Camera FriendlyName> (WebcamSwitcher)"` (e.g.
  `HD Pro Webcam C920 (WebcamSwitcher)`). Each is a permanent, dedicated feed of
  that physical camera.
- **One switchable virtual camera**, named
  `WebcamSwitcher Virtual Camera`, carrying whichever camera is currently
  active. This is the one you normally select in conferencing apps.
- All of them are registered on launch when **Start virtual camera on launch**
  is enabled (`VirtualCameraOnLaunch`).

## Requirements

- **Windows 11** (build 22000+ for the virtual camera; build 26100+ recommended
  for non-blocking share mode).
- x64 CPU.
- No code-signing certificate or kernel driver is required — the virtual camera
  is a user-mode Media Foundation media source.

## Installation

Download the installer from [Releases](../../releases) and run it. The installer
requires administrator rights once (to register the media source in `HKLM`).

### Manual install (no installer)

1. Copy the build output (the self-contained app folder) somewhere stable, e.g.
   `C:\Program Files\WebcamSwitcher\`.
2. Register the media source once, from an **elevated** command prompt:
   ```
   regsvr32 "C:\Program Files\WebcamSwitcher\WebcamSwitcher.Source.dll"
   ```
3. Run `WebcamSwitcher.exe`.

## Usage

- The **system tray icon** shows the app; double-click to open the window.
- The main window shows **live previews** of both cameras; click a preview (or
  press its hotkey) to make that camera active. The green border marks the
  active camera.
- **Default hotkeys** (configurable in Settings):
  - `Ctrl+Alt+1` — camera 1
  - `Ctrl+Alt+2` — camera 2
  - `Ctrl+Alt+N` — rotate through all cameras
- **Settings** (tray → Settings, or the Settings button) edits everything:
  cameras (add/remove any number), output resolution & FPS, hotkeys, and startup
  options — all applied without restarting the app.
- Each camera is exposed to other apps as
  **"<Camera Name> (WebcamSwitcher)"**, and the active camera additionally as
  **"WebcamSwitcher Virtual Camera (Windows Virtual Camera)"**.

### Quit and single-instance behaviour

- Only one instance runs at a time. Launching a second one brings the existing
  window to the front instead of starting a competing copy.
- If a previous instance is **hung** (not responding, e.g. left over from a
  crash), a new launch detects it via a freshness heartbeat and terminates it,
  then starts normally.
- Exiting fully releases the cameras, pipes, and virtual-camera registrations
  within a few seconds, so the app can be restarted immediately.
- `WebcamSwitcher.exe --exit` shuts down a running instance cleanly (useful from
  scripts).

### NVIDIA Broadcast

1. Start WebcamSwitcher and confirm the virtual camera is active.
2. In NVIDIA Broadcast, open **Camera** settings and select
   **WebcamSwitcher Virtual Camera** as the input source.
3. Use NVIDIA Broadcast's own virtual camera as the output in your conferencing
   app.

> The virtual camera registers in the standard Windows `VideoCapture` device
> enumeration (the same list NVIDIA Broadcast uses). If a specific app doesn't
> list it, it's most likely enumerating only physical/driver-backed cameras.

## Configuration

All settings are editable from the **Settings** window (tray → Settings, or the
Settings button in the main window) and apply **without restarting the app**.
They are stored in `%APPDATA%\WebcamSwitcher\config.json`:

```jsonc
{
  "Width": 1920,
  "Height": 1080,
  "Fps": 30,
  "Cameras": [
    { "DeviceId": "\\\\?\\USB#...\\GLOBAL", "FriendlyName": "HD Pro Webcam C920" },
    { "DeviceId": "\\\\?\\USB#...\\GLOBAL", "FriendlyName": "OsmoPocket3" }
  ],
  "ActiveIndex": 0,
  "Hotkeys": [ "Ctrl+Alt+1", "Ctrl+Alt+2" ],
  "RotateHotkey": "Ctrl+Alt+N",
  "StartWithWindows": false,
  "StartMinimized": false,
  "VirtualCameraOnLaunch": true
}
```

- `Cameras` — the cameras to capture (any number). `DeviceId` is the stable
  device ID; if the list is empty, the first two physical cameras are used
  automatically. An optional `CaptureWidth` / `CaptureHeight` per camera pins the
  capture resolution (otherwise a matching native format is chosen).
- `Width` / `Height` / `Fps` — the virtual cameras' output format (default
  1920x1080 @ 30fps). Source frames are scaled to this.
- `Hotkeys` — one per camera, in order. Format: `Modifier+Modifier+Key`
  (e.g. `Ctrl+Alt+1`, `Ctrl+Shift+F2`, `Alt+N`).
- `RotateHotkey` — a single key that cycles through all cameras.
- `StartWithWindows` — add/remove the app from the per-user startup (Run key).
- `StartMinimized` — start hidden to the tray.
- `VirtualCameraOnLaunch` — register the virtual cameras as soon as the app
  starts (also toggleable at runtime from the main window).

> The output format is also written to
> `%ProgramData%\WebcamSwitcher\format.ini` and the virtual-camera → pipe
> mapping to `%ProgramData%\WebcamSwitcher\vcams.ini`, so the native
> virtual-camera source advertises the right format and routing. A consuming app
> may need to re-open the camera to pick up a new resolution.

## Building

Prerequisites: Visual Studio 2022 (with the C++ toolset) and the .NET 9 SDK.

```
powershell -File build.ps1
```

This produces a self-contained app in `artifacts/app` (the `.exe` + the
virtual-camera `WebcamSwitcher.Source.dll`). The installer (Inno Setup) is built
by CI; run the `.iss` with [Inno Setup](https://jrsoftware.org/isinfo.php) to
build it locally.

## Releases & CI

GitHub Actions ([`.github/workflows/build.yml`](.github/workflows/build.yml))
builds the C++ media source, the WPF app, the portable zip, and the Inno Setup
installer on every push to `main` and `dev`, on pull requests to `main`, and when
a tag is pushed.

**Publishing a release:** push a version tag on `main` (or `dev`):

```
git tag v1.0
git push origin v1.0
```

The tag triggers the same build with the version taken from the tag, and the
workflow automatically creates a **GitHub Release** for that tag containing:

- `WebcamSwitcher-Setup-<version>.exe` — the installer
- `WebcamSwitcher-<version>-portable.zip` — the self-contained app folder

Release notes are generated automatically from the commits since the previous
tag. Any further tag on `main` produces a new release the same way.

## Testing

- `tools/UiTest` is a FlaUI (UI Automation) harness that launches the app,
  opens Settings, verifies the camera dropdowns render real names, selects a
  camera + changes the output format, clicks **Apply**, and asserts the dialog
  closes. Run it from the interactive desktop session (e.g. via a scheduled
  task), since UI Automation can't see Session 0.
- `tools/SmokeTest`, `tools/CaptureTest`, and `tools/FormatApplyTest` cover the
  virtual-camera registration, cross-session pipe, capture/normalization, and
  format-change paths headlessly.

## Project layout

```
src/WebcamSwitcher.Source/   C++ COM media source (the virtual camera)
src/WebcamSwitcher.Core/     C# capture/normalize/publish engine + vcam service
src/WebcamSwitcher.App/      WPF app (tray, hotkeys, settings, previews)
src/WebcamSwitcher.Shared/   frame transport protocol (C header)
installer/                   Inno Setup script
tools/                       dev/test harnesses (smoke test, capture test)
```

## License

MIT — see [LICENSE](LICENSE). Portions derived from MIT-licensed reference
code; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
