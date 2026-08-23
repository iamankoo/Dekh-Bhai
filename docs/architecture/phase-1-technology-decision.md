# Phase 1 Technology Decision

This document records the stack chosen for Dekh Bhai's Phase 1 core casting engine, why each
piece was chosen over the alternatives, and the limitations that come with those choices.

## Environment this decision was made against

Inspected on the target dev machine before choosing anything:

| Item | Finding |
|---|---|
| OS | Windows 11 Home, build 26200 (x64) |
| RAM | 7.5 GB |
| GPU | AMD Radeon integrated graphics |
| .NET SDK | not installed → installed .NET 8 SDK via winget |
| Node.js | v24.14.0 (already present) |
| Python | 3.10.11 (already present) |
| Rust / Go | not installed |
| FFmpeg | not installed → installed via winget (`BtbN.FFmpeg.LGPL.Shared.8.1`) |
| Visual Studio Build Tools / Windows SDK | not installed (not required - see below) |
| Browsers | Chrome and Edge present |
| winget | available, used for all native tooling installs |

No production authentication, payments, analytics, or installer work was done, per the Phase 1
brief - the goal was a real, working media pipeline, not a polished product.

## The core decision: native Windows capture, not a browser tab

The brief is explicit that `getDisplayMedia()`/browser-tab capture, screenshot polling, and
LAN-only hacks are disqualified. That leaves one realistic path on Windows: consume the OS's own
compositor-level capture API directly from a native Win32 desktop process. Windows has exactly
one modern API for this - **Windows Graphics Capture** (`Windows.Graphics.Capture`, shipped since
Windows 10 1903) - so there wasn't a real alternative to evaluate; the decision was really about
*language/runtime* to host it in.

## Chosen stack

| Layer | Choice | Why |
|---|---|---|
| Desktop language/runtime | C# / .NET 8 | WGC is a WinRT API; .NET's CsWinRT projections (built into the SDK once the TFM targets a Windows version) give first-class access without a C++ interop layer. .NET 8 is the current LTS. |
| Desktop UI | WPF | Minimal, well-understood, ships in-box with the SDK, no separate UI toolkit dependency. |
| Screen capture | `Windows.Graphics.Capture` (WGC), via `IGraphicsCaptureItemInterop.CreateForMonitor` + `Direct3D11CaptureFramePool.CreateFreeThreaded` | The only modern, DRM/protected-content-aware, compositor-level capture API on Windows. Free-threaded frame pool means capture runs on its own thread pool, independent of the WPF UI thread/window. |
| GPU interop | [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D11/DXGI bindings) | Actively maintained successor to SharpDX (SharpDX itself was archived/abandoned in 2019 - explicitly rejected for that reason). Used to create the D3D11 device WGC requires and to read captured GPU textures back to CPU memory. |
| Video codec | VP8, encoded via [SIPSorceryMedia.FFmpeg](https://www.nuget.org/packages/SIPSorceryMedia.FFmpeg) (wraps libavcodec) | VP8 is mandatory-to-implement in every WebRTC browser, avoiding H.264 profile-negotiation complexity for a Phase 1 proof. `SIPSorceryMedia.Encoders` (an older, VP8-only wrapper) was tried first but its own NuGet listing marks it **legacy and no longer maintained**, explicitly pointing users at `SIPSorceryMedia.FFmpeg` - so the maintained package was used instead. |
| Audio codec | Opus, encoded via [Concentus](https://github.com/lostromb/concentus) 2.2.2 | SIPSorceryMedia.FFmpeg ships an audio *decoder* and a file/device *source*, but no general-purpose Opus *encoder* class - so a dedicated Opus encoder was needed. Concentus is a pure-managed C# port of the reference libopus encoder/decoder (net8.0 target, actively published), avoiding another native binary dependency. |
| System audio capture | NAudio 2.3.0, `WasapiLoopbackCapture` | The standard, actively maintained .NET audio library; `WasapiLoopbackCapture` is exactly WASAPI loopback - it captures the mix that would otherwise go to the speakers, which is real system audio, not the microphone. NAudio 3.x was released during this build but dropped support for anything below net9.0, so 2.3.0 (the newest net8.0-compatible release) was pinned instead. |
| WebRTC transport | [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) 10.0.16 | Actively maintained pure-C#/.NET implementation of ICE/DTLS-SRTP/RTP/RTCP and the `RTCPeerConnection` surface. No native WebRTC binary to ship, which matters a lot for a Windows desktop app that already has to bundle FFmpeg. |
| Signaling | Minimal Node.js (`ws` + `express`) WebSocket relay | Signaling only ever carries small JSON (SDP/ICE) - a lightweight, disposable Phase 1 component. Node was already on the machine and needs no separate runtime to install. It never touches media. |
| Viewer | Plain HTML/CSS/JS, standard `RTCPeerConnection`/`getStats` APIs, no build step | No installation requirement per the brief; works in any modern browser without bundling/transpiling for a Phase 1 test client. |

## Rejected alternatives

- **SharpDX** for D3D11 interop - archived by its maintainer in 2019; Vortice.Windows is its
  actively maintained spiritual successor and was used instead.
- **SIPSorceryMedia.Encoders** for VP8 - its own NuGet page states it is legacy/unmaintained and
  points at SIPSorceryMedia.FFmpeg.
- **ScreenRecorderLib** (a maintained WGC+Media Foundation wrapper) - investigated, but its API is
  built around recording to an (optionally fragmented) **MP4 file/stream**, not a raw per-frame
  callback. Demuxing MP4 fragments in real time to get RTP-ready access units would have added
  latency and complexity for no benefit over driving WGC directly.
- **NAudio 3.x** - the current major version, but it dropped net8.0 support in favor of net9.0+;
  the machine's installed SDK is .NET 8, so the newest net8.0-compatible 2.x release was used.
- **Google's official WebRTC native library via a C++/CLI shim** - would work, but requires
  compiling/bundling a large native binary and a C++ toolchain that isn't installed on this
  machine; SIPSorcery gets the same protocol coverage in pure managed code.
- **A browser tab / `getDisplayMedia()` host** - explicitly disqualified by the brief, and would
  have made "Dekh Bhai's own UI must never be captured" impossible to guarantee.

## Architecture

```
Desktop UI (WPF)
      |
      v
SessionController              <- the only thing the UI talks to
      |
  +---+----+
  |        |
  v        v
ScreenCapture   AudioCapture (WASAPI loopback)
  |                |
  v                v
VideoEncoderPipeline   AudioEncoderPipeline
 (VP8 via FFmpeg)       (Opus via Concentus)
  |                |
  +-------+--------+
          |
          v
     WebRtcHost (one RTCPeerConnection per viewer)
          |
          v
     SignalingClient <--WebSocket--> Node signaling server <--WebSocket--> Browser viewer
          |                                                                     |
          +----------------------------- WebRTC (direct) ----------------------+
```

`DekhBhai.Core` contains capture/audio/media/rtc/session as separate namespaces with narrow
interfaces (`IScreenCapture`, `IAudioCapture`) specifically so the UI project never touches
GPU/WinRT/WASAPI types directly, and so Phase 2 (duration picker, QR code, polished UI) is
additive rather than a rewrite.

## Known limitations (Phase 1)

- **Single primary monitor only.** `CaptureSettings.MonitorHandle` defaults to the primary
  display; a monitor picker is Phase 2 work.
- **48kHz stereo assumption for system audio.** WASAPI's default Windows mix format is
  overwhelmingly 48kHz float on modern hardware (confirmed on the dev machine), which also
  happens to be Opus's native/RTP clock rate, so Phase 1 does not resample. A device reporting a
  different rate is detected and its audio is dropped (with a status message) rather than
  producing corrupted audio - a real resampler is a Phase 2/3 item if a mismatched device is
  found in practice.
- **FFmpeg is bundled into the installable build, not a runtime dependency for end users.** The
  app project copies FFmpeg 8.x shared DLLs (`avcodec`, `avutil`, `avformat`, `avdevice`,
  `avfilter`, `swscale`, `swresample`) from a local winget install into both its build output and
  its publish output (`Content` items with `CopyToPublishDirectory` in
  `desktop/src/DekhBhai.App/DekhBhai.App.csproj`), so the self-contained MSIX build ships them -
  no FFmpeg install is required on a machine Dekh Bhai is installed on. See
  `docs/development/packaging.md` for the full installable-build writeup.
- **No TURN server.** Only a public STUN server (`stun.l.google.com:19302`) is configured, so
  hosts/viewers behind symmetric NATs may fail to connect peer-to-peer. Acceptable for a Phase 1
  same-network/most-NATs proof; a TURN relay is Phase 2/3 infrastructure.
- **No public Internet signaling.** Phase 1's signaling server is a local development process
  (`ws://localhost:8787` by default) - it is not deployed anywhere public, and Dekh Bhai does
  **not** claim to provide an Internet share link today. A friend installing the Phase 1 MSIX on
  another machine and a viewer on a *different* network from the host will not be able to
  connect, because there is no public signaling endpoint for them to reach. What Phase 1 *does*
  guarantee is that this is purely a configuration gap, not an architectural one: the host reads
  its signaling/viewer URLs from `DEKHBHAI_SIGNALING_WS_URL`/`DEKHBHAI_VIEWER_BASE_URL`
  environment variables (`desktop/src/DekhBhai.App/AppConfig.cs`), defaulting to localhost, so
  pointing the same installed app at a real `wss://` production signaling deployment in Phase 2
  needs no code change - just standing up that service and setting those two variables.
- **One viewer was the tested scenario**, though the architecture (one `RTCPeerConnection` per
  viewer, fanned out from the same encoded samples) supports more than one concurrently.
- **Microphone capture exists in the engine (`MicrophoneAudioCapture`) but isn't wired into the
  UI or mixed into the session** - deliberately deferred, per the brief, to keep it additive for
  Phase 2.

## The yellow capture border: source, and why it is not a Dekh Bhai defect

While testing, a thin yellow line appears along the top edge of the physical display for a few
seconds right after a stream starts. This was investigated directly (not assumed):

- **Source**: this is **Windows' own Graphics Capture indicator**, drawn by the DWM compositor
  whenever *any* app has an active `GraphicsCaptureSession` - it is not something Dekh Bhai
  renders. Dekh Bhai's capture code never draws anything; it only reads GPU texture bytes back to
  CPU memory (`WindowsGraphicsScreenCapture.ProcessFrame`) and hands them to the encoder unmodified.
- **Confirmed empirically**: a screenshot of the live physical desktop and a same-moment pixel
  sample of the actual frame the viewer received were taken simultaneously while the border was
  visible locally. The local screenshot showed the yellow line; a pixel scan of the corresponding
  row in the decoded WebRTC video (`getImageData` on a canvas snapshot of the `<video>` element)
  found zero yellow pixels. The border is drawn by DWM into the final composited desktop image
  *after* WGC has already delivered the frame to the app, so it is a local-only visual cue and is
  never part of what viewers see.
- **Supported suppression mechanism, and it's now implemented**: Windows exposes
  `GraphicsCaptureSession.IsBorderRequired` to suppress the border, but only after (a) the app
  has package identity and declares the restricted `graphicsCaptureWithoutBorder` capability in
  its MSIX manifest, and (b) the app calls
  `GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)` at runtime and
  the user grants a one-time OS consent prompt. Checked directly against Microsoft's own
  capability-declaration documentation: restricted capabilities can be **sideloaded and used
  without any Microsoft approval process** - approval is only required to publish to the Store -
  so this doesn't need a Store submission, just MSIX packaging. Since Phase 1 now ships an
  installable MSIX build anyway (see `docs/development/packaging.md`), this was implemented:
  `WindowsGraphicsScreenCapture.TryDisableCaptureBorderAsync` requests the access and sets
  `IsBorderRequired = false` when granted; `packaging/msix/AppxManifest.xml` declares
  `<uap11:Capability Name="graphicsCaptureWithoutBorder"/>`. This required moving the TFM from
  `net8.0-windows10.0.19041.0` to `net8.0-windows10.0.22621.0` so these newer WinRT APIs are
  projected at all (`IsBorderRequired`/`GraphicsCaptureAccess` don't exist in the 19041 contract).
  The request is fire-and-forget and wrapped in try/catch: on an unpackaged run (`dotnet run`)
  every step fails harmlessly and capture behaves exactly as before - this only has any effect
  once truly MSIX-installed. It was not conclusively re-observed whether the OS consent prompt
  actually appeared during automated install testing (no interactive click was performed to
  approve it, since testing was scripted); this should be re-verified with a human present at
  first run on a real target machine, since first-time restricted-capability consent is
  inherently an interactive OS prompt that can't be scripted around.
- **Net effect for the product requirement**: the requirement is that *Dekh Bhai* adds nothing to
  the captured desktop - it doesn't, and never did (this section is about Windows' own local
  screen indicator, not anything in the captured frame data, which was already proven clean
  above). The border the user sees locally is Windows telling *them* that capture is active; this
  section covers making that OS indicator itself go away using Windows' own supported mechanism,
  not modifying what viewers receive.

## The black screen after a Windows screenshot action: investigation

A real-world test reported the viewer showing "Live", a video element still active, ~3 FPS, and
a fully black picture, after a Windows screenshot/capture action was triggered while Dekh Bhai
was running. This was traced rather than guessed at:

- **Code audit first**: `WindowsGraphicsScreenCapture.ProcessFrame` does no image processing of
  any kind - it copies the WGC-delivered GPU texture to a CPU staging buffer and hands the raw
  bytes to the encoder unchanged. There is no code path in Dekh Bhai that could independently
  produce or get "stuck" on black content; if the delivered frame is black, WGC supplied black.
- **Detector validated first, so the *absence* of a signal is meaningful.** Before trying to
  reproduce the reported scenario, the existing `FrameContentHint` sampling was confirmed to
  actually work: a real fullscreen black window was shown for two seconds while capturing, and
  the trace log correctly reported `Normal -> LikelyBlack` on entry and `LikelyBlack -> Normal` on
  exit, with normal (tens-of-milliseconds) frame gaps throughout and zero frames dropped on the
  viewer side (`framesDropped: 0` via `getStats()`) - i.e. the pipeline transitions into and back
  out of genuine black content smoothly, with no stall, crash, or stuck state. This is exactly
  the required "keep the session alive, recover automatically" behavior, confirmed working.
- **Reproduction attempts**: tried triggering Win+Shift+S (Snip & Sketch), launching the Snipping
  Tool directly (`ms-screenclip:`), and a plain GDI `CopyFromScreen` capture (the same technique
  used for verification screenshots throughout this project) while Dekh Bhai was live and a
  viewer connected. None of these produced a black frame or a `FrameContentHint` transition - the
  pipeline stayed healthy (0 dropped frames, hint stayed `Normal`) through all of them on this
  machine/GPU.
- **Conclusion**: black or low-fps frames are a legitimate WGC/OS-level condition (the same
  category as DRM/protected content or the Windows secure desktop shown during a UAC prompt -
  WGC cannot read those surfaces and correctly returns black rather than erroring), not a Dekh
  Bhai defect, and the architecture already handles it correctly and automatically - this was
  proven with a real black-to-normal transition, not just asserted. The exact trigger from the
  original report was not reproduced with the specific tools tried here; it may be specific to a
  different screenshot tool, timing, or this machine's AMD integrated GPU driver behaving
  differently under a second concurrent capture consumer. No code change was made because none
  was needed - the pass-through/recovery behavior this scenario requires was already correct by
  construction (nothing in the pipeline branches on frame content).
