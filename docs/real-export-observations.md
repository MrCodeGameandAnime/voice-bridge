# Real Google Voice Takeout observations

**Gates:** 0.5 — real export validation; 3 — message parser validation
**Result:** Inventory and structural audit completed for the one ignored Takeout archive in this repository; the Gate 3 message parser was also validated against its message pages.
**Boundary:** The archive was streamed in place and not extracted or changed. Gate 0.5 was documentation and read-only inspection; parser behavior and its limits are recorded separately below.

## Archive scale

| Measure | Observed count |
|---|---:|
| Regular files | 8,946 |
| Compressed / uncompressed archive bytes | 196,388,878 / 205,940,118 |
| HTML files | 7,624 |
| Image media (`.jpg`, `.gif`) | 936 (885 JPG, 51 GIF) |
| Audio-extension media (`.mp3`, `.amr`) | 348 (346 MP3, 2 AMR) |
| Video/container media (`.3gp`, `.mp4`) | 35 (31 3GP, 4 MP4) |
| Total media files | 1,319 |
| VCF files | 3 |
| Voicemail HTML records | 344 |
| Call/event HTML records | 4,837 |
| Message/conversation HTML records | 2,443 |
| Message rows inside those pages | 18,732 |

All 8,946 archive entries were read through to EOF; no ZIP read or CRC errors occurred. Extension inventory is exhaustive for this archive: `.html` 7,624; `.jpg` 885; `.mp3` 346; `.gif` 51; `.3gp` 31; `.mp4` 4; `.vcf` 3; `.amr` 2. No other file extensions were present.

| Archive location | HTML | JPG | MP3 | GIF | 3GP | MP4 | AMR | VCF | Total |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `Voice/Calls/` (all direct children) | 7,380 | 885 | 324 | 27 | 31 | 4 | 2 | 2 | 8,655 |
| `Voice/Spam/` | 244 | 0 | 0 | 24 | 0 | 0 | 0 | 0 | 290 |
| `Voice/Phones.vcf` | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 1 |
| **Total** | **7,624** | **885** | **324** | **51** | **31** | **4** | **2** | **3** | **8,946** |

Every HTML page is under `Voice/Calls/` or `Voice/Spam/`; there are no HTML pages elsewhere in the archive. `Calls/` has no nested content. There are 7,380 HTML pages in `Calls/` and 244 in `Spam/`.

## Audit method and classification

The ZIP was enumerated and streamed in place. HTML was inspected structurally using a tolerant standard-library HTML parser; the scan retained aggregate counts and structural features, not message text, contact values, phone numbers, or media payloads. Page-shape fingerprints describe observed tag/class/attribute-name structure with content and attribute values excluded. Their short IDs are identifiers for this catalogue, not Google version numbers.

Page family counts were classified using filename labels together with structural content signals, not filename tokens alone. Message pages contain `div.message` rows; call/event and voicemail pages carry event metadata such as `abbr.published`, with voicemail also identified by its event label and audio/transcript structure. Nine group-named pages have no recognized event-label token, but their structure places them with message/conversation pages. Every HTML page received one of the three family classifications below.

| Family | Calls | Spam | Total | Basis and subtypes |
|---|---:|---:|---:|---|
| Message/conversation pages | 2,235 | 208 | 2,443 | 2,434 filenames labelled `Text`; 9 `Group Conversation` pages without a recognized label |
| Call/event pages | 4,823 | 14 | 4,837 | 3,030 `Placed`, 1,100 `Received`, 707 `Missed` |
| Voicemail pages | 322 | 22 | 344 | `Voicemail` label |
| **Total HTML** | **7,380** | **244** | **7,624** | No unclassified pages |

The recognized-label naming pattern on 7,615 HTML files is `<label> - <kind> - YYYY-MM-DDTHH_mm_ssZ.html`; the other nine have the `Group Conversation - <timestamp>` form. Observed labels were `Text`, `Placed`, `Received`, `Missed`, and `Voicemail`. No `Recorded` label was found. All HTML pages had an `html` root, and the scan found no UTF-8 replacement characters. The tolerant parser does not prove strict HTML conformance, so this is not a claim that every source page is valid HTML.

## Observed HTML structures

### Page-level structural shapes

There are 33 distinct page-level signatures. Feature names in the table summarize the audited structures: `message` means `div.message` rows; `q` is the message body element; `dt` and `published` refer to timestamp-bearing `abbr` elements; `duration` is the call duration `abbr`; `full-text` is the transcript span; `audio` is an audio element; `img` is an image element; `video-class` is a class marker (not a `<video>` element); `participants` is a participant container; and `recording-error` is the recording error marker. A signature can occur across page families: the 102-page audio/published/duration signature below consists of 100 voicemail pages and 2 `Received` event pages. Message signatures also occur across ordinary and group-named conversations.

| Shape ID | Pages | Observed feature summary |
|---|---:|---|
| `CE19D6645E` | 4,126 | Call/event; published timestamp and duration |
| `0E4C9CC763` | 918 | Message rows, body, and message timestamp |
| `B842FE780D` | 707 | Call/event; published timestamp, no duration |
| `20DA55FECE` | 432 | Message rows, body, and message timestamp |
| `D2DEA61976` | 428 | Message rows, body, and message timestamp |
| `D61B869634` | 243 | Voicemail; transcript, audio, published timestamp, duration |
| `9FF9AFA251` | 155 | Message rows, body, and message timestamp |
| `88BFC5AB2D` | 136 | Message rows with image, body, and message timestamp |
| `0FF941CC34` | 111 | Message rows with image, body, and message timestamp |
| `562C23B0E5` | 102 | Audio, published timestamp, duration; spans 100 voicemail and 2 `Received` event pages (the voicemail pages have no transcript span) |
| `6C91FF0ED2` | 100 | Message rows, body, and message timestamp |
| `598196A4EC` | 43 | Message rows with image, body, and message timestamp |
| `73CA1207F2` | 37 | Message rows with image, body, and message timestamp |
| `841D04575E` | 36 | Message rows with image, body, and message timestamp |
| `66AA123ED5` | 10 | Message rows with image and video class marker, body, and timestamp |
| `A9599E74C3` | 10 | Message rows, body, and message timestamp |
| `DC09B4387A` | 4 | Message rows with participant container, body, and timestamp |
| `2243888E39` | 3 | Message rows with image and video class marker, body, and timestamp |
| `9B8B0C5B97` | 3 | Message rows with video class marker, body, and timestamp |
| `F9CA4AD301` | 2 | Message rows with video class marker, body, and timestamp |
| `3060346DDD` | 2 | Message rows with image and video class marker, body, and timestamp |
| `62CCC14DDE` | 2 | Call/event; recording-error marker, published timestamp, duration |
| `B18D463315` | 2 | Message rows with video class marker, body, and timestamp |
| `4C1ED6B458` | 2 | Message rows with participant container and image, body, and timestamp |
| `B794423A7D` | 2 | Message rows with participant container, body, and timestamp |
| `53CED456FC` | 1 | Message rows with video class marker, body, and timestamp |
| `DCF76A091B` | 1 | Message rows with image, body, and timestamp |
| `76B7B065D0` | 1 | Message rows with audio and image, body, and timestamp |
| `894F8B27F3` | 1 | Message rows with participant container, body, and timestamp |
| `B0289F93F6` | 1 | Message rows, body, and message timestamp |
| `E04839EF42` | 1 | Message rows, body, and message timestamp |
| `7C98F2CD12` | 1 | Message rows with audio and image, body, and timestamp |
| `99E22D44E5` | 1 | Voicemail; transcript, published timestamp, duration; no audio element |
| **Total** | **7,624** | **33 signatures** |

### Message row shapes and fields

The 18,732 `div.message` rows all contained a recognized `abbr.dt` timestamp, an `a.tel` sender element, and a `q` body. All row timestamps used an ISO-like representation with a numeric offset. Sixty-one sender elements had an empty `tel:` target; the Gate 3 parser retained the rows and reported those missing phone values. Counts below are row counts, and the differences reflect observed tag/attribute/class structure rather than a semantic message type.

| Shape ID | Rows | Additional observed structure beyond the common row |
|---|---:|---|
| `CF43DF01D6` | 8,705 | Base shape: `a`, `abbr`, `cite`, `div`, `q` |
| `5297599D6E` | 8,245 | Base shape plus `span` |
| `7D7EC56CFE` | 824 | Base shape plus `br` and `span` |
| `3CE1343152` | 461 | Base shape plus `img` |
| `6F31F131D4` | 260 | Base shape plus `img` and `span` |
| `6B1A271AEA` | 123 | Base shape plus `br` |
| `8E758758A2` | 75 | Base shape plus `br`, `img`, and `span` |
| `C2830412C8` | 32 | Video class marker and `span`; no `<video>` element |
| `B4FF27524C` | 2 | `img` and video class marker |
| `F384BAC0CF` | 2 | `<audio>` element |
| `6B0CABE5F4` | 2 | Same visible tag/class inventory as span variant, but a different attribute-name set |
| `0ACFC2E2EE` | 1 | `br`, video class marker, and `span` |
| **Total** | **18,732** | **12 signatures** |

### Message, group, and MMS variants

- **Message pages:** 2,434 `Text`-labelled HTML files contain 18,732 total rows; 18,524 rows are in `Calls/`, and 208 in `Spam/`. Every audited row had timestamp, sender link, and body nodes. There are 12 row shapes, including line breaks, spans, images, audio elements, and video class markers.
- **Group candidates:** Nine HTML filenames say `Group Conversation` and have no recognized event label. Nine pages across four page shapes have a `participants` class. Separately, 41 `Calls/` pages have at least three distinct `tel:` links: 39 have three and two have four or more. These are overlapping structural clues, not a confirmed group-conversation count or membership model.
- **MMS/image:** 798 message rows contain an `img` element. 666 rows have an exact `MMS Sent` or `MMS Received` body. These signals overlap, but the scan did not establish that all such rows are equivalent MMS records or that every attachment reference resolves.
- **Video-like/audio:** 35 rows have a `video` class marker; two rows also have image markup, and there are three image elements among those rows. No message row contains a `<video>` element. Two rows contain `<audio>`. A class marker alone is not proof of an embedded or playable video.

### Call/event variants

There are 4,837 event pages: 3,030 `Placed`, 1,100 `Received`, and 707 `Missed`. All had `abbr.published` with a `title` timestamp. `Placed` and `Received` pages had `abbr.duration`; all 707 `Missed` pages lacked it. Two pages had `recording-error-message` structure. Duration presence, a file label, or a displayed contact does not by itself establish normalized direction or call outcome semantics beyond the observed event label.

### Voicemail variants

There are 344 `Voicemail` HTML pages: 322 in `Calls/` and 22 in `Spam/`. All had a published timestamp; all but one had an audio element. A `span.full-text` transcript was present on 244 pages and absent on 100. All 22 voicemail pages in `Spam/` lacked the transcript span. Two event pages have a `recording-error-message` marker. The archive has 346 MP3 files, so the page and media counts do not establish a one-to-one association.

## Observed edge cases and outliers

### Media references and absent files

The scan found 1,283 unique local image/audio references after deduplicating by source page and raw reference. Using exact relative-path matching or an extensionless-stem match where applicable:

| Reference type | Matched | Unmatched | Notes |
|---|---:|---:|---|
| Image | 920 | 16 | All 920 matches used extensionless-stem matching: 896 from `Calls/`, 24 from `Spam/` |
| Audio | 325 | 22 | 301 exact relative-path matches and 2 extensionless-stem matches in `Calls/`; 22 exact relative-path matches in `Spam/` |
| **Total** | **1,245** | **38** | The 38 unmatched references are all in `Calls/`: 16 image, 22 audio |

The unmatched 38 are unresolved by these rules; they may reflect absent media, transformed names, or another link convention. The archive contains no duplicate basenames to explain them. Do not label them definitively missing until the source reference semantics are understood.

### Filenames and contact labels

The archive has zero duplicate full paths under case-insensitive comparison and zero duplicate basenames. Aggregate filename anomalies include 38 empty contact-label components, 19 non-ASCII names, 23 comma-containing labels, 110 labels with unusual punctuation, and 5,855 phone-like labels. These are morphology counts only; the report intentionally omits actual names and numbers. Filename labels should remain raw input evidence and should not be treated as normalized identities.

### Format changes, malformed records, and unknown types

- **Old/new changes:** This single archive contains 33 page signatures and 12 row signatures, but no version metadata was established and no timeline/version comparison can be made. All message rows had the expected timestamp/sender/body nodes; call/voicemail observations are in the tables above. Timestamp values observed here had an ISO-like numeric-offset form in HTML and a UTC `Z` filename form with colons represented by underscores. Other export versions remain unexamined.
- **Malformed records:** No ZIP read/CRC failure, HTML page without an `html` root, UTF-8 replacement character, or message row missing one of the audited timestamp, `tel:` link, or body elements was detected. The standard-library HTML parser is tolerant, so strict syntax conformance was not checked. Missing transcript/audio nodes and recording-error markers are observed optional-field anomalies, not automatically malformed records.
- **Unknown file types:** No unknown extensions occurred. The complete extension set is `.html`, `.jpg`, `.mp3`, `.gif`, `.3gp`, `.mp4`, `.vcf`, and `.amr`. VCF file contents were not opened or classified beyond extension and path.
- **Unusual structures:** Nine group-named message pages lack a recognized filename event token; 100 voicemail pages lack transcript spans; one voicemail page lacks audio; 35 message rows have a video class but no video element; two audio message rows and two recording-error markers warrant future fixture-level review.

### Gate 3 message parser validation

- The DOM-based parser recognized all 2,443 message pages by their `div.message` rows and retained all 18,732 rows. The other 5,181 HTML pages were reported as unsupported by this message-specific parser; they are call/event and voicemail pages. The full pass emitted 5,242 issues: one unsupported-page issue per non-message page and 61 row-scoped sender-phone issues.
- The parser completed with no exceptions. It emitted 61 `message_sender_phone_missing` issues for empty `tel:` targets and preserved each row's sender display name while leaving its phone number unknown. The remaining 18,671 sender links had non-empty phone values. It retained 973 raw attachment references, including 35 video-anchor references with no inferred media type.
- Parsed messages retain source-relative path and row index, raw timestamp and parsed offset timestamp when valid, body text, row sender evidence, and raw attachment references. Direction remains unknown; page-level participant links are kept as separate evidence and are not assigned to each row. Attachment references are not resolved to archive files at this gate.
- Inspection of the 35 video-class rows found one `a.video[href]` per row. The 35 distinct bare-relative targets each matched exactly one archive media file by extensionless stem: 31 `.3gp` and four `.mp4`. The parser now preserves the raw href as an attachment reference while leaving media type and matched path unknown; the class name alone does not determine either value.
- The source archive SHA-256 remained `DD7E801C1AE833FE30B872501F1B0B28EA0240A4402C886CA8600A449A91509C` after validation. No extraction or source modification occurred.

## Gate 4 conversation reconstruction validation

The Gate 4 pass reconstructed every supported message page in the ignored Takeout archive, using one source HTML path as one conversation identity. It did not merge separate pages based on similar labels or phone numbers.

| Result | Count | Handling |
|---|---:|---|
| Message pages / conversations | 2,443 | Each conversation retains its source-relative path and raw filename label |
| Message rows retained | 18,732 | No rows removed during sorting or duplicate detection |
| One-to-one conversations | 2,427 | Exact `Text` filename timestamp shape, no explicit page-member links, and one or two normalized sender phone identities |
| Confirmed group conversations | 9 | Each had a group-style filename and at least two distinct phone identities in explicit page-member evidence |
| Unknown conversations | 7 | Each was a one-row text page with an empty sender `tel:` target; membership and sender phone remain unknown |
| Exact duplicate source-page groups | 0 | Every source page remains represented; detection hashes the decoded HTML content |
| Attachment references matched | 945 / 973 | Exact relative path, exact basename, or a unique extensionless stem |
| Attachment references unresolved | 28 / 973 | Kept with their raw reference and a row-scoped issue; no match was guessed |

The group classifier also accepts three or more distinct normalized phone identities when they are evidenced by explicit page members or message-row senders. A group-style filename without sufficient membership evidence remains `Unknown` and gets a `conversation_membership_ambiguous` issue. The nine observed group pages met the explicit page-member threshold.

Phone normalization removes observed presentation punctuation and whitespace, retains a leading `+`, and does not add or remove a country prefix. Unsupported forms remain unknown. Participant display names are normalized separately, while raw names and phone values remain in participant evidence. Page-level members and row-level senders are retained as distinct evidence sources.

Messages are ordered by parsed timestamp, with missing timestamps last and source row order as the tie-breaker. Identical bodies at different times remain separate messages. Direction remains unknown. Attachment types come from an explicit HTML media element or, when the parser has no type, from the uniquely matched source file extension; the `video` class alone is not used to assign a type.

The reconstruction pass emitted 5,270 issues: 5,181 unsupported-page reports for call/event and voicemail HTML outside this message-specific gate, 61 empty sender `tel:` reports, and 28 unresolved attachment references. It completed without parser or reconstruction exceptions. The source archive SHA-256 remained `DD7E801C1AE833FE30B872501F1B0B28EA0240A4402C886CA8600A449A91509C`; the archive was read in place and not modified.

## Gate 0 comparison

Gate 0 proposed a discovery-oriented inventory, per-file HTML/message structures, MMS and group variation, media references, calls and voicemail, timestamps, participants, and explicit treatment of uncertainty. The real archive validates several concrete examples but does not establish a universal Takeout contract.

### Confirmed assumptions

- This export has `Takeout/Voice/Calls/` with per-conversation or event HTML and adjacent media; all `Calls/` entries are direct children.
- Message HTML contains multiple `div.message` rows, not just one message per file. The 2,443 message/conversation pages contain 18,732 rows.
- The observed message rows expose timestamp, sender-link, and body nodes. Call/event timestamps use `abbr.published`; duration is optional and absent on missed-call pages.
- Media references are present and should be matched to archive files conservatively. Some resolve only by extensionless stem, and unresolved references exist. Gate 0.5's 38 unmatched unique references cover its broader image/audio audit; the Gate 4 message-only pass independently left 28 of 973 references unresolved.
- Group-like structures and filename/contact labels vary. Preserving source evidence and leaving uncertain participant, direction, and membership semantics unknown remains necessary.
- The inventory must be broad and retain unsupported/ambiguous cases. A `Calls/`-only or one-template assumption would lose observed Voice data.

### Disproven or narrowed assumptions

- **“All Voice records are under `Calls/`” is false for this archive.** It also contains a sibling `Spam/` folder with 290 files and a root-level `Voice/Phones.vcf`; two more VCF files are in `Calls/`.
- **“One stable HTML shape” is false for this archive.** The audit found 33 page signatures and 12 message-row signatures.
- **“Every message/conversation page has a recognized event label” is false.** Nine group-named pages have no recognized label token.
- **“Voicemail always includes transcript and audio” is false.** 100 pages lack transcript spans and one lacks an audio element.
- **“Every media reference maps directly to an archive filename” is false under exact and extensionless-stem matching.** Thirty-eight references remain unresolved.
- **No `Recorded` event label was observed**, despite historical parser examples describing recorded events. Absence in this archive does not establish that other versions lack it.
- Gate 0's examples of general Takeout categories (greetings, billing history, service address) are not represented as recognized files here. Their absence is specific to this archive and scan scope, not evidence they cannot be exported.

### New discoveries

- The archive is 8,946 files: 7,624 HTML, 1,319 media, and three VCF files, with no other extension.
- `Voice/Spam/` contains 244 HTML files (208 text pages, 14 call/event pages, 22 voicemail pages), 24 GIFs, and 22 MP3s.
- The nine unlabeled group-named pages, 41 multi-`tel:` pages in `Calls/`, and nine pages with a participants class are separate overlapping candidate indicators; none alone proves group semantics.
- MMS text and embedded media signals do not coincide one-to-one: 666 rows have the exact MMS placeholder text, 798 have an image, and 35 carry a video class marker. Some signals overlap.
- Media links use more than one successful matching pattern in this sample. The counts and unresolved set are recorded above for future evidence.
- The directory contains 2 VCF files under `Calls/` in addition to `Voice/Phones.vcf`; the VCF contents are unknown.

### Remaining unknowns

- Whether another export, language, account type, or date uses different folders, nesting, templates, encodings, labels, or file types.
- Whether and how the account's own number maps to visible `Me`, and how message direction, group membership, or conversation identity should be inferred.
- What the 38 unmatched media references point to, whether the two recording-error markers indicate missing recordings, and how voicemail HTML maps to individual MP3 files.
- The meaning/content of the three VCFs, the semantics of video class markers and the 666 MMS placeholders, and whether the nine group-named pages represent nine distinct groups.
- Whether other Voice export categories (greetings, billing history, service address) appear in other archives.
- Whether a strict HTML validator would find syntax defects. This audit used a tolerant parser and is not a conformance test.

## Gate 0.5 exit

This gate produced an archive inventory, shape catalogue, edge-case counts, and comparison with Gate 0 assumptions. The source archive remains untouched. The updated format overview is [google-voice-takeout-format.md](google-voice-takeout-format.md). Stop here: parser implementation and application changes are outside Gate 0.5.
