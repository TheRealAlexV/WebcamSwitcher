# WebcamSwitcher

Windows 11 USB webcam switcher that composites **two USB webcams** into a single
**custom Media Foundation virtual camera**, switchable via configurable **global
hotkeys**. Compatible with NVIDIA Broadcast, Zoom, Teams, OBS, and browsers.

> Status: under active development.

## How it works

1. `WebcamSwitcher.App` (WPF, user session) captures **two physical cameras
   simultaneously** in non-blocking share mode (so other apps can keep using the
   cameras) and normalizes the *active* camera to a fixed format (default
   1920x1080 @ 30fps NV12).
2. It publishes the selected frames over a local named pipe.
3. `WebcamSwitcher.Source` (native C++ COM media source, loaded by the Windows
   Frame Server) exposes a **virtual camera** that emits those frames.
4. Global hotkeys switch the active camera instantly (per-camera keys + a rotate
   key).

## Requirements

- Windows 11 (build 22000+ for the virtual camera; build 26100+ recommended for
  non-blocking share mode).
- .NET 9 runtime (for the app) and the VC++ redistributable (for the source DLL).

## Building

See `docs/architecture.md` and `build.ps1`.

## License

MIT — see [LICENSE](LICENSE). Portions derived from MIT-licensed reference code;
see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
