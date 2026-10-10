# Voice Bridge

<img src="../root/res/takeout_stack.png" alt="Voice Bridge Takeout archive icon" width="220">

**Your Google Voice history, kept local.** Open a Google Voice Takeout to browse messages, calls, voicemails, media, and import issues on your PC. Build a local database, report, and offline HTML archive when you are ready to export.

## Get Voice Bridge

[Get Voice Bridge from the Microsoft Store](https://apps.microsoft.com/detail/9N6KNRV562N8?hl=en-us&gl=US).
The Microsoft Store is the supported customer distribution channel.

Installing or removing the app does not change your original Takeout or delete files you created in an export destination. Manage those files separately.

## Windows requirements

- 64-bit Windows 10, version 2004 (build 19041) or later, or Windows 11.
- Enough free disk space for the application, the local database and report, and the generated HTML archive. Required space depends on the size and media content of the Takeout.
- A Google Voice Takeout ZIP file, or a folder extracted from that ZIP.

## Open and browse a Takeout

1. Select the original Takeout ZIP or an extracted Takeout folder. Voice Bridge reads the source without modifying it.
2. Scan the archive. Scanning does not require an output destination; review the discovered content and any scan warnings.
3. Use the sidebar to browse Messages, Calls, Voicemails, Media, and Issues. Select a record to inspect its available details and source information.
4. When you are ready to create files, open **Export**, choose a destination outside the extracted source, and select **Build local archive**. The destination must not already contain Voice Bridge's `voicebridge.db`, `import-report.json`, or `archive` output.
5. Optionally create a relational CSV export from the Export page. CSV export records media paths and unresolved relationships but does not copy source media.

The local archive destination contains:

- `voicebridge.db` — reconstructed records and their source evidence.
- `import-report.json` — counts, warnings, and issue details.
- `archive\` — a self-contained HTML archive for local browsing.
- `csv-export\` — relational CSV tables and an export report, if you create the optional CSV export.
- `voicebridge-crash-*.txt` — only if an unhandled application failure is logged after a safe output folder has been selected.

Existing output files are not overwritten. If an operation is canceled, inspect the destination because completed files may remain there.

## Screenshots

These screenshots use a synthetic sample Takeout and contain no personal archive data.

![Voice Bridge with no archive loaded](../root/res/screenshots/Screenshot%202026-10-06%20072425.png)

![Browsing sample media and its details](../root/res/screenshots/Screenshot%202026-10-06%20075921.png)

![Completed local archive and optional CSV export](../root/res/screenshots/Screenshot%202026-10-06%20075846.png)

## Privacy

VoiceBridge processes the selected files locally. The application does not use accounts, cloud processing, telemetry, or automatic uploads. It reads the selected Takeout without changing the source. See [the privacy statement](privacy.md) and the in-app **Privacy** button for details about local crash logs.

## Help

See [troubleshooting and support](support.md). Do not send your Takeout or generated database with a report unless you have reviewed it and intentionally chosen to share it; these files can contain private messages, phone numbers, and attachments.
