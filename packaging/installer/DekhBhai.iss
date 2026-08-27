; Dekh Bhai single-file installer (Inno Setup - free/open-source, no large framework needed).
;
; What this wraps, and why: Dekh Bhai's MSIX packaging is load-bearing, not incidental - package
; identity is the ONLY supported way to get Windows Graphics Capture's border-suppression
; capability (graphicsCaptureWithoutBorder), which requires the app to be MSIX-installed - see
; docs/architecture/phase-1-technology-decision.md. Replacing the app with a plain portable exe
; would silently break that. This installer therefore does NOT reimplement or replace the app -
; it is a thin wrapper that gets a normal Windows user from "one downloaded .exe" to "MSIX
; installed with its certificate trusted", which otherwise required an interactive
; Import-Certificate PowerShell command a non-technical friend cannot be expected to run - see
; docs/architecture/phase-3-technology-decision.md ("Single-file installer").
;
; Everything a friend needs is embedded in the single compiled .exe this script produces
; (DekhBhai.msix and DekhBhaiSigning.cer are compressed into the installer itself, not
; distributed alongside it) - the self-contained MSIX already bundles the .NET runtime and
; FFmpeg, so nothing else needs installing on the target machine.

#define MyAppName "Dekh Bhai"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Aniket Raj"
#define MyPackageFamilyName "Aniket.DekhBhai_ztn0zpwa8syma"
; The package Identity Name (AppxManifest.xml <Identity Name="...">) - what Get-AppxPackage -Name
; matches against. NOT the same value as the Application Id below - conflating the two here is
; exactly what broke the desktop shortcut (see AUMID comment in [Icons]).
#define MyAppId "Aniket.DekhBhai"
; The manifest's <Application Id="..."> (AppxManifest.xml), which is "DekhBhai", not "Aniket.DekhBhai".
; Windows' Application User Model ID (AUMID) used by shell:AppsFolder activation is
; "<PackageFamilyName>!<Application Id>" - it is NOT "<PackageFamilyName>!<Identity Name>". Using
; MyAppId (the Identity Name) here silently resolves to nothing, and explorer.exe's fallback
; behavior for an unresolvable shell:AppsFolder AUMID is to open the default shell folder
; (Documents) instead of erroring - which is exactly the desktop-shortcut bug this fixes.
#define MyAppUserModelId "DekhBhai"

[Setup]
AppId={{21FED9C0-CC17-40E8-B97F-4CE98C5BDA6E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Dekh Bhai
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
; MSIX install + certificate trust both require writing to machine-wide stores/state, hence
; admin - the UAC prompt this produces is the "Windows needs permission to install Dekh Bhai"
; experience called for, not an interactive PowerShell window.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\dist\release
OutputBaseFilename=DekhBhai-Setup
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\..\desktop\src\DekhBhai.App\Assets\DekhBhai.ico
WizardStyle=modern
UninstallDisplayName={#MyAppName}
; Nothing here needs a per-user vs per-machine directory choice or file associations - keep the
; wizard to the minimum: welcome, license-free install, done.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Staged into {app} only so [Run]/[Icons] below have a stable local path to reference during
; install - once Add-AppxPackage below finishes, the actual running application lives in its own
; MSIX-managed location (Program Files\WindowsApps\...), not here. These staged copies are not
; the thing a user interacts with afterward.
Source: "..\..\dist\DekhBhai.msix"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\msix\DekhBhaiSigning.cer"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\desktop\src\DekhBhai.App\Assets\DekhBhai.ico"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; Step 1: trust the self-signed certificate in the machine's TrustedPeople store - the same
; store/effect as the documented `Import-Certificate ... -CertStoreLocation Cert:\LocalMachine\
; TrustedPeople` step in docs/development/packaging.md, just run by the installer instead of
; asked of the user. certutil is a built-in Windows tool, not a downloaded script.
Filename: "{sys}\certutil.exe"; \
    Parameters: "-addstore -f ""TrustedPeople"" ""{app}\DekhBhaiSigning.cer"""; \
    StatusMsg: "Trusting Dekh Bhai's signing certificate..."; \
    Flags: runhidden waituntilterminated

; Step 2: install the MSIX for the current user. Add-AppxPackage is a PowerShell cmdlet with no
; certutil-style plain CLI equivalent for a standard per-user MSIX install - invoked here by the
; installer itself (never something the user types or sees a window for), which is what "not
; require PowerShell commands from the user" means: PowerShell as an internal implementation
; detail of our own installer is not the same as asking a friend to open PowerShell.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Add-AppxPackage -Path '{app}\DekhBhai.msix' -ForceApplicationShutdown"""; \
    StatusMsg: "Installing Dekh Bhai..."; \
    Flags: runhidden waituntilterminated

[UninstallRun]
; Uninstalling the wrapper must also remove the MSIX - otherwise "uninstall Dekh Bhai" from
; Settings > Apps would remove this installer's own registration but leave the actual app
; installed and still in the Start Menu, which would be confusing and wrong.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name '{#MyAppId}' | Remove-AppxPackage"""; \
    RunOnceId: "RemoveDekhBhaiAppx"; \
    Flags: runhidden waituntilterminated

[Icons]
; Optional only (per the brief) - MSIX already creates its own Start Menu entry automatically
; from AppxManifest.xml's VisualElements the moment Add-AppxPackage above completes; nothing
; needs to be done here for that. A desktop shortcut is NOT a plain shortcut to the exe path
; (that would run the exe outside its package identity, silently breaking the Graphics Capture
; border-suppression capability this app relies on MSIX for) - it launches through Explorer's
; shell:AppsFolder\<PackageFamilyName>!<AppId> activation path instead, which is the supported
; way to launch a packaged app with its identity intact, exactly like double-clicking the real
; Start Menu tile does.
Name: "{autodesktop}\Dekh Bhai"; \
    Filename: "{win}\explorer.exe"; \
    Parameters: "shell:AppsFolder\{#MyPackageFamilyName}!{#MyAppUserModelId}"; \
    IconFilename: "{app}\DekhBhai.ico"; \
    Tasks: desktopicon
