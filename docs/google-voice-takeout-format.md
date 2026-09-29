# Google Voice Takeout format reconnaissance

**Gates:** 0 — format reconnaissance; 0.5 — real export validation; 3 — message parser validation
**Status:** Gate 0.5 inventory and Gate 3 message parser validation completed against the ignored real export in this repository.
**Scope:** Gate 0/0.5 record the format evidence and inventory; Gate 3 adds a message-page parser validated against this one export. The source archive was read in place, not modified or extracted. Parser validation is evidence about this sample, not a general Google format contract.

## Findings at a glance

Google officially says Voice exports can include call logs, text messages, voicemails and their transcripts, voicemail greetings, billing history, the Google Voice number, and the service address. Its documentation does not specify the on-disk layout or HTML schema. [S1]

Public parser documentation and user reports commonly show the data under `Takeout/Voice/Calls/`, with HTML files for text conversations and call-related events, plus referenced media files. This is useful reconnaissance, not a stable Google format contract. [S3][S4][S5][S6]

The real archive now confirms that layout for this export and adds sibling `Spam/` and `Phones.vcf` entries. It contains 8,946 files, including 7,624 HTML records and 1,319 media files. The full content-free inventory and shape catalogue are in [real-export-observations.md](real-export-observations.md). Gate 3's message-page parser recognizes the 2,443 message pages and preserves all 18,732 rows, with 61 row-scoped issues for empty `tel:` values. This validates one export only; it is not a Google format contract.

## Evidence levels

- **Official product scope:** Google confirms which categories may be exported, but not their filenames, folders, or serialization. [S1]
- **Observed by third-party parsers:** Maintainers describe parsing or testing against their own Takeout exports. These reports help identify likely variants and failure cases, but are not a specification and often do not publish the raw source files. [S3][S4][S5]
- **Community or secondary descriptions:** Useful cross-checks for likely folder/file examples; not authoritative and not independently verified here. [S6][S7]

## Commonly reported directory and file layout

The following layout was observed in this archive. It remains an example rather than a canonical contract:

```text
takeout-<export-id>.zip
└── Takeout/
    ├── archive_browser.html
    └── Voice/
        ├── Calls/                 # 8,655 files, directly in this folder
        │   ├── <label> - Text - <UTC-timestamp>.html
        │   ├── <label> - <event> - <UTC-timestamp>.html
        │   ├── <referenced-media-file>
        │   └── ...
        ├── Spam/                  # 290 files
        └── Phones.vcf
```

The `Voice/Calls/` path and HTML-per-conversation/event pattern recur in public parser references and are confirmed in this archive. All 8,655 `Calls` files are direct children; this archive also has a `Voice/Spam/` directory and a `Voice/Phones.vcf` file. Do not assume all Voice data is under `Calls`. [S3][S4][S5]

Google's general Takeout instructions say an extracted archive may contain `archive_browser.html`, which helps a person inspect the export. The Voice-specific help page confirms categories, not whether every archive includes this file. [S2]

| Area | Evidence observed | Confidence and limits |
|---|---|---|
| Exported categories | Call logs, text messages, voicemails and transcripts, voicemail greetings, billing history, Voice number, and service address. | High for product scope; Google does not document filenames or formats here. [S1] |
| Calls directory | `Takeout/Voice/Calls/` is reported by parsers and present in this archive; its 8,655 files are directly in the folder. | Confirmed for this archive only. A sibling `Spam/` directory also contains Voice records. [S3][S5][S6] |
| HTML records | Individual HTML files contain message conversations or call/voicemail events; message files can contain many message rows. | Confirmed in this archive: 2,443 message/conversation pages contain 18,732 rows. There are 33 page shapes and 12 message-row shapes. [S3][S4] |
| Voice number/contact export | This archive contains one `Voice/Phones.vcf` file and two additional VCF files under `Calls/`. | VCF presence and path confirmed here; VCF contents were not inspected. The official category list only confirms the number is exportable. [S1][S7] |
| Audio/media | This archive contains 936 image files, 348 audio-extension files, and 35 video/container-extension files; HTML references many of them. | Extensions and counts confirmed for this archive. 38 local media references remain unresolved by exact path, basename, or extensionless-stem matching. [S3][S5][S6][S7] |

## Filename and record examples

One reported text filename is:

```text
+1234567890 - Text - 2023-01-15T14_30_00Z.html
```

All 7,615 filenames with a recognized label in this archive use `<label> - <kind> - YYYY-MM-DDTHH_mm_ssZ.html`; the other nine are named `Group Conversation - <timestamp>.html`. This archive uses `Text`, `Placed`, `Received`, `Missed`, and `Voicemail`; no `Recorded` filename label was found. These are observations from one export, not an exhaustive enum.

Filenames are not a safe source of normalized identity or event data. Names may be phone numbers, saved contact labels, or group names. Contact names can contain punctuation, and media filenames may be transformed or deduplicated. One maintainer reports media filenames truncated to 50 characters with a parenthesized suffix on duplicates in their archive. [S5]

The filename timestamp is UTC (`Z`) with colons replaced by underscores in this archive. Message and event timestamps in HTML are ISO-like values with a numeric offset. Preserve both raw values; do not infer that they are interchangeable. [S4][S5]

## HTML content reported by parsers

The following selectors were found in historical parser references and confirmed in this archive. They remain observations, not a parser contract:

| Content | Historical DOM observation | Important caveat |
|---|---|---|
| Text message rows | `div.message`; a row may contain `abbr.dt` with a `title` timestamp, an `a.tel` sender, and a `q` body. | Historical parser versions reported null-node errors and group-message problems. Gate 3 parsed the message rows in this archive, retaining malformed rows as partial messages with issues. [S3] |
| Call/event time | `abbr.published` with a `title` attribute. | The parser author explicitly recorded a timestamp-format change in 2015. [S3] |
| Call contact/number | `a.tel`, with visible contact text and a `tel:` link. | Contact names and number availability vary; retain the original string and do not normalize destructively. [S3][S5] |
| Call duration | `abbr.duration` in some call records. | Reported absent for missed calls and some recorded events. [S3] |
| Voicemail transcript/audio | `span.full-text` and an `audio` element with a `src` reference. | The audio element/file may be absent. Transcript availability and the link target are not guaranteed. [S3] |
| Group text conversations | A conversation may include several participants and messages from different senders. | A historical parser assumed a single `a.tel` sender in some paths; later users reported group messages triggered null errors. Do not make a one-contact-per-file assumption. [S3][S4] |
| MMS/message media | Message output may include image references; reported placeholder text includes `MMS Sent`/`MMS Received`. | A maintained fork reports that matching referenced filenames to exported files required handling truncation and duplicate suffixes. Never pair media by approximate name alone. [S4][S5] |
| Video-like message references | This archive has 35 message rows with a `video` class marker and no `<video>` element; each row has an `a.video` link with an `href`. | All 35 distinct href targets matched exactly one `.3gp` or `.mp4` file by extensionless stem (31 and 4 respectively). Gate 3 preserves the raw href but leaves media type and matched path unknown in the parsed record. |

The historical parser's sent/received inference from the visible name `Me` is not reliable enough to copy as a rule. Direction must be recorded as unknown unless the source evidence supports it for the specific format/version. [S3]

In this archive, every one of the 18,732 `div.message` rows has a recognized `abbr.dt` timestamp, an `a.tel` sender element, and a `q` body. Sixty-one sender elements have an empty `tel:` target; Gate 3 preserves the display name, leaves the phone number unknown, and reports a row-scoped issue for each. All observed message and call/event timestamp values use an ISO-like numeric-offset form. There are 798 message rows with an `img` element, 35 with a `video` class marker (none with a `<video>` element), two with an `audio` element, and 666 exact `MMS Sent`/`MMS Received` placeholder bodies. Each video-class row has a concrete `a.video[href]` reference that maps by extensionless stem to one `.3gp` or `.mp4` archive file; the parser preserves the reference without assigning a media type or resolved path. These counts describe this export only.

## Gate 0.5 real-export validation

- The archive contains 8,946 regular files (196,388,878 compressed bytes; 205,940,118 uncompressed bytes). All entries were streamed to EOF with no ZIP/CRC read errors; no extraction or writes to the source were performed.
- The directory inventory is `Voice/Calls/` (8,655 direct files), `Voice/Spam/` (290 files), and `Voice/Phones.vcf` (one file). The archive has no HTML outside those Voice folders.
- HTML counts: 2,443 message/conversation pages, 4,837 call-event pages, and 344 voicemail pages. No HTML page was left unclassified by the structural audit.
- HTML structures: 33 page-level structural signatures and 12 message-row signatures. The signatures intentionally omit text and attribute values; they are not inferred software-version identifiers. The full tables are in [real-export-observations.md](real-export-observations.md).
- Message rows: 18,732 total; 18,524 under `Calls/` and 208 under `Spam/`. Call-event labels: 3,030 `Placed`, 1,100 `Received`, and 707 `Missed`. Voicemail labels: 322 under `Calls/` and 22 under `Spam/`.
- Media: 1,319 files with image/audio/video-container extensions, plus three VCF files. No duplicate full paths or duplicate basenames were found. The extensions present were `.html`, `.jpg`, `.mp3`, `.gif`, `.3gp`, `.mp4`, `.vcf`, and `.amr`; no other file extension was present.
- Edge cases include nine group-named HTML files without a recognized event label, 100 voicemail pages without a `full-text` class, one voicemail page without an audio element, two `recording-error-message` markers, 38 unresolved local media references, and filenames with empty or unusual contact-label components. The message parser separately found 61 rows whose `tel:` link target is empty.
- Gate 0.5 itself was an inventory-only scan. Gate 3 subsequently implemented a message-page parser and validated it against the selected archive; this does not establish behavior for other Takeout versions, locales, account types, or split archives.

## Gate 3 message parser validation

- The DOM-based parser recognized 2,443 message pages containing 18,732 `div.message` rows. It reported the other 5,181 HTML pages as unsupported by this message-specific parser; those pages are call/event and voicemail records, not failed message rows. The full pass emitted 5,242 issues: one unsupported-page issue per non-message page, plus 61 row-scoped sender-phone issues.
- There were no parser exceptions. Sixty-one rows had empty `tel:` targets; these were retained with display-name evidence, null phone numbers, and row-scoped `message_sender_phone_missing` issues. The other 18,671 rows had a non-empty sender phone value. The parser retained 973 raw attachment references, including 35 video-anchor references whose media type remains unknown.
- The parser retains raw and parsed offset timestamps, decoded message body text (including line breaks), sender evidence, source path and row index, and raw media references. It does not infer message direction from `Me`, assign page-level participant links to each row, or resolve attachment paths.
- The 35 `a.video[href]` values are distinct bare-relative references. Each maps unambiguously by extensionless stem to one archive media file: 31 `.3gp` and four `.mp4`. The parser now preserves these `href` values as attachment references with an unknown media type; the class marker alone does not set the type.
- The source archive SHA-256 remained `DD7E801C1AE833FE30B872501F1B0B28EA0240A4402C886CA8600A449A91509C` after validation; it was not extracted or modified.

## Gate 4 conversation reconstruction validation

- Reconstruction produced one conversation per supported source message page: 2,443 conversations containing all 18,732 rows. Source-relative paths remain the identity boundary; pages are not merged solely because labels or participants look alike.
- The archive yielded 2,427 one-to-one conversations, 9 confirmed group conversations, and 7 conversations whose membership remains unknown. The 9 group pages each have at least two distinct phone identities in explicit page-member evidence. The 7 unknown cases are one-row text pages with empty sender `tel:` targets.
- Phone normalization removes formatting punctuation, retains a leading `+`, and does not apply country-code assumptions. Raw participant values and their evidence source remain available beside normalized identities. Message direction stays unknown.
- Messages are sorted by parsed timestamp, then source row order; rows with no parsed timestamp sort last. Identical bodies are retained. No exact duplicate source-page content groups were found; all pages remain present even when duplicate content is detected.
- Of 973 message attachment references, 945 matched by exact relative path, exact basename, or unique extensionless stem. Twenty-eight remain unresolved and generate issues. Unambiguous video links can receive a matched path and media type from the source file extension; the `video` class itself does not establish either value.
- The full pass produced 5,270 issues: 5,181 unsupported non-message HTML pages, 61 empty sender phone targets, and 28 unresolved message attachment references. Unsupported call/event and voicemail parsing remains outside Gate 4. No exceptions occurred and the source archive hash was unchanged.

## What remains unknown after this sample

- Whether other exports use different folder names, casing, nesting, or split-archive layouts.
- Whether older/newer exports, locales, or account types use additional HTML templates or timestamp encodings. This archive showed 33 structural signatures but only one timestamp-value format; that does not prove there are no other versions. [S3]
- How `Me` maps to the account's Voice number, and when names or numbers are omitted from participant links.
- Whether every group-named file represents a distinct group conversation, and how membership changes or conversation renames should be represented.
- The targets of Gate 0.5's 38 unmatched unique image/audio references and Gate 4's 28 unresolved message references; these may be absent, transformed, or use another linking rule and should not be called missing without stronger evidence.
- The meaning of the two `recording-error-message` records, the contents/semantics of VCFs, and the association between each voicemail HTML page and each MP3 file.
- Whether greetings, billing history, service address, or other official Voice export categories appear in other archives. They were not present as recognized files here. [S1]
- Whether any JSON, CSV, or other structured formats accompany different exports.

## Initial parsing and inventory strategy

1. Accept an archive or extracted directory and enumerate it without modifying the source. For ZIP input, inspect entries in place or extract only into isolated temporary storage; never rewrite the archive or source files.
2. Find Voice content by path and file inventory. This sample requires at least `Voice/Calls`, `Voice/Spam`, and `Voice/Phones.vcf` discovery; do not treat `Calls` as the only relevant folder. Keep all paths relative to the selected source and include unclassified files in the scan report.
3. Classify HTML with a real HTML parser and observed DOM/content signals. Do not structurally parse HTML with regex and do not classify solely from a filename token.
4. Treat filenames, links, timestamps, and displayed labels as raw evidence. Use explicit in-document values where their meaning has been confirmed for a supported format. Keep original text alongside parsed values.
5. Emit one message per source message row, preserving the source file and source-level conversation context. Group messages conservatively; leave sender, direction, timestamp, membership, and attachment links unknown when evidence is insufficient.
6. Resolve an attachment only when its in-document reference maps to a concrete source file under a documented matching rule. Record an issue for missing or ambiguous references rather than guessing.
7. Inventory call, voicemail, greeting, and other records independently from text messages. Missing optional audio/media should produce an issue or warning, not abort the import.
8. Record unsupported structures and malformed records with source paths and reasons so later samples can become regression fixtures.
9. Validate every supported parser path against sanitized real exports before describing that path as real-export support. Until then, label behavior as based on documented/public or synthetic fixtures only.

## Proposed normalized data model

These are starting concepts, not fixed schemas. Keep raw source values and provenance so later format evidence can refine the model without losing the original meaning.

| Model | Proposed responsibility | Provenance and unknowns to preserve |
|---|---|---|
| `SourceArchive` | Identity and metadata for the selected ZIP or extracted source. | Original path/name, source kind, optional content hash, scan/import run. Never mutate source. |
| `SourceFile` | One archive entry or extracted file. | Relative path, raw filename, size, extension/content type when known, optional hash, classification, parse status. |
| `ImportRun` | One scan/import attempt. | Start/end, source identity, counts, status, warnings/errors, unsupported structures. |
| `Conversation` | A conservatively reconstructed message grouping. | Source file links, raw display label, grouping basis/confidence; do not merge solely by similar names or numbers. |
| `Participant` | A participant as evidenced by a record. | Raw display name and raw number, optional separately stored normalized form, source references. Missing identity remains missing. |
| `Message` | One message item from an observed message row. | Raw and parsed timestamp, body, sender evidence, nullable/unknown direction, conversation/source identity, raw source details. |
| `Attachment` | A media or other file referenced by a record. | Original reference string, matched `SourceFile` if unambiguous, media type, relationship basis/confidence. |
| `CallRecord` | A call event and its source evidence. | Raw event label, timestamp, participants/numbers, duration, nullable direction, source file. |
| `Voicemail` | A voicemail event and optional transcript/audio references. | Raw transcript, optional audio `SourceFile`, timestamp/duration/sender evidence, source file. |
| `ImportIssue` | A malformed, unsupported, missing, or ambiguous item. | Issue code/severity, source file/path, safe diagnostic detail, and any uncertain raw values needed for review. |

Do not require every conversation, message, call, or voicemail to have normalized phone numbers, a known direction, or a matched attachment. Keep source identity available at each level so every derived record can be traced back.

## Gate 7 real-export validation

The Gate 7 importer and exporters were validated against the full archive described above. It classified every one of the 7,624 HTML pages: 2,443 message/conversation pages, 4,837 call events, and 344 voicemail events. It retained all 18,732 message rows, skipped zero records, and completed without parser exceptions.

- Call and voicemail event labels were read from the prefix of the observed `.haudio .fn` content. The preserved call labels are `Placed`, `Received`, and `Missed`; `Voicemail` remains its own record type. No sent/received direction or call outcome is inferred from these labels.
- Event timestamps are retained raw from `abbr.published[title]` and parsed only when the value has an explicit `Z` or numeric UTC offset. A value without an explicit zone remains unparsed and receives an invalid-timestamp issue, rather than inheriting the machine's local timezone. Contact labels and phone targets from `a.tel` are retained independently. When the link has no visible contact text, the parser uses the filename label only if the filename matches the observed contact / event / UTC-timestamp form for that exact event label. The raw filename label and its evidence source remain in the database and exports; the label is not normalized or treated as identity.
- `abbr.duration` title and display text are preserved separately. A duration is parsed only when its value has the unambiguous `hours:minutes:seconds` shape. This archive yielded parsed durations for 4,130 calls and 344 voicemails; all 707 missed-call pages lacked a duration node.
- Voicemail transcript text is preserved when present; 244 of 344 pages had `span.full-text`, while 100 had no transcript span. Missing transcripts remain null without an invented value. Of 344 voicemail records, 343 had a direct audio reference: 321 matched an archive file and 22 remained unresolved. One voicemail had no audio reference and was retained with an issue. Both observed call audio references matched; the two call pages with a recording-error marker were retained with issues.
- Message rows retain their 973 media references, of which 945 matched and 28 remained unresolved. Across messages, calls, and voicemails, the importer retained 1,318 media references: 1,268 matched and 50 unresolved. Missing or unresolved media does not stop import.
- The completed import reported 190 warnings, zero errors, and zero unsupported HTML pages. Warnings cover 38 records with no usable contact label, 38 with no phone target, 61 message rows with empty sender targets, two recording-error markers, one voicemail without audio, and 50 unresolved media references. Each source record remains available.
- The HTML export included 2,443 conversation pages, calls, voicemails, transcripts, and matched local media; it copied 1,268 media files with zero unavailable. The relational CSV export included separate call, voicemail, and media-reference tables. No external HTTP(S) resource references were emitted.
- The 196,388,878-byte Takeout retained SHA-256 `DD7E801C1AE833FE30B872501F1B0B28EA0240A4402C886CA8600A449A91509C` before and after full import and export validation. No source extraction or modification occurred.

## Sources

- **[S1]** Google Voice Help, [Export your data from Voice](https://support.google.com/voice/answer/10130510?hl=en-US). Official list of exportable data categories; no file-level schema.
- **[S2]** Google Account Help, [How to download your Google data](https://support.google.com/accounts/answer/3024190?hl=en). General Takeout archive guidance, including `archive_browser.html`.
- **[S3]** Steve Whitcher / NeighborGeek, [parser description](https://www.neighborgeek.net/2014/12/import-gvhistory-export-google-voice_16.html) and [parser source](https://gist.github.com/NeighborGeek/84d578de2bc5538bd8d1). Historical selectors and a reported timestamp change; later comments expose format/group-message fragility.
- **[S4]** Paul Sanford, [google-voice-takeout-parser](https://github.com/psanford/google-voice-takeout-parser). Public parser README with text, group-chat, missed-call, and voicemail output examples. The examples are parser output, not a Google schema.
- **[S5]** SLAB-8002, [gvoice-sms-takeout-xml](https://github.com/SLAB-8002/gvoice-sms-takeout-xml). Maintainer reports real-archive testing and attachment filename/contact/group edge cases; this is one user's export experience.
- **[S6]** Ethan River Page, [Memoria Google Export guide](https://github-wiki-see.page/m/ethanriverpage/memoria/wiki/Google-Export). Secondary path and filename examples, with explicit warning that HTML can vary.
- **[S7]** Google Voice Help Community, [timestamp/export discussion](https://support.google.com/voice/thread/144198592/on-text-messages-why-does-the-time-stamp-disappear-about-30-days-after-sent-received?hl=en). A community product expert describes historical HTML/MP3/VCF forms and `archive_browser.html`; community content is not an official format specification.
