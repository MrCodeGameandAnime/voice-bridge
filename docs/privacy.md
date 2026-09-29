# VoiceBridge privacy statement

VoiceBridge processes the Google Voice Takeout source selected by the user on that user's Windows PC. The application does not require an account and does not use cloud processing, telemetry, analytics, or automatic uploads.

The Takeout ZIP or extracted source is read without being changed. The local database, import report, and offline HTML archive are written to the destination folder selected by the user. The application does not upload these outputs.

## Local crash logs

After a source and a safe output folder have both been selected, an unhandled application failure may cause VoiceBridge to write a `voicebridge-crash-*.txt` file into that existing output folder. The file can include exception details and local file paths. It is not sent automatically. VoiceBridge does not create the destination folder just to write a crash log, and it does not write a crash log inside an extracted Takeout source.

Review a crash log before choosing to share it. It may reveal local folder names or other details about the files on your PC. Removing the VoiceBridge application folder does not remove a crash log from the selected output folder.

## User control

The user chooses the input source and output location, can cancel an import, and controls whether to keep or remove the generated files. The application has no automatic update or remote support service.
