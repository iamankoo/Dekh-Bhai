# Packaging Dekh Bhai as an installable Windows app

## Selected installer technology: MSIX

**MSIX**, built and signed with the raw Windows SDK tools (`makeappx.exe`, `signtool.exe`) - no
Visual Studio, no WiX, no third-party installer authoring tool.

### Why MSIX

MSIX was chosen for one concrete, technical reason, not because it was the easiest option:

**It's the only supported route to suppressing Windows' own screen-capture border indicator.**
Windows exposes `GraphicsCaptureSession.IsBorderRequired`, but the OS will only honor it after
the app (a) has **package identity** (i.e. is MSIX-packaged) and declares the restricted
`graphicsCaptureWithoutBorder` capability in its manifest, and (b) the user grants a one-time
consent prompt via `GraphicsCaptureAccess.RequestAccessAsync`. A plain unpackaged `.exe` cannot
use this API at all - there is no way around that requirement (see
`docs/architecture/phase-1-technology-decision.md` for the full investigation). Since MSIX was
already necessary for that, it made sense to use it for Task 4's installable-build requirement
too, rather than adding a second, different packaging technology.

MSIX also directly satisfies the rest of the installable-build requirements: self-contained
.NET deployment (no runtime install needed), a Start Menu entry, a real install/uninstall
lifecycle through Windows' own "Apps & Features" (no custom uninstaller to write and maintain),
and no admin rights needed to *run* the installed app (only to *trust* a non-Store-signed
package once, the same as any self-signed sideloaded app).

### Why not the alternatives

- **WiX/MSI** - would have worked for the "installable app" requirement alone, but cannot grant
  package identity, so it would not unlock the border-suppression capability. Since MSIX was
  needed anyway, adding MSI as well would just be two packaging systems to maintain for no
  benefit in Phase 1.
- **A `.wapproj` (Visual Studio Windows Application Packaging Project)** - the more common way to
  build MSIX for a WPF app, but its build targets ship with the Visual Studio "Universal Windows
  Platform development" workload, which is not installed on this machine (per the brief, no
  Visual Studio should be required). Packaging was done instead with the same `makeappx`/
  `signtool` command-line tools that `.wapproj` calls under the hood, driven directly - this
  needs only the Windows SDK, not Visual Studio.
- **Store submission** - not attempted; restricted capabilities can be sideloaded and used
  without any Microsoft approval process (approval is only required to publish to the Store -
  confirmed directly against Microsoft's own capability-declaration documentation). Sideloading
  is the correct fit for a Phase 1 test build.

## Build command

```powershell
# One-time (see "Runtime requirements" below): .NET 8 SDK, a local FFmpeg 8.x shared build, and
# the Windows 10.0.26100 SDK (for makeappx.exe/signtool.exe) all need to be present on the build
# machine - none of that is required on the machine the built package gets *installed* on.

# The signing certificate's password is required and has no hardcoded default (a hardcoded
# signing-key password was found and removed during Phase 2 hardening - see
# docs/architecture/phase-2-technology-decision.md). Set it once per shell session:
$env:DEKHBHAI_PFX_PASSWORD = "<the password the .pfx below was generated with>"
scripts\build-msix.ps1
```

This single script (see `scripts/build-msix.ps1`) runs the whole pipeline:

1. `dotnet publish desktop/src/DekhBhai.App/DekhBhai.App.csproj -c Release -r win-x64 --self-contained true` -
   produces a self-contained win-x64 build (includes the .NET runtime and the FFmpeg native DLLs,
   which are bundled as `Content` items in `DekhBhai.App.csproj` with `CopyToPublishDirectory` -
   see the FFmpeg section below) into `packaging/publish/`.
2. Stages that output plus `packaging/msix/AppxManifest.xml` and `packaging/msix/Assets/*.png`
   into `packaging/msix/layout/`.
3. `makeappx.exe pack /d packaging/msix/layout /p dist/DekhBhai.msix` - builds the package.
4. `signtool.exe sign /fd SHA256 /a /f packaging/msix/DekhBhaiSigning.pfx dist/DekhBhai.msix` -
   signs it.

## Output location

```
dist/
    DekhBhai.msix
```

That's the one file to hand someone. `packaging/publish/` and `packaging/msix/layout/` are
build-time intermediates (gitignored) - not part of the deliverable.

## Installation instructions

Dekh Bhai isn't Store-published, so Windows needs to be told to trust the package's signing
certificate before it will install - a one-time step, same as any sideloaded app.

1. Trust the certificate (**run PowerShell as Administrator**):
   ```powershell
   Import-Certificate -FilePath "packaging\msix\DekhBhaiSigning.cer" -CertStoreLocation "Cert:\LocalMachine\TrustedPeople"
   ```
2. Install the package (no admin needed):
   ```powershell
   Add-AppxPackage -Path "dist\DekhBhai.msix"
   ```
   Or just double-click `DekhBhai.msix` in File Explorer - Windows opens the App Installer UI,
   which shows an **Install** button once the certificate above is trusted.
3. Launch **Dekh Bhai** from the Start Menu, same as any other installed app.

Windows Sideloading must be allowed on the target machine, which it is **by default** on
Windows 10 2004+ and all Windows 11 versions (this is not something Dekh Bhai's installer needs
to change).

## Uninstall instructions

Standard Windows uninstall - no custom uninstaller:
- **Settings → Apps → Installed apps → Dekh Bhai → Uninstall**, or
- Right-click the Start Menu tile → **Uninstall**, or
- `Remove-AppxPackage -Package "Aniket.DekhBhai_1.0.0.0_x64__<hash>"` (get the exact package
  full name with `Get-AppxPackage -Name Aniket.DekhBhai`).

Verified directly: after `Remove-AppxPackage`, `Get-AppxPackage` returns nothing, the Start Menu
entry is gone, and the install directory under `C:\Program Files\WindowsApps\` is deleted -
confirmed clean.

## Runtime requirements (on the machine Dekh Bhai is *installed on*)

- Windows 10, version 2004 (build 19041) or later, **or** Windows 11 - x64 only (see
  "Windows compatibility" below).
- Nothing else. The deployment is **fully self-contained**: the .NET 8 runtime, every NuGet
  dependency, and the FFmpeg native DLLs are all inside the package. No .NET SDK/runtime
  install, no Node.js, no FFmpeg install, no Visual Studio, no Git, and no PowerShell scripts
  are needed on the installed machine.
- The Dekh Bhai **host app** needs no signaling server install on that machine either for local
  testing - it talks to the signaling endpoint configured via `DEKHBHAI_SIGNALING_WS_URL` /
  `DEKHBHAI_VIEWER_BASE_URL` (defaulting to `ws://localhost:8787` for Phase 1 development; a
  Phase 2 production deployment would point these at a real public signaling service instead -
  see `docs/architecture/phase-1-technology-decision.md`). The **viewer** stays a plain browser
  page and needs nothing installed at all.

### FFmpeg bundling

`SIPSorceryMedia.FFmpeg` needs FFmpeg's native shared libraries (`avcodec`, `avutil`,
`avformat`, `avdevice`, `avfilter`, `swscale`, `swresample`) to be resolvable at runtime. These
are declared as `Content` items with `CopyToPublishDirectory` in `DekhBhai.App.csproj`, sourced
from a local FFmpeg install at build time (`FFmpegNativeDir` MSBuild property - see
`docs/development/setup.md`) but **copied into the publish/package output itself**, so the
*built app* never depends on that path existing - only the *build machine* does. Verified this
is true: the DLLs are present in `dist/DekhBhai.msix` and in the installed
`C:\Program Files\WindowsApps\...` folder, and `FFmpegBootstrap.EnsureInitialised()` in the code
always resolves the native library path from `AppContext.BaseDirectory` (wherever the exe
actually is at runtime) - never a hard-coded path.

## Signing status

Self-signed, for **sideloading only** - not submitted to or signed for the Microsoft Store.

- Certificate: `CN=Aniket Raj`, generated with `New-SelfSignedCertificate` (see
  `scripts/build-msix.ps1` header comment for the parameters used); private key at
  `packaging/msix/DekhBhaiSigning.pfx` (gitignored - **never commit this file**), public
  certificate at `packaging/msix/DekhBhaiSigning.cer` (also gitignored, since it's build output,
  but safe to share/commit if needed - it contains no secret material).
- The package's `Identity/Publisher` in `AppxManifest.xml` (`CN=Aniket Raj`) matches the
  certificate's Subject exactly, as MSIX requires.
- A production release (Phase 3) should replace this with either a properly issued code-signing
  certificate (so Windows trusts it without the manual `Import-Certificate` step) or a Microsoft
  Store submission.

### Windows SmartScreen implications

Because the package is self-signed rather than signed by a certificate with an established
reputation (or Store-distributed), **SmartScreen/Defender may warn on first run** on a machine
that hasn't explicitly trusted the certificate, and the one-time `Import-Certificate` step above
is mandatory before `Add-AppxPackage`/App Installer will accept the package at all (a fresh
install attempt without trusting the cert fails immediately with
`HRESULT 0x800B0109` - verified directly during testing). This is expected, standard behavior
for a self-signed sideloaded app, not a bug. A trusted commercial code-signing certificate
(Phase 3) removes this friction entirely.

## Windows compatibility

| | |
|---|---|
| Minimum supported Windows version | Windows 10, version 2004 (build 19041) - required by Windows Graphics Capture |
| Recommended Windows version | Windows 11 (22H2/build 22621 or later) - required for the border-suppression capability; earlier Windows 10 builds still capture and stream correctly, just with the OS's yellow capture-indicator border always visible |
| Architecture | x64 only in this build. **ARM64 is not supported** - not attempted, not tested. Vortice.Windows and SIPSorceryMedia.FFmpeg both publish ARM64-capable binaries in principle, but the FFmpeg native DLLs bundled here are the x64 BtbN build; producing an ARM64 package would need an ARM64 FFmpeg build and a separate `-r win-arm64` publish/package, which hasn't been done or tested. |
| GPU requirements | Any GPU with a Windows 10-era WDDM driver supporting Direct3D 11 with BGRA support (`D3D11_CREATE_DEVICE_BGRA_SUPPORT`) - true of essentially all GPUs from the last decade, integrated or discrete. Tested on AMD integrated graphics. |
| Audio requirements | A default Windows audio render (playback) device for system-audio loopback capture. Tested against the default WASAPI mix format (48kHz float) - see the "known limitations" note in the architecture doc about non-48kHz devices. |

Viewer compatibility is unrelated to the host's OS - see
`docs/testing/test-plan.md` for what's actually been tested there (Chrome only, so far).

## Limitations

- Self-signed/sideload-only - see "Signing status" above.
- x64 only - see "Windows compatibility" above.
- The MSIX build is ~140 MB, mostly the self-contained .NET runtime and FFmpeg's native
  libraries (`avcodec`/`avfilter` alone account for over half of it). No attempt was made to
  trim this (e.g. via `PublishTrimmed`) - trimming a WPF + WinRT-interop + FFmpeg.AutoGen
  dependency graph safely is real work and risks breaking reflection-based bits of that stack;
  not attempted for Phase 1.
- No desktop shortcut is created automatically - MSIX apps get a Start Menu entry (verified:
  `Get-StartApps` lists "Dekh Bhai" after install), and the user can pin that to the taskbar or
  desktop themselves via the normal Windows right-click menu, exactly like any Store app. A
  separately-scripted desktop shortcut was not added since it isn't how MSIX apps normally
  behave and isn't required by the brief ("where appropriate").
- This build's signaling/viewer endpoint defaults to `ws://localhost:8787` /
  `http://localhost:8787/` unless overridden. The endpoint is read from
  `DEKHBHAI_SIGNALING_WS_URL`/`DEKHBHAI_VIEWER_BASE_URL` environment variables specifically so a
  production signaling deployment can be pointed at later without changing this package. **As of
  Phase 2, no production signaling deployment exists yet** - see
  `docs/deployment/phase-2.md` for the deployment procedure and section 5 there
  ("Configuring an installed MSIX build") for exactly how to set these two variables on an
  installed copy, including the sign-out/sign-in requirement for a packaged app to pick up a
  newly-set environment variable.
