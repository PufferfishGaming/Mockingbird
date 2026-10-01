# ADR-0001: One-file setup wizard built with WiX Burn around the MSI

Status: accepted (0.1.15)

## Context
Installation first meant an MSI plus a `.cmd` and a `.ps1` bootstrapper that downloaded the rest into `C:\Mockingbird Studio Installer`. It needed several files, an administrator prompt, and was fragile (URL case, tags, proxies).

## Decision
Ship one `Mockingbird-Studio-Setup.exe`: a WiX 6 Burn bundle with the standard bootstrapper UI (license link, one Install button, progress, finish with Launch). The existing per-user MSI is embedded unchanged, so its hash and integrity manifest stay valid. The MSI is installed with `ARPSYSTEMCOMPONENT=1`, so Apps & Features lists only the bundle.

## Consequences
- Per user, no administrator prompt; the .NET runtime is included. Upgrades from an older bundle work (verified 0.1.15 to 0.1.16).
- The build needs the `WixToolset.BootstrapperApplications.wixext` extension under `.tools/wix-extensions` (downloaded once, version-pinned).
- The file is about 190 MB because the MSI is embedded; every update downloads it in full.
- Unsigned, so SmartScreen warns.
