# Unsigned test MSIX packaging

**Status:** Design approved; awaiting spec review before implementation
**Date:** 2026-10-01

## Goal

Add a repeatable, local-only path that produces an unsigned x64 MSIX test package for the VoiceBridge WinUI desktop app. Keep the existing self-contained portable ZIP path intact. Connect the replacement app and package artwork already supplied in `root/src/VoiceBridge.Desktop/Assets` to the app and manifest, and ensure the MSIX build produces the package PRI through the Windows packaging toolchain.

## Non-goals

- Signing the package, creating or storing a certificate/private key, or preparing a signed release identity.
- Microsoft Store submission, listing, screenshots, or publication.
- Installing the package as part of the packaging script.
- Changing application behavior, archive processing, or the CLI.
- Replacing the portable ZIP or changing its installation model.
- Adding a separate Windows Application Packaging Project.

## Current state

- `VoiceBridge.Desktop.csproj` targets `net10.0-windows10.0.26100.0`, x64, self-contained, and currently sets `WindowsPackageType` to `None`.
- The project still references the removed `Assets/VoiceBridge.ico` and `Assets/VoiceBridgeMark.png` files.
- The user supplied `Assets/AppIcon.ico`, scalable `AppList` images, `Square150x150Logo` images, `Wide310x150Logo.png`, `StoreLogo` images, and `MedTile` scale images.
- `root/scripts/package-release.ps1` builds a self-contained publish directory and packages it as the portable ZIP under `root/output/`.
- `CopyWinUiPriToPublishDirectory` already copies the generated app PRI file into publish output for the portable ZIP.
- `docs/release.md` currently says MSIX is deferred.

## Approaches considered

1. **Single-project MSIX as an opt-in build mode (chosen).** Add the manifest and package resources to the existing WinUI desktop project; enable package generation only from a dedicated script. This preserves the current solution shape and keeps the portable build mode available.
2. **Separate Windows Application Packaging Project.** This would isolate packaging settings, but adds another project and solution configuration for a single executable that already uses WinUI 3.
3. **Manual MakePri/MakeAppx workflow.** This would duplicate resource and manifest handling in the release script and risk diverging from the normal MSBuild packaging pipeline.

Microsoft documents single-project MSIX for WinUI 3 desktop projects and MSBuild package generation with `GenerateAppxPackageOnBuild`; it does not support an MSIX bundle in this mode. The app currently has one executable and targets x64, so the single-project path fits this package. See [single-project MSIX packaging](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix).

## Package identity and test-only boundary

Use the stable development package identity:

- Identity name: `404Builds.VoiceBridge.Local`
- Publisher: `CN=404 Builds, OID.2.25.311729368913984317654407730594956997722=1`
- Version: four-part package version derived from the existing `major.minor.patch` release version (for example, `1.0.0.0` for `1.0.0`).
- Display name: `VoiceBridge`
- Publisher display name: `404 Builds`
- Architecture: x64; produce one `.msix`, not a bundle.

The required OID marks this as an unsigned package. Windows gives it an identity distinct from any future signed package, so the signed release will be a separate install identity and will not upgrade this test package in place. This artifact is only for local Windows 11 testing; the Microsoft guidance explicitly says not to distribute unsigned packages broadly. Installing an unsigned package is outside the packaging script and may require an elevated PowerShell session. See [Create an unsigned MSIX package](https://learn.microsoft.com/en-us/windows/msix/package/unsigned-package).

## Asset and PRI wiring

- Set the executable icon to `Assets/AppIcon.ico`.
- Replace runtime/XAML references to the removed `VoiceBridgeMark.png` with `Assets/Square150x150Logo.png`, retaining that one baseline brand image in the portable publish output.
- Add `Package.appxmanifest` to `root/src/VoiceBridge.Desktop` and point its app visual elements to `Assets/AppList.png` (44×44), `Assets/Square150x150Logo.png` (150×150), and `Assets/Wide310x150Logo.png` (310×150) where the corresponding manifest slots apply.
- Point the package `Properties/Logo` resource to `Assets/StoreLogo.png` (50×50).
- Keep the supplied scale and target-size variants under their existing names so the package resource index can select them. Do not duplicate or rename the user's assets. The `MedTile` images are not mapped to a manifest slot unless implementation verifies a supported slot that uses them; the manifest's square tile can use the supplied `Square150x150Logo` family.
- Generate the package's `resources.pri` as part of the MSIX build; do not check in a generated PRI. Preserve the existing portable-publish PRI copy target.

## Build and output shape

- Add a dedicated `root/scripts/package-msix.ps1` script with version, output-directory, and MSBuild path parameters, following the path-safety conventions in `package-release.ps1`.
- Make package generation explicitly opt-in through the script's MSBuild properties. A regular desktop Release build and the portable ZIP build remain unpackaged by default.
- Build the app in Release for x64 and generate an unsigned `.msix` with package signing disabled and bundle generation disabled.
- Write only the final artifact and temporary staging files under `root/output/`; default the final output to `root/output/msix-test` and refuse to overwrite an existing package. Reject output paths outside `root/output` and paths passing through reparse points. Clean only staging paths created by this script.
- Use a clear artifact name such as `VoiceBridge-1.0.0-win-x64-unsigned.msix`.
- Keep `package-release.ps1` as the portable ZIP path and update its required app icon/brand-mark files to the new supplied asset names without adding the complete MSIX artwork set to the ZIP.
- Update `docs/release.md` to distinguish the portable ZIP from the local unsigned test MSIX, and document the Windows 11 test-install command separately from package creation.

## Validation

1. Run the existing Release build and full test suite as required for this repository's gate workflow.
2. Run the MSIX packaging script and confirm the result is a fresh, unsigned x64 `.msix` under `root/output/`.
3. Inspect the package container and confirm it includes the expected package manifest with the development identity/OID, `VoiceBridge.exe`, generated `resources.pri`, and referenced image resources; use the Windows SDK package validation tool if it is available.
4. Confirm the ordinary unpackaged Release build and portable ZIP path still use the new app icon/brand asset and preserve their existing behavior.
5. Do not install the package or alter the user's app registrations as part of validation. The deliverable for this task is the unsigned test artifact and reproducible packaging path.

## Risks and limits

- Unsigned package installation is intended for Windows 11 testing, not broad distribution, and may require administrator rights because the package contains executable content.
- The future signed identity and distribution model remain undecided. A signed build must remove the unsigned OID and use a publisher that matches its signing identity; this test identity is intentionally not upgrade-compatible with it.
- The Windows SDK and single-project MSIX packaging tooling must be available on the build host. The portable Release build remains available if the package tooling is absent, but that would block producing the requested MSIX until the tooling is available.
