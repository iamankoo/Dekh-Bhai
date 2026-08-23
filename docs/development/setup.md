# Development Setup

## Prerequisites

- Windows 10 2004 (build 19041) or later, x64 - required for Windows Graphics Capture.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`winget install Microsoft.DotNet.SDK.8`).
- [Node.js](https://nodejs.org) 18+ (any recent LTS; developed against v24).
- Only if you want to **build the installable MSIX package** (not needed for `dotnet run`/
  `dotnet test`): the Windows 10.0.26100 SDK, for `makeappx.exe`/`signtool.exe`
  (`winget install Microsoft.WindowsSDK.10.0.26100`). See `docs/development/packaging.md`.
- A local FFmpeg 8.x **shared** build (DLLs, not just `ffmpeg.exe`). The LGPL shared build from
  the BtbN releases works well:
  ```
  winget install BtbN.FFmpeg.LGPL.Shared.8.1
  ```
  Find the install path (something like
  `%LOCALAPPDATA%\Microsoft\WinGet\Packages\BtbN.FFmpeg.LGPL.Shared.8.1_*\ffmpeg-*\bin`) and
  either:
  - leave the default path in `desktop/src/DekhBhai.App/DekhBhai.App.csproj`
    (`FFmpegNativeDir` property) if it matches, or
  - override it at build time: `dotnet build -p:FFmpegNativeDir="C:\path\to\ffmpeg\bin"`.

  Only needed to **build** Dekh Bhai - a built/installed copy of the app (see
  `docs/development/packaging.md`) bundles these DLLs and needs no FFmpeg install of its own.

  The build copies `avcodec`, `avutil`, `avformat`, `avdevice`, `avfilter`, `swscale`, and
  `swresample` DLLs next to the built exe. All seven are required - `SIPSorceryMedia.FFmpeg`
  calls `avdevice_register_all()` during init, so a partial copy (e.g. missing `avdevice`) fails
  fast with a `DllNotFoundException` on startup.

## Repository layout

```
Dekh-Bhai/
  desktop/            .NET solution (host app + media/capture/rtc engine)
    src/DekhBhai.App/  WPF host UI
    src/DekhBhai.Core/ capture/audio/media/rtc/session engine (no UI dependency)
    tests/              unit tests for DekhBhai.Core
    tools/CaptureSmokeTest/  standalone console app to sanity-check WGC capture alone
  signaling/          Node.js WebSocket signaling relay + static viewer host
  viewer/             plain HTML/JS/CSS browser viewer (served by signaling/)
  scripts/            convenience launch scripts
  docs/               architecture/development/testing docs (this file included)
```

## Running everything locally

1. **Signaling server + viewer** (also serves the viewer's static files):
   ```
   cd signaling
   npm install
   npm start
   ```
   Listens on `http://localhost:8787` (WebSocket path `/ws`). Health check: `GET /healthz`.

2. **Desktop host app**:
   ```
   cd desktop
   dotnet build -c Debug
   dotnet run --project src/DekhBhai.App
   ```
   Or use `scripts/run-app.ps1` / `scripts/dev-all.ps1` (starts both signaling and the app).

3. Click **START SHARING**, pick a duration (15 minutes / 1 hour / 5 hours / Until I Stop), then
   **START**. The app connects to the signaling server, starts capture, and shows a share link
   like `http://localhost:8787/v/<id>` plus a QR code for it. Open that link in a browser (on the
   same machine, or another device on the same network with the host's LAN IP instead of
   `localhost`) to view the stream.

   This is still local/development signaling (`ws://localhost:8787`) - see
   `docs/deployment/phase-2.md` for pointing a build at a real public signaling deployment via
   `DEKHBHAI_SIGNALING_WS_URL`/`DEKHBHAI_VIEWER_BASE_URL`, and
   `docs/architecture/phase-2-technology-decision.md` for the session/signaling design behind the
   duration picker, QR code, and production URL shape.

## Debug tracing

Both the capture engine and the session controller support a low-overhead, opt-in trace log,
disabled by default:

```
$env:DEKHBHAI_TRACE = "1"
dotnet run --project desktop/src/DekhBhai.App
```

`DekhBhai.Core`'s `WindowsGraphicsScreenCapture` writes `[trace]` lines to stdout; `SessionController`
writes timestamped lines to `session-trace.log` next to the built exe. Useful when diagnosing
WinRT/D3D11 interop issues, which fail in ways that don't always produce a clean managed stack
trace.

## Solution file

`desktop/DekhBhai.sln` includes all four projects (`DekhBhai.App`, `DekhBhai.Core`,
`DekhBhai.Core.Tests`, `CaptureSmokeTest`). Open it in Visual Studio / Rider, or use `dotnet
build`/`dotnet test` from the `desktop/` folder.
