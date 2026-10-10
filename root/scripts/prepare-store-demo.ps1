# Creates fictional Takeout input for screenshots through Voice Bridge's normal workflow.
# Every run has a new directory. Supplied media is copied; existing sources/exports are retained.
[CmdletBinding()]
param(
    [string]$DemoRoot = (Join-Path $PSScriptRoot '..\output\store-demo')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Speech
Add-Type -AssemblyName System.IO.Compression

$demoDirectory = [IO.Path]::GetFullPath($DemoRoot)
$mediaDirectory = Join-Path $demoDirectory 'media'
[IO.Directory]::CreateDirectory($mediaDirectory) | Out-Null
$imageExtensions = @('.jpg', '.jpeg', '.png', '.webp', '.gif')
$videoExtensions = @('.mp4', '.m4v', '.mov', '.webm', '.3gp')
$inputFiles = @(Get-ChildItem -LiteralPath $mediaDirectory -File)
$pictures = @($inputFiles | Where-Object { $_.Extension.ToLowerInvariant() -in $imageExtensions })
$videos = @($inputFiles | Where-Object { $_.Extension.ToLowerInvariant() -in $videoExtensions })
if ($pictures.Count -gt 1 -or $videos.Count -gt 1) {
    throw 'Place only one picture and one video in the media folder so selection is unambiguous.'
}

$runName = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $demoDirectory "runs\$runName"
$sourceDirectory = Join-Path $runDirectory 'source'
$callsDirectory = Join-Path $sourceDirectory 'Takeout\Voice\Calls'
[IO.Directory]::CreateDirectory($callsDirectory) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$anchor = [DateTimeOffset]::Parse('2026-10-02T17:00:00+00:00', [Globalization.CultureInfo]::InvariantCulture)
$inputMedia = [Collections.Generic.List[object]]::new()
$messageCount = 0

function Encode-Html([string]$Value) { [Net.WebUtility]::HtmlEncode($Value) }
function File-Time([DateTimeOffset]$Value) { $Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH_mm_ss'Z'") }
function Save-Html([string]$Name, [string]$Body) {
    $html = '<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Fictional Voice archive record</title></head><body>' + $Body + '</body></html>'
    [IO.File]::WriteAllText((Join-Path $callsDirectory $Name), $html, $utf8)
}
function Message-Row([string]$Name, [string]$Phone, [DateTimeOffset]$Time, [string]$Text, [string]$Media = '') {
    '<div class="message"><abbr class="dt" title="' + $Time.ToString('o') + '">' + $Time.ToString('MMM d, h:mm tt') + '</abbr><cite><a class="tel" href="tel:' + (Encode-Html $Phone) + '">' + (Encode-Html $Name) + '</a></cite><q>' + (Encode-Html $Text).Replace("`n", '<br>') + '</q>' + $Media + '</div>'
}

$contacts = @(
    @{ Name = 'Alex Morgan'; Phone = '+12025550101'; Topic = 'the weekend plans'; Reply = 'Saturday at ten works for me. Let us meet near the park entrance.'; Transcript = 'Hi, it is Alex. Just checking that we are still meeting at the park on Saturday at ten. I will bring coffee. See you there.' },
    @{ Name = 'Jamie Taylor'; Phone = '+12025550102'; Topic = 'the book club'; Reply = 'I finished the last chapter. Looking forward to hearing what everyone thought.'; Transcript = 'Hi, it is Jamie. The book club is meeting at six on Thursday. Bring your notes if you have them. Call me when you get a chance.' },
    @{ Name = 'Casey Rivera'; Phone = '+12025550103'; Topic = 'the dinner reservation'; Reply = 'The table is booked for six thirty. There is a patio if the weather stays nice.'; Transcript = 'Hey, it is Casey. Our dinner reservation is confirmed for six thirty. The restaurant has parking around the back. See you tonight.' },
    @{ Name = 'Morgan Lee'; Phone = '+12025550104'; Topic = 'the trail walk'; Reply = 'The shorter trail looks perfect. I will bring water and an extra jacket.'; Transcript = 'Hi, Morgan here. The trail is open again, and the weather looks good for Sunday. Give me a call so we can pick a meeting time.' },
    @{ Name = 'Riley Chen'; Phone = '+12025550105'; Topic = 'the library visit'; Reply = 'The library opens at nine. We can stop by the cafe afterward.'; Transcript = 'Hi, it is Riley. I found the book we talked about at the library. I can pick up a copy for you if you would like. Talk soon.' },
    @{ Name = 'Sam Reed'; Phone = '+12025550106'; Topic = 'the game night'; Reply = 'I will bring the board games. Four people should be a good group.'; Transcript = 'Hey, it is Sam. Game night is still on for Friday. I will bring two games and some snacks. Let me know if you need a ride.' },
    @{ Name = 'Community Garden'; Phone = '+12025550107'; Topic = 'the garden meetup'; Reply = 'We are planting herbs on Sunday morning. Gloves and spare tools are available.'; Transcript = 'Hello from the community garden. Our Sunday planting session starts at nine. We have spare gloves and tools. Everyone is welcome.' },
    @{ Name = 'Northside Bike Shop'; Phone = '+12025550108'; Topic = 'the bicycle tune-up'; Reply = 'Your bike is ready for pickup. We adjusted the brakes and checked both tires.'; Transcript = 'Hello from Northside Bike Shop. Your bicycle tune-up is finished, and your bike is ready for pickup. We are open until five today.' }
)

$pictureName = $null
$videoName = $null
foreach ($kind in @('picture', 'video')) {
    $candidates = @(if ($kind -eq 'picture') { $pictures } else { $videos })
    if ($candidates.Count -eq 0) { continue }
    $file = $candidates[0]
    $row = if ($kind -eq 'picture') { 2 } else { 4 }
    $name = 'Alex Morgan - Text - ' + (File-Time $anchor) + "-$row-1" + $file.Extension.ToLowerInvariant()
    $copiedPath = Join-Path $callsDirectory $name
    [IO.File]::Copy($file.FullName, $copiedPath, $false)
    $originalHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $copiedHash = (Get-FileHash -LiteralPath $copiedPath -Algorithm SHA256).Hash
    if ($copiedHash -ne $originalHash) { throw "Copied media hash differs: $($file.Name)" }
    $inputMedia.Add([pscustomobject]@{ Kind = $kind; FileName = $file.Name; ArchiveFileName = $name; Sha256 = $originalHash })
    if ($kind -eq 'picture') { $pictureName = $name } else { $videoName = $name }
}

for ($batch = 0; $batch -lt 3; $batch++) {
    for ($index = 0; $index -lt $contacts.Count; $index++) {
        $contact = $contacts[$index]
        $time = $anchor.AddDays(-9 * $batch).AddHours(-3 * $index)
        $texts = @(
            "Hi! Checking in about $($contact.Topic).",
            $contact.Reply,
            'That sounds good. Thank you for arranging it!',
            'Happy to help. I will send the details this afternoon.',
            'I have added it to my calendar.',
            "Great. We can catch up then.`nHave a good rest of your day!",
            'You too. See you soon!'
        )
        $rows = [Text.StringBuilder]::new()
        for ($row = 0; $row -lt $texts.Count; $row++) {
            $sender = if ($row % 2 -eq 0) { 'Me' } else { $contact.Name }
            $phone = if ($row % 2 -eq 0) { '+12025550100' } else { $contact.Phone }
            # One deliberate source anomaly gives the Issues page a truthful sample finding.
            if ($index -eq 2 -and $batch -eq 0 -and $row -eq 3) { $phone = '' }
            $media = ''
            if ($index -eq 0 -and $batch -eq 0 -and $row -eq 2 -and $null -ne $pictureName) {
                $texts[$row] = 'Here is the picture I mentioned.'
                $media = '<img src="' + (Encode-Html $pictureName) + '" alt="Shared picture">'
            }
            if ($index -eq 0 -and $batch -eq 0 -and $row -eq 4 -and $null -ne $videoName) {
                $texts[$row] = 'Here is the video I mentioned.'
                $media = '<a class="video" href="' + (Encode-Html $videoName) + '">Shared video</a>'
            }
            [void]$rows.Append((Message-Row $sender $phone ($time.AddMinutes(3 * $row)) $texts[$row] $media))
            $messageCount++
        }
        Save-Html ($contact.Name + ' - Text - ' + (File-Time $time) + '.html') $rows.ToString()
    }
}

$groupNames = @('Weekend crew', 'Book club', 'Community garden volunteers', 'Friday game night')
for ($groupIndex = 0; $groupIndex -lt $groupNames.Count; $groupIndex++) {
    $time = $anchor.AddHours(-2 - 4 * $groupIndex)
    $members = @($contacts[$groupIndex], $contacts[($groupIndex + 1) % $contacts.Count])
    $body = [Text.StringBuilder]::new('<div class="participants"><a class="tel" href="tel:+12025550100">Me</a>')
    foreach ($member in $members) { [void]$body.Append('<a class="tel" href="tel:' + $member.Phone + '">' + (Encode-Html $member.Name) + '</a>') }
    [void]$body.Append('</div>')
    $groupTexts = @('Is everyone still free this weekend?', 'Yes! What time were you thinking?', 'How about ten in the morning?', 'Ten works for me.', 'I can bring coffee and a few snacks.', 'Thanks! I will share the meeting spot later.', 'Looking forward to it.', 'See you both then!')
    for ($row = 0; $row -lt $groupTexts.Count; $row++) {
        $sender = if ($row % 3 -eq 0) { @{ Name = 'Me'; Phone = '+12025550100' } } else { $members[($row - 1) % 2] }
        [void]$body.Append((Message-Row $sender.Name $sender.Phone ($time.AddMinutes(4 * $row)) $groupTexts[$row]))
        $messageCount++
    }
    Save-Html ($groupNames[$groupIndex] + ' - Text - ' + (File-Time $time) + '.html') $body.ToString()
}

$historicalTime = $anchor.AddDays(-30)
$historicalBody = (Message-Row 'Alex Morgan' '+12025550101' $historicalTime 'Here is the older photo from our outing.' '<img src="archived-outing-photo.jpg" alt="Older shared photo">') + (Message-Row 'Me' '+12025550100' ($historicalTime.AddMinutes(5)) 'Thank you! I will keep the message with my notes.')
Save-Html ('Archived outing - Text - ' + (File-Time $historicalTime) + '.html') $historicalBody
$messageCount += 2

for ($index = 0; $index -lt 52; $index++) {
    $contact = $contacts[$index % $contacts.Count]
    $time = $anchor.AddHours(1).AddHours(-10 * $index)
    $eventName = @('Received', 'Placed', 'Missed')[$index % 3]
    $duration = [TimeSpan]::FromSeconds(75 + (($index * 41) % 820))
    $durationHtml = if ($eventName -eq 'Missed') { '' } else { '<abbr class="duration" title="' + $duration.ToString('hh\:mm\:ss') + '">(' + $duration.ToString('hh\:mm\:ss') + ')</abbr>' }
    $body = '<div class="haudio"><span class="fn">' + $eventName + ' call from ' + (Encode-Html $contact.Name) + '</span><a class="tel" href="tel:' + $contact.Phone + '">' + (Encode-Html $contact.Name) + '</a><abbr class="published" title="' + $time.ToString('o') + '">' + $time.ToString('MMM d, h:mm tt') + '</abbr>' + $durationHtml + '</div>'
    Save-Html ($contact.Name + " - $eventName - " + (File-Time $time) + '.html') $body
}

$synthesizer = [System.Speech.Synthesis.SpeechSynthesizer]::new()
try {
    $voiceNames = @($synthesizer.GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name })
    if ('Microsoft Zira Desktop' -in $voiceNames) { $synthesizer.SelectVoice('Microsoft Zira Desktop') }
    $audioFormat = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    for ($index = 0; $index -lt $contacts.Count; $index++) {
        $contact = $contacts[$index]
        $time = $anchor.AddHours(2).AddDays(-$index)
        $stem = $contact.Name + ' - Voicemail - ' + (File-Time $time)
        $audioPath = Join-Path $callsDirectory ($stem + '.wav')
        $synthesizer.SetOutputToWaveFile($audioPath, $audioFormat)
        $synthesizer.Speak($contact.Transcript)
        $synthesizer.SetOutputToNull()
        $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($audioPath))
        try {
            [void]$reader.ReadBytes(12)
            $audioBytes = 0
            while ($reader.BaseStream.Position + 8 -le $reader.BaseStream.Length) {
                $chunk = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
                $chunkSize = $reader.ReadUInt32()
                if ($chunk -eq 'data') { $audioBytes = $chunkSize; break }
                $reader.BaseStream.Position += $chunkSize + ($chunkSize % 2)
            }
            if ($audioBytes -le 0) { throw "No audio data generated for $($contact.Name)" }
        } finally { $reader.Dispose() }
        $duration = [TimeSpan]::FromSeconds([Math]::Ceiling($audioBytes / 32000.0))
        $body = '<div class="haudio"><span class="fn">Voicemail from ' + (Encode-Html $contact.Name) + '</span><a class="tel" href="tel:' + $contact.Phone + '">' + (Encode-Html $contact.Name) + '</a><abbr class="published" title="' + $time.ToString('o') + '">' + $time.ToString('MMM d, h:mm tt') + '</abbr><abbr class="duration" title="' + $duration.ToString('hh\:mm\:ss') + '">(' + $duration.ToString('hh\:mm\:ss') + ')</abbr><span class="full-text">' + (Encode-Html $contact.Transcript) + '</span><audio controls src="' + (Encode-Html ($stem + '.wav')) + '"></audio></div>'
        Save-Html ($stem + '.html') $body
    }
} finally { $synthesizer.Dispose() }

$zipPath = Join-Path $runDirectory 'Voice Bridge sample Takeout.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($sourceDirectory, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
$manifest = [ordered]@{
    Purpose = 'Fictional screenshot input; no real Takeout data was read or copied.'
    CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    SourceZip = $zipPath
    ExtractedSource = $sourceDirectory
    MediaDropFolder = $mediaDirectory
    SuggestedDesktopExport = (Join-Path $runDirectory 'desktop-export')
    Conversations = 29
    Messages = $messageCount
    Calls = 52
    Voicemails = 8
    GeneratedVoicemailAudioFiles = 8
    SuppliedMedia = @($inputMedia.ToArray())
    PendingMedia = @(@('picture', 'video') | Where-Object { $_ -notin @($inputMedia | ForEach-Object { $_.Kind }) })
    ExpectedImportWarnings = @('message_sender_phone_missing', 'attachment_reference_unresolved')
    SourceSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
}
$json = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $runDirectory 'preparation.json'), $json, $utf8)
[IO.File]::WriteAllText((Join-Path $demoDirectory 'latest.json'), $json, $utf8)
$readme = @"
# Voice Bridge Store screenshot data

## Put your picture and video here

$mediaDirectory

Place one picture and one video in that folder. Original filenames are fine.
Pictures: JPG, JPEG, PNG, WEBP, or GIF. Videos: MP4, M4V, MOV, WEBM, or 3GP.
MP4 with H.264 video is a practical choice for the Windows preview.
Your supplied files are copied byte-for-byte into a fresh demo; they are not edited.

## Refresh after adding media

Run root/scripts/prepare-store-demo.ps1 from PowerShell. Each run creates a fresh
source folder and ZIP under runs/. latest.json identifies the newest source and
suggested desktop export directory. Existing runs and exports are retained.

In Voice Bridge, select the ZIP listed below, scan it, then choose the suggested
desktop-export directory from Export and build. The CLI validation output is a
separate directory so it does not block the visible app build.

Current ZIP: $zipPath
Current desktop export destination: $($manifest.SuggestedDesktopExport)

## Contents

29 conversations (including four groups), $messageCount messages, 52 calls,
eight voicemails with locally synthesized audio, and any supplied picture/video.
All text, contacts, phone numbers, and transcripts are fictional. The supplied
picture/video are the only user-provided content. No original Takeout is used.

Two intentional source anomalies populate the Issues page: an empty sender phone
target and an older image reference without an accompanying source file. These
findings are emitted normally by the engine. No issue counts or record counts
are injected into the UI. The pending user picture/video have no references until
provided, so their absence does not create extra warnings.

Media can be browsed after a supplied picture/video is included in a new run.
No screenshots are captured by this script. Capture the real desktop app once
the supplied media is in place. Do not upload captures from the real-data run.
"@
[IO.File]::WriteAllText((Join-Path $demoDirectory 'README.md'), $readme, $utf8)
Write-Output $json
