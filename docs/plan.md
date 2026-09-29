PROJECT: 404 VoiceBridge
WORKING NAME: VoiceBridge
OWNER: 404 Builds

MISSION

Build a local-first Windows application that takes a Google Voice Google Takeout export and reconstructs it into a clean, searchable, understandable archive.

The immediate commercial goal is not to build a giant migration suite.

The immediate goal is:

    Google Voice Takeout
        ->
    parse and reconstruct the user's history
        ->
    store it cleanly
        ->
    let the user inspect/export it
        ->
    clearly account for malformed or skipped data

This project is being built under time pressure.

Optimize for:
- correctness
- fast iteration
- deterministic behavior
- testability
- a shippable narrow MVP

Do NOT optimize for:
- speculative features
- abstraction for abstraction's sake
- cloud architecture
- cross-platform support
- enterprise architecture
- generalized Google Takeout support
- AI features
- monetization plumbing yet

The product must work locally.

No user messages, attachments, voicemail files, or archives should be uploaded to any server.

======================================================================
CORE PRODUCT PROMISE
======================================================================

The user should be able to:

1. Select a Google Voice Takeout ZIP or extracted folder.
2. Let VoiceBridge scan the export.
3. See what was found.
4. Reconstruct message conversations.
5. Preserve MMS/media relationships where possible.
6. Index calls and voicemails where present.
7. Search/browse reconstructed history.
8. Export useful representations.
9. See exactly what could not be processed and why.

The source Takeout data must be treated as immutable.

Never modify the original archive or extracted files.

======================================================================
TECHNICAL DIRECTION
======================================================================

Language:
- C#

Runtime:
- Use the installed stable .NET SDK.
- Prefer .NET 10 if available.
- Do not downgrade or install additional SDKs without need.

UI:
- WinUI 3 eventually.
- WinUI 3 is explicitly NOT part of the early gates.

Architecture:

    VoiceBridge.Core
        Domain models
        Takeout discovery
        Parsing
        Normalization
        Conversation reconstruction
        Validation

    VoiceBridge.Storage
        SQLite
        persistence
        indexes
        search

    VoiceBridge.Export
        HTML
        CSV
        JSON/reporting
        later migration formats

    VoiceBridge.Cli
        headless import/debug interface

    VoiceBridge.Tests
        fixtures
        regression tests
        integration tests

    VoiceBridge.WinUI
        added only after the CLI pipeline is proven

HTML parsing:
- Use a real HTML parser such as AngleSharp or HtmlAgilityPack.
- Do not structurally parse exported HTML using regex.

Storage:
- SQLite.
- Design for libraries containing tens or hundreds of thousands of records.
- Do not require the entire archive to remain resident in memory.

======================================================================
DOMAIN MODEL
======================================================================

Start with explicit models roughly equivalent to:

SourceArchive
SourceFile
Conversation
Participant
Message
Attachment
CallRecord
Voicemail
ImportIssue
ImportRun

These names may evolve if the actual Google Voice export format demands it.

Important:
Do not force source data into assumptions that are not supported by evidence.

Unknown should remain unknown.

Malformed should remain malformed.

Ambiguous should remain ambiguous.

Do not silently invent:
- participants
- phone numbers
- timestamps
- attachment relationships
- message direction
- conversation membership

======================================================================
ABSOLUTE RULE: DO NOT INVENT GOOGLE TAKEOUT FORMAT
======================================================================

The parser must be grounded in actual Google Voice Takeout structure.

Do not implement a parser based on what Google Voice exports are assumed to
look like.

Use, in descending order of preference:

1. Real user-provided Google Voice Takeout samples.
2. Real sanitized samples already available to the project.
3. Publicly documented real Google Voice Takeout examples.
4. Existing open-source parsers as format references.
5. Synthetic fixtures ONLY when modeling an already-supported observed format.

Synthetic fixtures are useful for testing.

Synthetic fixtures are NOT evidence that the parser supports real Takeout data.

If no real export is available, explicitly mark the relevant functionality as
"implemented against documented/synthetic fixtures, not yet validated against
a real export."

======================================================================
GATE POLICY
======================================================================

Every gate is a hard checkpoint.

For each gate:

1. Implement only the scope of that gate.
2. Run all tests.
3. Fix regressions.
4. Summarize:
   - what changed
   - files changed
   - tests added
   - test results
   - known limitations
   - unresolved assumptions
5. Stop.
6. Wait for explicit user approval before entering the next gate.

Do not quietly continue into later gates.

Do not spawn background agents, secondary agents, or independent coding chats.
If additional agentic help appears necessary, explain why first and wait for
explicit approval.

======================================================================
GATE 0 — FORMAT RECONNAISSANCE
======================================================================

GOAL

Understand what we are actually parsing before building the parser.

TASKS

- Investigate the known structure of Google Voice Takeout exports.
- Identify:
  - directory structure
  - message files
  - SMS representation
  - MMS representation
  - group messages
  - attachments
  - calls
  - voicemails
  - voicemail transcripts
  - timestamps
  - participants
  - phone-number formatting
  - filename patterns
- Examine existing public parsers where useful.
- Document variations and uncertainty.
- Identify what cannot yet be confirmed without a real Takeout sample.

DELIVERABLE

docs/google-voice-takeout-format.md

Include:

- observed structures
- examples
- source/evidence for each structural assumption
- uncertain areas
- initial parsing strategy
- proposed normalized data model

NO APPLICATION CODE YET except tiny exploratory code if necessary.

EXIT CRITERIA

We can explain what a Google Voice Takeout contains and how VoiceBridge intends
to discover and parse it without pretending unknown details are known.

STOP AFTER GATE 0.

======================================================================
GATE 1 — SOLUTION SCAFFOLD
======================================================================

GOAL

Create the production structure without implementing the product yet.

CREATE

VoiceBridge.sln

Projects:

src/
    VoiceBridge.Core/
    VoiceBridge.Storage/
    VoiceBridge.Export/
    VoiceBridge.Cli/

tests/
    VoiceBridge.Tests/

docs/

Define initial domain models.

Define interfaces only where they already serve a concrete boundary.

Avoid architecture astronautics.

Add:

- logging strategy
- result/error types
- cancellation support
- basic configuration
- test infrastructure

CLI should compile and display help.

Example:

    voicebridge --help

EXIT CRITERIA

- entire solution builds cleanly
- tests execute
- CLI launches
- no Takeout functionality yet
- project dependencies flow inward sensibly

STOP AFTER GATE 1.

======================================================================
GATE 2 — TAKEOUT DISCOVERY AND INVENTORY
======================================================================

GOAL

Accept a ZIP or extracted Takeout directory and inventory its contents without
yet attempting full reconstruction.

CLI:

    voicebridge scan <source>

Support:

- .zip source
- extracted directory source

The scanner should identify candidate Google Voice content and produce:

- number of source files
- candidate message files
- attachments
- voicemail media
- other Voice files
- unknown/unclassified files
- warnings

Generate a machine-readable scan report.

Example output:

    Google Voice export detected

    Files scanned:             8,412
    Message files:             3,981
    Candidate attachments:     1,024
    Voicemail files:             188
    Other recognized files:      211
    Unknown files:                 8

Do not modify/extract destructively into the user's source.

Temporary extraction/storage must be isolated.

TESTS

Include fixtures for:

- ZIP input
- directory input
- missing Voice folder
- corrupt ZIP
- empty archive
- unrelated Takeout data
- malformed filenames
- cancellation

EXIT CRITERIA

VoiceBridge can reliably answer:

"What is in this export?"

It does NOT yet need to reconstruct conversations.

STOP AFTER GATE 2.

======================================================================
GATE 3 — MESSAGE PARSING
======================================================================

GOAL

Parse real Google Voice message records into normalized Message records.

Support the simplest confirmed message format first.

Parse only fields supported by source evidence.

Capture:

- timestamp
- body
- participants
- direction when determinable
- source file
- attachment references where determinable
- raw/source identity needed for debugging

Malformed records must generate ImportIssue entries instead of crashing the
entire import.

Do not silently discard unsupported records.

Add fixture-based regression tests.

TEST PROPERTY

For every supported fixture:

    source input
        ->
    parsed normalized representation
        ==
    expected normalized representation

EXIT CRITERIA

Supported Google Voice message files can be parsed deterministically into
normalized records with useful error reporting.

STOP AFTER GATE 3.

======================================================================
GATE 4 — CONVERSATION RECONSTRUCTION
======================================================================

GOAL

Turn individual parsed message records into useful conversation history.

Implement:

- participant normalization
- conservative phone-number normalization
- one-to-one conversation grouping
- confirmed group conversation grouping
- chronological ordering
- duplicate detection where identity is sufficiently strong
- attachment association

Important:

Do not merge two conversations solely because they look similar.

Do not destroy source identity.

Every reconstructed record should remain traceable back to its source file.

Add detailed tests for:

- same participant formatted differently
- group messages
- duplicated source files
- identical message bodies at different times
- multiple attachments
- ambiguous membership
- missing participant data

EXIT CRITERIA

VoiceBridge can reconstruct supported message histories into coherent
conversations without silently inventing relationships.

STOP AFTER GATE 4.

======================================================================
GATE 5 — SQLITE STORAGE + END-TO-END CLI IMPORT
======================================================================

GOAL

Turn the parser into an actual system.

CLI:

    voicebridge import <source> --output <directory>

Pipeline:

    source
      ->
    scan
      ->
    parse
      ->
    normalize
      ->
    reconstruct
      ->
    SQLite
      ->
    import report

SQLite should contain normalized tables for the supported models.

Add useful indexes.

Prefer SQLite FTS for message search if appropriate.

Provide progress events through the core so both CLI and future UI can consume
the same progress information.

Example CLI:

    Scanning archive...
    Parsing messages...
    12,000 / 48,291
    Reconstructing conversations...
    Writing database...

    Import complete.

    Conversations: 241
    Messages: 48,291
    Attachments: 1,284
    Warnings: 17
    Errors: 3

    Database:
    C:\...\voicebridge.db

    Report:
    C:\...\import-report.json

IMPORT REPORT

Must include:

- input identity
- start/end time
- files scanned
- records parsed
- records skipped
- warnings
- errors
- unsupported structures
- relevant source filenames

EXIT CRITERIA

A single CLI command can process supported Google Voice Takeout input
end-to-end and create a reusable local database.

THIS IS THE FIRST MAJOR MVP CHECKPOINT.

STOP AFTER GATE 5.

======================================================================
GATE 6 — HUMAN-READABLE ARCHIVE EXPORT
======================================================================

GOAL

Produce something useful even without a GUI.

Generate a local static archive:

output/
    archive/
        index.html
        conversations/
        assets/
    voicebridge.db
    import-report.json

Archive requirements:

- conversation list
- chronological messages
- participant labels
- timestamps
- attachment links
- search if practical without requiring a server
- responsive enough for ordinary desktop viewing
- no internet dependency

Also support:

    voicebridge export html ...
    voicebridge export csv ...

CSV export should preserve enough identifiers to avoid flattening away useful
relationships.

EXIT CRITERIA

A user can take a Google Voice export and receive a clean browsable archive
without installing any additional software.

At this gate we have something potentially useful enough to show testers.

STOP AFTER GATE 6.

======================================================================
GATE 7 — CALLS, VOICEMAILS, AND MEDIA
======================================================================

GOAL

Expand beyond message history using only formats verified during Gate 0 or from
real examples encountered during development.

Implement:

- call records
- voicemail records
- voicemail audio association
- voicemail transcript association
- MMS/media organization

Expose them in:

- database
- reports
- HTML archive

Do not block the entire import because an optional media file is missing.

Missing relationships should become ImportIssue records.

EXIT CRITERIA

The supported Google Voice export is represented substantially as one coherent
local archive rather than merely SMS history.

STOP AFTER GATE 7.

======================================================================
GATE 8 — RESILIENCE / REAL-WORLD REGRESSION PASS
======================================================================

GOAL

Make the parser boring.

Feed it ugly examples.

For every real failure:

    reproduce
      ->
    isolate
      ->
    fixture
      ->
    regression test
      ->
    fix
      ->
    rerun entire suite

Explicitly test:

- large archives
- weird HTML
- Unicode
- emoji
- HTML entities
- duplicate data
- missing attachments
- invalid dates
- corrupt files
- interrupted operation
- paths containing spaces
- long Windows paths
- unusual phone numbers
- group conversations
- partial exports

Measure basic performance.

Do not prematurely micro-optimize.

EXIT CRITERIA

Known real failures become regression tests and the end-to-end pipeline remains
stable.

STOP AFTER GATE 8.

======================================================================
GATE 9 — WINUI 3 FRONTEND
======================================================================

DO NOT BEGIN THIS GATE UNTIL THE CLI PIPELINE IS PROVEN.

GOAL

Put a polished Windows interface over the existing engine.

The UI must call the same Core APIs used by the CLI.

The UI must not contain parsing logic.

INITIAL UI FLOW

Welcome
    ->
Choose Google Voice Takeout
    ->
Choose destination
    ->
Scan summary
    ->
Import
    ->
Results

Initial controls:

- Select ZIP
- Select Folder
- Select Output Folder
- Scan
- Start Import
- Cancel
- Open Archive
- View Issues
- Open Output Folder

Progress display:

    Scanning...
    Found 48,291 messages
    Found 1,284 attachments
    Found 188 voicemails

    Importing...
    ████████████░░░░ 71%

    Messages          34,288
    Conversations        198
    Warnings               9

Use a background task.

Never freeze the UI during parsing.

Support cancellation.

The user should not see:
- console windows
- stack traces
- internal source paths unless relevant
- implementation details

EXIT CRITERIA

An ordinary Windows user can complete the entire supported workflow without
opening a terminal.

STOP AFTER GATE 9.

======================================================================
GATE 10 — RELEASE CANDIDATE
======================================================================

GOAL

Prepare the narrow product for real testers / first customers.

Tasks:

- application branding
- 404 Builds attribution
- icons
- version information
- About screen
- privacy statement
- clear local-only explanation
- useful user-facing errors
- release build
- installation strategy
- clean uninstall
- crash logging that remains local unless user explicitly chooses to share it
- sample screenshots
- basic README/support docs

Do NOT add accounts.

Do NOT add telemetry by default.

Do NOT add cloud processing.

Do NOT add payment/licensing yet unless explicitly requested.

Evaluate:

- direct installer
- MSIX
- Microsoft Store later

EXIT CRITERIA

A tester who knows nothing about the implementation can install VoiceBridge,
process an export, inspect the result, and understand failures.

STOP AFTER GATE 10.

======================================================================
POST-MVP — DO NOT BUILD WITHOUT APPROVAL
======================================================================

Potential later work:

- SMS Backup & Restore XML export
- Android migration workflows
- Google Messages migration support
- advanced PDF export
- contact enrichment
- direct migration tools
- repair/recovery features
- licensing/payment
- automatic updates
- Mac version
- other Takeout products
- AI summaries/search
- cloud services

These are not MVP requirements.

======================================================================
CRITICAL ENGINEERING PRINCIPLES
======================================================================

1. Source data is immutable.

2. Every normalized item should retain provenance.

3. Unknown remains unknown.

4. One malformed file must not kill a 50,000-message import.

5. Import results must be auditable.

6. CLI and UI use the same core.

7. WinUI does not own business logic.

8. Real failures become fixtures.

9. Tests define behavior.

10. Do not optimize hypothetical problems.

11. Do not rewrite working architecture without a demonstrated reason.

12. Shipping a narrow working product is more important than building a broad
    elegant platform.

======================================================================
OVERNIGHT TARGET
======================================================================

The ideal overnight stopping point is Gate 5 or Gate 6.

Success tomorrow morning would look like:

    voicebridge import takeout.zip --output result

and receiving:

    Import complete

    Conversations: 284
    Messages: 63,119
    Attachments: 1,481
    Voicemails: 0 / unsupported until later gate
    Warnings: 12
    Errors: 2

    result/
        voicebridge.db
        import-report.json
        archive/

If this works against a REAL Google Voice Takeout export, the central product
hypothesis has survived its first technical test.

Do not sacrifice this milestone to work on UI polish.