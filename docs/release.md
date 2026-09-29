# Release strategy

## Current package: portable x64 ZIP

Gate 10 produces `VoiceBridge-1.0.0-win-x64.zip`. It contains the self-contained application, its local runtime files, and the README, privacy statement, support guide, and sample screenshots. A tester extracts the folder and runs `VoiceBridge.exe`; removing that folder uninstalls the application without touching the separate output folder.

This package is self-contained for .NET and the Windows App SDK, targets x64 Windows 10 version 2004 (build 19041) or later, and has no installer service, registry setup, automatic update, account, or network processing path. It is not code signed, so Windows may show its normal download reputation prompt when the ZIP comes from an unfamiliar location.

## Other distribution options

- **Direct installer:** deferred. The portable package already provides a reversible install and clean folder removal, while a conventional installer would add machine-level install and uninstall behavior that the current product does not need.
- **MSIX:** deferred. It can provide package identity and managed installation, but requires package validation and an appropriate signing identity for direct distribution.
- **Microsoft Store:** evaluated as a later option only. Store submission, listing, and distribution are outside this gate and are not performed here.

No signing key or external account is used for this release candidate.

## Rebuild the release ZIP

Run this command from the repository root in a Visual Studio Developer PowerShell with the Windows and .NET SDK workloads installed. The script publishes the app into a new staging folder, packages it with the release documents and screenshots, then removes that temporary folder.

```powershell
./root/scripts/package-release.ps1 -OutputDirectory root\output\gate10-release -Version 1.0.0
```

The script refuses to overwrite a ZIP, rejects junctions and symbolic links in the output path, and only writes package output under `root/output/`. It publishes into a fresh staging directory so stale files cannot enter the ZIP, excludes PDB files, and prints the final path, entry count, byte size, and SHA-256.
