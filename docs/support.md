# Troubleshooting and support

## VoiceBridge cannot scan the source

- Confirm that the selected item is the original Google Takeout ZIP or the folder extracted from it.
- If Windows or another application moved the source, select it again.
- If the source is on removable or network storage, make sure it remains connected and readable while VoiceBridge works.
- Review the scan summary. Unknown files and warnings are retained in the summary rather than treated as confirmed Google Voice records.

## VoiceBridge cannot write the result

- Select a destination folder that you can write to and that has enough free space.
- For an extracted source, choose a destination outside that source folder.
- Choose a destination that does not already contain `voicebridge.db`, `import-report.json`, or an `archive` directory from an earlier VoiceBridge run. VoiceBridge stops when its output names are already present so it does not overwrite those files.
- If you canceled, the database or report may have completed before the cancellation. Inspect the output folder before retrying; use a different empty destination if VoiceBridge reports that the files already exist.

## Review a completed import

Use **View Issues** to inspect warnings and errors with their source file and row when available. An unresolved media reference means the message contains a reference that VoiceBridge could not map to a source media file. VoiceBridge keeps the issue visible; it does not claim that the attachment was recovered. Use **Open Archive** to browse the generated HTML, and **Open Output Folder** to inspect the database and JSON report.

## Local crash log

An unhandled application failure may leave a `voicebridge-crash-*.txt` file in the selected output folder. The log is local and is never sent automatically. Review it before sharing because it may include exception details and local file paths. If no output folder was selected, or the output would be inside an extracted Takeout source, VoiceBridge does not write a crash log.

## Sharing a problem report

Use the VoiceBridge support channel provided with the build you received. Include the VoiceBridge version shown in the window or **About**, Windows version, the step that failed, and relevant issue codes or a reviewed crash log. Do not include the Takeout ZIP, database, full HTML archive, message text, phone numbers, or media unless you have reviewed the contents and intentionally chosen to share them.
