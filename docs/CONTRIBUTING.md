# Contributing to Voice Bridge

Thanks for helping improve Voice Bridge. It is a Windows application and command-line tool for browsing Google Voice Takeout data locally.

## Start with an issue

Before preparing a code change, open an issue describing the bug or proposed improvement and wait for maintainer feedback. Issue-first pull requests are welcome, but changes are reviewed and accepted case by case. Keep each pull request focused and link it to the issue it addresses.

You may view the source on GitHub, but do not compile, build, or run it unless the maintainer has given explicit written permission. Permission to work on a discussed contribution may be limited to that contribution and its testing; it does not authorize distributing a compiled copy.

For bug reports, include the Voice Bridge version, Windows version, steps to reproduce, and what you expected to happen. Remove personal information before posting.

## Protect Takeout data

Never attach a real Takeout ZIP, extracted Takeout files, Voice Bridge database, generated archive, export, or unreviewed crash log to an issue or pull request. These may contain private messages, phone numbers, voicemail transcripts, media, or local file paths. Review screenshots and logs before sharing them, too.

Use synthetic data for examples and regression fixtures. Do not modify the original Takeout source during development or validation. For parser and import fixes, add a small synthetic fixture and regression test when practical.

## Repository layout

- `docs/` — project documentation.
- `root/src/VoiceBridge.Desktop/` — WinUI desktop application.
- `root/src/VoiceBridge.Cli/` — command-line application.
- `root/src/VoiceBridge.Core/`, `VoiceBridge.Storage/`, and `VoiceBridge.Export/` — core, storage, and export code.
- `root/tests/VoiceBridge.Tests/` — automated tests.
- `root/VoiceBridge.sln` — Visual Studio solution.

## Build and run checks

Run these checks only after receiving the maintainer's explicit written permission to build or test a discussed contribution. Install the .NET 10 SDK. Building the full desktop solution requires Windows and MSBuild with the Windows SDK and Windows App SDK dependencies used by the project.

From the repository root, run the test project checks used by CI:

```powershell
dotnet restore root/tests/VoiceBridge.Tests/VoiceBridge.Tests.csproj
dotnet build root/tests/VoiceBridge.Tests/VoiceBridge.Tests.csproj --configuration Release --no-restore
dotnet test root/tests/VoiceBridge.Tests/VoiceBridge.Tests.csproj --configuration Release --no-build --no-restore
```

On Windows, build the full solution with:

```powershell
msbuild root\VoiceBridge.sln /restore /p:Configuration=Release
```

## Implementation guidelines

- Keep Takeout processing local and preserve the source unchanged.
- Use the existing services and storage APIs; keep parsing and business rules out of the UI.
- Preserve provenance and make malformed, unknown, ambiguous, or unresolved data visible rather than silently dropping or guessing.
- Avoid unrelated architectural changes and new network, cloud, telemetry, or Google API dependencies.
- Update relevant documentation when behavior or user-facing steps change.

## Pull request checklist

- [ ] The pull request links to an issue discussed with the maintainer.
- [ ] The change is focused and its user-visible behavior is explained.
- [ ] Relevant tests pass; bug fixes include regression coverage when practical.
- [ ] No real Takeout or other personal data is included.
- [ ] Documentation is updated if needed.
