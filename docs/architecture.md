# Architecture notes

## Gate 1 foundations

- `VoiceBridge.Core` owns domain types and result/error contracts. It has no storage, UI, or logging dependency.
- `VoiceBridge.Storage` and `VoiceBridge.Export` depend inward on Core. The CLI composes those boundaries.
- Source values that may be absent or ambiguous stay nullable and raw values are retained beside any parsed form.
- Cancellation is carried as a `CancellationToken` at operation boundaries; Ctrl+C in the CLI cancels the active operation.
- The CLI uses `Microsoft.Extensions.Logging`. `VOICEBRIDGE_LOG_LEVEL` configures its minimum level; diagnostics go to stderr and command output stays on stdout. Logs must not include message bodies or media contents.

Takeout discovery and parsing are introduced only by their corresponding plan gates.
