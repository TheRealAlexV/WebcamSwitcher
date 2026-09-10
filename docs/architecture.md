# Architecture

## Overview

WebcamSwitcher is a Windows 11 desktop application that composites two physical
USB webcams into a single user-mode **Media Foundation virtual camera**, with
instant switching via global hotkeys.

```
 Physical USB cams                    WebcamSwitcher.App  (WPF, user session, standard user)
 ┌──────────┐  ┌──────────┐        ┌─────────────────────────────────────────────────────┐
 │ Cam A    │  │ Cam B    │        │ CameraPipeline A ─┐                                  │
 └────┬─────┘  └────┬─────┘        │ CameraPipeline B ─┼─▶ ActiveCamSelector              │
      │  MF source reader (SHARE mode, non-blocking)     │        │                     │
      └─────────┬───┘             │              VideoProcessor MFT ─▶ 1080p30 NV12     │
                │                 │                              │                       │
                │                 │                     FramePublisher ──▶ Named-pipe server
                │                 │  Tray ▸ Hotkeys ▸ Settings ▸ Live previews           │
                │                 └─────────────────────────────────────────────────────┘
                │                                        │  \\.\pipe\WebcamSwitcher.Frames.v1  (cross-session)
                │                 ┌──────────────────────┼──────────────────────────────────┐
                │                 │ Frame Server (svchost, session 0, LocalService)      │
                │                 │   WebcamSwitcher.Source.dll  (C++ COM IMFMediaSource)│
                │                 │     pipe client → emits NV12 1080p30 samples         │
                │                 └──────────────────────┼──────────────────────────────────┘
                │                                        │  MF / DirectShow camera
                │                                        ▼
                │                  Consumer apps (Zoom/Teams/OBS)  +  NVIDIA Broadcast
                │                     (Broadcast consumes our VCam as a camera input)
                └── other apps can still use the physical cams directly (share mode)
```

## Components

- **`WebcamSwitcher.Source`** — native C++ COM DLL implementing the Media
  Foundation virtual-camera media source (`IMFMediaSource` / `IMFMediaStream`,
  activated via `IMFActivate`). It advertises a fixed output format (default
  NV12 1920x1080@30) and emits samples from frames it reads over a named pipe.
  It is registered in `HKLM` via `regsvr32` (one-time, admin).

- **`WebcamSwitcher.App`** — C# (.NET 9) WPF app. Captures both physical cameras
  in non-blocking share mode, normalizes the active camera via the Video
  Processor MFT, publishes frames over the named pipe, and hosts the tray icon,
  global hotkeys, settings, and live previews. It also calls
  `MFCreateVirtualCamera` with **Session** lifetime so the virtual camera exists
  only while the app runs.

- **`WebcamSwitcher.Shared`** — the frame transport protocol (see
  `WebcamSwitcherProtocol.h`), shared by both sides.

## Frame transport

A single named pipe `\\.\pipe\WebcamSwitcher.Frames.v1`:

- The **app is the pipe server**. It creates the pipe with a security descriptor
  granting `SYSTEM` and `LOCAL SERVICE` read/write (the Frame Server runs as
  LocalService in session 0), plus the current user.
- The **source is the pipe client**. On connect it receives a `WsConfigMsg`
  describing the fixed output format, then a stream of `WsFrameInfo + pixels`
  frame messages.
- The source keeps the most recent frame in memory and emits samples on a
  30 fps scheduler, repeating the last frame if the pipe stalls (so the virtual
  camera never freezes) and emitting a black frame when no frame has arrived yet.

A named pipe (rather than `Global\` shared memory) is used because it requires
no special privileges — a standard user process can create and serve it, while
`Global\` object creation needs `SeCreateGlobalPrivilege`.

## Non-blocking capture

Both physical cameras are opened in **sharing mode** so other applications can
still open the same cameras exclusively:

- `MF_DEVSOURCE_ATTRIBUTE_FRAMESERVER_SHARE_MODE = 1` (Windows 11 build 26100+)
  via the Media Foundation device source, or
- WinRT `MediaCapture` with `MediaCaptureSharingMode.SharedReadOnly`.

A sharing-mode source cannot reconfigure the camera, so the app consumes the
camera's native format and normalizes it itself (Video Processor MFT → NV12
1080p30).

## Key findings from the feasibility spike (M0)

1. `MFCreateVirtualCamera` works on the target machine (Windows 11 Pro 25H2,
   build 26200). A registered media source appears in `DeviceInformation` /
   VideoCapture enumeration as `<name> (Windows Virtual Camera)`.
2. The virtual camera enumerates only from the **interactive session** (session
   1), not from a non-interactive (session 0) context — the app already runs in
   the interactive session, so this is not a limitation.
3. Two cameras (Logitech C920 640x480 YUY2@30 and DJI OsmoPocket3 1280x720
   NV12@30) open simultaneously in `SharedReadOnly` mode.
4. Non-blocking verified: an exclusive/controlling `MediaCapture` instance opens
   the same camera while a shared instance holds it.
5. `MediaCapture.GetPreviewFrameAsync` is disallowed in `SharedReadOnly`; frame
   capture must use `MediaFrameReader` or the MF source reader.
6. The media source DLL must be registered in `HKLM` and live in a path readable
   by `LOCAL SERVICE` / `SYSTEM` (e.g. `C:\Program Files\...` or a non-profile
   path such as `C:\src\...`).

## Registration & install

- `regsvr32 WebcamSwitcher.Source.dll` registers the COM media source in `HKLM`
  (admin required, one-time).
- `WebcamSwitcher.App` calls `MFCreateVirtualCamera(SoftwareCameraSource,
  Session, CurrentUser, "WebcamSwitcher Virtual Camera", "{CLSID}", ...)` then
  `Start`, at runtime.
- The Inno Setup installer performs the `regsvr32` (install) and `regsvr32 /u`
  (uninstall) steps elevated.

## Hotkeys

- Implemented via Win32 `RegisterHotKey` on a hidden `HwndSource` window in the
  app.
- Defaults: `Ctrl+Alt+1`..`N` for direct selection, `Ctrl+Alt+N` to rotate.
- Configurable in settings; conflicts are detected and reported.

## See also

- `WebcamSwitcherProtocol.h` — wire protocol.
- MFCreateVirtualCamera: https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera
- Reference implementation (MIT): https://github.com/smourier/VCamSample
