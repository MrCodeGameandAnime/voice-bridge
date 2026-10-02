# Unsigned test MSIX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a reproducible unsigned x64 VoiceBridge MSIX test package while preserving the existing portable ZIP release.

**Architecture:** Keep packaging in the existing WinUI desktop project using single-project MSIX tooling, enabled only by `root/scripts/package-msix.ps1`. Keep package identity and artwork in a project-level manifest, generate PRI/package output during MSBuild, and validate the final package before placing it under `root/output/`.

**Tech Stack:** .NET 10, WinUI 3 / Windows App SDK 1.8, MSBuild, Windows SDK MSIX tools, PowerShell, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-01-unsigned-msix-packaging.md`

## Global Constraints

- Preserve the existing self-contained portable x64 ZIP release path.
- Package identity name is `404Builds.VoiceBridge.Local`.
- Unsigned publisher is `CN=404 Builds, OID.2.25.311729368913984317654407730594956997722=1`.
- Build one unsigned x64 `.msix`, not an MSIX bundle, and do not install it during validation.
- Package version is four-part, derived from the existing three-part version (for example `1.0.0` becomes `1.0.0.0`).
- Generated package artifacts and temporary staging files must remain under `root/output/`.
- Refuse to overwrite existing packages and reject output paths outside `root/output/` or through reparse points.
- Do not create signing credentials or submit/publish through the Microsoft Store.
- Do not modify the original Google Voice Takeout archive or application archive-processing semantics.
- Run the existing Release build and full test suite before considering this gate complete.
- Work on the existing `main` branch; do not create a branch or worktree.

## Review Focus

- **Portable branding regression:** the prior logo files are gone, so Release builds or ZIP contents could still reference missing assets. Task 1 builds the app and inspects the portable ZIP for the replacement logo and PRI.
- **Manifest artwork drift:** a manifest path, identity, or OID typo could produce a package that cannot register. Task 2 adds xUnit contract tests for the identity and every referenced source asset.
- **Accidental signed or bundled output:** the test artifact could carry the wrong identity, a signature, or multiple architectures. Task 3 inspects the package manifest, signature entry, package count, and architecture before moving the artifact.
- **Unsafe output handling:** a path traversal, junction, or pre-existing artifact could overwrite data outside the generated output folder. Task 3 exercises outside-root, reparse-point, and existing-file rejection while confirming pre-existing marker contents remain unchanged.
- **Missing or incomplete packaging toolchain:** an MSBuild/SDK failure could leave a partial artifact that looks finished. Task 3 requires one complete MSIX with the executable, manifest, PRI, and referenced resources before reporting success.

---

### Task 1: Reconnect the app and portable ZIP to the replacement artwork

**Files:**
- Modify: `root/src/VoiceBridge.Desktop/VoiceBridge.Desktop.csproj`
- Modify: `root/src/VoiceBridge.Desktop/MainWindow.xaml`
- Modify: `root/scripts/package-release.ps1`

**Interfaces:**
- Consumes: the user-supplied assets under `root/src/VoiceBridge.Desktop/Assets`.
- Produces: app executable icon `Assets/AppIcon.ico` and baseline runtime logo `Assets/Square150x150Logo.png`; the portable publish stage must contain the latter at `Assets/Square150x150Logo.png`.
- Keep the existing `net10.0-windows10.0.26100.0` target, x64 runtime, self-contained setting, and ordinary `WindowsPackageType=None` behavior.

- [ ] **Step 1: Confirm the broken-asset baseline**

Run: `dotnet build root/VoiceBridge.sln --configuration Release`

Expected: the current missing `VoiceBridge.ico` or `VoiceBridgeMark.png` reference is reported, establishing the failing baseline.

- [ ] **Step 2: Relink the desktop app artwork**

Set `ApplicationIcon` to `Assets\AppIcon.ico`, change the `MainWindow.xaml` image URI to `ms-appx:///Assets/Square150x150Logo.png`, and update the desktop project's copied/published Content item from `VoiceBridgeMark.png` to `Square150x150Logo.png`.

- [ ] **Step 3: Relink portable release validation to the new runtime logo**

Change the required-file check in `root/scripts/package-release.ps1` to require `Assets\Square150x150Logo.png`; keep all package-only scaled artwork out of the portable ZIP.

- [ ] **Step 4: Build and create a fresh portable ZIP**

Run: `dotnet build root/VoiceBridge.sln --configuration Release`

Expected: `Build succeeded` with no missing artwork inputs.

Run: `./root/scripts/package-release.ps1 -OutputDirectory root\output\msix-validation\portable -Version 1.0.0`

Expected: one new ZIP is reported under `root/output/`.

- [ ] **Step 5: Inspect the portable ZIP contents**

Verify it contains `VoiceBridge.exe`, `VoiceBridge.pri`, and `Assets/Square150x150Logo.png`, contains no `VoiceBridgeMark.png` entry, and still includes the existing release documents and screenshots.

### Task 2: Add the unsigned package manifest and contract tests

**Files:**
- Create: `root/src/VoiceBridge.Desktop/Package.appxmanifest`
- Create: `root/src/VoiceBridge.Desktop/Properties/PublishProfiles/win10-x64.pubxml`
- Modify: `root/src/VoiceBridge.Desktop/VoiceBridge.Desktop.csproj`
- Create: `root/tests/VoiceBridge.Tests/MsixPackagingContractTests.cs`

**Interfaces:**
- Consumes: the replacement artwork linked in Task 1.
- Produces: package identity `404Builds.VoiceBridge.Local`; publisher `CN=404 Builds, OID.2.25.311729368913984317654407730594956997722=1`; `Square44x44Logo=Assets\AppList.png`; `Square150x150Logo=Assets\Square150x150Logo.png`; `Wide310x150Logo=Assets\Wide310x150Logo.png`; package `Properties/Logo=Assets\StoreLogo.png`.
- The package project properties must be opt-in for MSIX generation; the normal project build remains `WindowsPackageType=None`.

- [ ] **Step 1: Write the failing manifest contract tests**

Add `MsixPackagingContractTests.PackageManifest_UsesUnsignedDevelopmentIdentity` and assert `Name == "404Builds.VoiceBridge.Local"`, `Publisher == "CN=404 Builds, OID.2.25.311729368913984317654407730594956997722=1"`, and source-manifest `Version == "1.0.0.0"`.

Add `MsixPackagingContractTests.PackageManifest_ReferencesExistingArtwork` to resolve `Assets\\AppList.png`, `Assets\\Square150x150Logo.png`, `Assets\\Wide310x150Logo.png`, and `Assets\\StoreLogo.png` from the manifest under `root/src/VoiceBridge.Desktop/Assets` and assert each file exists. Find the repository root by walking parents from `AppContext.BaseDirectory` until `root/VoiceBridge.sln` exists, so the tests do not depend on the test runner's current directory.

- [ ] **Step 2: Run the focused tests before adding the manifest**

Run: `dotnet test root/tests/VoiceBridge.Tests/VoiceBridge.Tests.csproj --configuration Release --filter FullyQualifiedName~MsixPackagingContractTests`

Expected: FAIL because `Package.appxmanifest` does not exist yet.

- [ ] **Step 3: Add manifest, content resources, and opt-in single-project MSIX settings**

Create the manifest with the approved development-only identity, display names, full-trust desktop entry point, and the specified image resources. Include the supplied PNG families as MSBuild `Content` so PRI/package generation sees the existing scale and target-size qualifiers; keep the portable publish copy metadata on only `Assets/Square150x150Logo.png`. Add `EnableMsixTooling` and the single-project publish profile. Preserve `WindowsPackageType=None` as the ordinary build default; have the MSIX build mode opt into package generation and `WindowsPackageType=MSIX`.

Create `win10-x64.pubxml` for the existing x64 target, with no signing certificate and no package signing.

- [ ] **Step 4: Run focused contract tests**

Run: `dotnet test root/tests/VoiceBridge.Tests/VoiceBridge.Tests.csproj --configuration Release --filter FullyQualifiedName~MsixPackagingContractTests`

Expected: both manifest tests PASS.

### Task 3: Build and verify the unsigned MSIX through a guarded script

**Files:**
- Create: `root/scripts/package-msix.ps1`
- Create: `root/tests/scripts/package-msix-safety.tests.ps1`
- Modify: `root/src/VoiceBridge.Desktop/VoiceBridge.Desktop.csproj` only if an MSBuild property hook is needed to keep packaging opt-in.

**Interfaces:**
- Script parameters: `-OutputDirectory` (default `root\output\msix-test`), `-Version` (default `1.0.0`, three-part), and `-MSBuildPath` (default `MSBuild.exe`).
- Successful invocation returns `PackagePath`, byte size, and SHA-256 for `VoiceBridge-<Version>-win-x64-unsigned.msix`.
- Convert the three-part release version to a four-part MSIX version by appending `.0`, and pass it to MSBuild as `AppxPackageVersion`.

- [ ] **Step 1: Write the path-safety harness before the package script**

Add four checks to `package-msix-safety.tests.ps1`: `RejectsOutputOutsideRootBeforeCallingMsBuild`, `RefusesExistingArtifactWithoutChangingMarker`, `RejectsOutputPathTraversingJunction`, and `LeavesNoFinalArtifactWhenMsBuildIsUnavailable`.

- Outside-root check: pass an unavailable MSBuild path and assert the script rejects the output path before attempting MSBuild lookup.
- Existing-artifact check: create a unique output directory under `root/output/` with `VoiceBridge-9.8.7-win-x64-unsigned.msix` containing known marker text; assert refusal and that the marker's SHA-256 is unchanged.
- Junction check: create a temporary junction under `root/output/` pointing at a test-owned temporary directory; target a child path through that junction and assert rejection without creating files in the junction target. Remove only the test-created link and empty temporary directory.
- Missing-tool check: target a fresh unique output directory and provide an unavailable MSBuild path; assert failure and no final `.msix` or leftover staging directory.

- [ ] **Step 2: Run the safety harness before the package script exists**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File root/tests/scripts/package-msix-safety.tests.ps1`

Expected: FAIL because `root/scripts/package-msix.ps1` has not been created.

- [ ] **Step 3: Implement version, path, and overwrite validation**

Require `Version` to match `major.minor.patch`. Resolve output paths against the repository root, require them to remain under `root/output/`, reject reparse points in every existing path segment, and refuse to overwrite the final `.msix`. Create only unique, script-owned staging paths under `root/output/`.

- [ ] **Step 4: Add the MSBuild package invocation**

Build the desktop project in Release for x64 with `RuntimeIdentifier=win-x64`, `GenerateAppxPackageOnBuild=true`, `WindowsPackageType=MSIX`, `AppxPackageSigningEnabled=false`, `AppxBundle=Never`, and the requested four-part `AppxPackageVersion`. Direct package output to the unique staging directory.

- [ ] **Step 5: Validate and atomically place the final package**

Require exactly one `.msix`. Inspect its ZIP entries and generated `AppxManifest.xml`; assert the expected identity and requested four-part version, `ProcessorArchitecture="x64"`, absence of `AppxSignature.p7x`, and presence of `VoiceBridge.exe`, `resources.pri`, every manifest-referenced base image, plus representative variants `Assets/AppList.scale-100.png`, `Assets/AppList.targetsize-16_altform-unplated.png`, `Assets/Square150x150Logo.scale-200.png`, and `Assets/StoreLogo.scale-200.png`. Run Windows SDK `MakeAppx.exe validate /p <staged-package>` when available. Move the validated package to the final path only after all checks pass; remove only the staging paths created by the script.

- [ ] **Step 6: Run the safety harness and build a fresh package**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File root/tests/scripts/package-msix-safety.tests.ps1`

Expected: all four safety checks PASS, and the pre-existing marker hash is unchanged.

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File root/scripts/package-msix.ps1 -OutputDirectory root\output\msix-validation\final -Version 1.0.0`

Expected: one package is reported with its path, size, and SHA-256; the generated manifest version is `1.0.0.0` and its processor architecture is `x64`.

### Task 4: Document the local test package and finish the gate

**Files:**
- Modify: `docs/release.md`
- Modify: `C:\Users\User\Documents\Dev\C#\.404 Voice Bridge\.codex\notes\overnight-build-report.md`

**Interfaces:**
- Consumes: the `package-msix.ps1` parameters and artifact name from Task 3.
- Produces: separate instructions for portable ZIP creation and unsigned Windows 11 test package creation/installation.

- [ ] **Step 1: Update release documentation**

Keep the portable ZIP as the current distributable test package. Describe the unsigned MSIX as a local Windows 11 test artifact, document `./root/scripts/package-msix.ps1 -OutputDirectory root\output\msix-test -Version 1.0.0`, and give the separate `Add-AppxPackage -Path <package> -AllowUnsigned` test-install command with the documented admin note. State that it is not a Store or broad-distribution package and does not share the future signed identity.

- [ ] **Step 2: Run the full Release build and test suite**

Run: `msbuild root\VoiceBridge.sln /restore /p:Configuration=Release`

Expected: MSBuild exits with code 0.

Run: `dotnet test root/tests/VoiceBridge.Tests/VoiceBridge.Tests.csproj --configuration Release`

Expected: all tests pass, including both new MSIX manifest contract tests.

- [ ] **Step 3: Build a fresh MSIX and verify the portable package again**

Run the unsigned package script to `root/output/msix-test` and inspect the final artifact as in Task 3. Confirm the portable ZIP from Task 1 remains intact and contains the expected app/runtime files.

- [ ] **Step 4: Append the overnight report and capture final repository status**

Record commit SHA, implementation summary, tests added and passing count, Release build result, package path/size/SHA-256, package manifest identity/version, package validation result, portable ZIP result, known limitations, and unresolved issues in the existing overnight report. Confirm all generated artifacts are confined to ignored `root/output/`.

- [ ] **Step 5: Commit and push the completed packaging gate on `main`**

Commit the gate changes together with message `feat(packaging): add unsigned test MSIX path`, then push the existing `main` branch. Do not create a branch or worktree.
