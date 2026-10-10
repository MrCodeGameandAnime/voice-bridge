# VoiceBridge Interactive Archive Browser — UI Implementation Spec

> The current persistent sidebar/workspace shell is correct and should be retained.
>
> The missing piece is that the `CONTENT` items in the sidebar must become **interactive archive navigation**, not static counters.
>
> The goal of this pass is to make VoiceBridge behave like a real desktop archive browser:
>
> - sidebar = navigation
> - main workspace = interactive content browser
> - content views = list/master pane + selected-item detail pane
> - Overview / Issues / Export remain task-specific pages
>
> Do not redesign the backend or create speculative universal Takeout abstractions.
>
> ---
>
> ## 1. Sidebar behavior
>
> The following sidebar items must be clickable:
>
> ```text
> CONTENT
> Messages
> Calls
> Voicemails
> Media
>
> TOOLS
> Overview
> Scan issues / Issues
> Export
> ```
>
> The selected item must have a persistent highlighted state.
>
> Sidebar counts should remain visible.
>
> Before archive build:
>
> ```text
> Messages      2,443 pages
> Calls         4,837 pages
> Voicemails      344 pages
> Media         1,319 files
> ```
>
> After archive build/import:
>
> use reconstructed record counts where available:
>
> ```text
> Messages      18,732
> Calls          4,837
> Voicemails       344
> Media          1,318
> ```
>
> Do not misrepresent scan-page counts as reconstructed record counts.
>
> ---
>
> # 2. Two archive states
>
> VoiceBridge must distinguish between:
>
> ## State A — Scanned only
>
> The source has been inventoried, but the local archive/database has not yet been built.
>
> Content sidebar items should still be clickable.
>
> Clicking them should open a lightweight pre-build page showing:
>
> - discovered source count
> - source-page/file information
> - explanation that detailed browsing requires archive build
> - button/link to Export
>
> Example:
>
> ```text
> Messages
>
> 2,443 message pages discovered.
>
> Build the local archive to browse reconstructed conversations
> and individual messages.
>
> [ Go to Export ]
> ```
>
> Do not leave these sidebar items dead or disabled without explanation.
>
> ## State B — Archive built
>
> Once the local database/import has completed, the content items become full interactive browsers.
>
> ---
>
> # 3. Messages page
>
> After archive build, clicking `Messages` should open a two-pane layout.
>
> ```text
> ┌─────────────────────────────┬──────────────────────────────────┐
> │ Conversation list           │ Selected conversation            │
> │                             │                                  │
> │ Search                      │ Contact / participant info       │
> │ Filters                     │ Message thread                   │
> │                             │ Attachments                      │
> │ Conversation rows           │ Provenance / metadata            │
> │                             │                                  │
> │ scroll                      │ scroll                           │
> └─────────────────────────────┴──────────────────────────────────┘
> ```
>
> ## Left pane
>
> Must include:
>
> - search box
> - scrollable conversation list
> - participant/contact/phone number
> - last message date/time
> - message count
> - short message preview where available
>
> Optional simple filters:
>
> - All
> - With attachments
> - Groups
>
> Do not invent labels such as Personal/Work unless those classifications actually exist in stored data.
>
> ## Right pane
>
> When a conversation is selected, show:
>
> - participants
> - source page(s)
> - message count
> - first timestamp
> - last timestamp
> - attachment count
> - issue/warning state if applicable
> - chronological message thread
>
> Each message should preserve:
>
> - timestamp
> - sender
> - direction if known
> - body
> - attachments/media references
> - unknown values as unknown
>
> Do not infer missing sender/direction/contact information.
>
> The thread must be read-only.
>
> Do not include a real compose box or imply that messages can be sent.
>
> ---
>
> # 4. Calls page
>
> Clicking `Calls` after archive build should open a searchable/scrollable call history.
>
> Recommended layout:
>
> ```text
> ┌─────────────────────────────┬──────────────────────────────────┐
> │ Call history                │ Selected call                     │
> │                             │                                  │
> │ Search                      │ Number/contact                    │
> │ Filters                     │ Timestamp                         │
> │                             │ Call type/direction               │
> │ Rows                        │ Duration                          │
> │                             │ Source provenance                 │
> │ scroll                      │ Warnings/issues                   │
> └─────────────────────────────┴──────────────────────────────────┘
> ```
>
> Each call row should show whatever is actually known:
>
> - contact/number
> - timestamp
> - type/direction
> - duration
>
> If direction/type/duration is unknown, display a neutral unknown state.
>
> Do not infer.
>
> Suggested filters:
>
> - All
> - Incoming
> - Outgoing
> - Missed
>
> Only expose filters supported by parsed data.
>
> ---
>
> # 5. Voicemails page
>
> Clicking `Voicemails` should open:
>
> ```text
> ┌─────────────────────────────┬──────────────────────────────────┐
> │ Voicemail list              │ Selected voicemail                │
> │                             │                                  │
> │ Search                      │ Contact/number                    │
> │ Filters                     │ Timestamp                         │
> │                             │ Transcript                        │
> │ Rows                        │ Audio playback                    │
> │                             │ Source info                       │
> │ scroll                      │ Issues                            │
> └─────────────────────────────┴──────────────────────────────────┘
> ```
>
> Selected voicemail detail should show:
>
> - sender/contact/number if known
> - timestamp
> - transcript if present
> - explicit `No transcript available` if absent
> - audio media if resolved
> - explicit unresolved/missing state if audio is unavailable
> - source page/path
> - warnings/issues
>
> If practical with existing WinUI/media APIs, allow local playback of resolved voicemail audio.
>
> Playback must use local exported/source media only.
>
> No cloud access.
>
> ---
>
> # 6. Media page
>
> Clicking `Media` should open a browser for all resolved/unresolved media references.
>
> Recommended layout:
>
> ```text
> ┌─────────────────────────────┬──────────────────────────────────┐
> │ Media list/grid             │ Selected media                    │
> │                             │                                  │
> │ Search/filter               │ Preview if supported              │
> │                             │ Filename/path                     │
> │ Images                      │ Type                              │
> │ Video                       │ Size                              │
> │ Audio                       │ Referenced by                     │
> │ Unresolved                  │ Source provenance                 │
> │                             │ Resolution status                 │
> └─────────────────────────────┴──────────────────────────────────┘
> ```
>
> Filters:
>
> - All
> - Image
> - Video
> - Audio
> - Unresolved
>
> For supported local image/audio/video types, preview when practical.
>
> Never silently omit unresolved references.
>
> ---
>
> # 7. Issues page
>
> The current static `Scan issues: 0` presentation should become a real findings browser once import/export findings exist.
>
> Before archive build:
>
> label clearly:
>
> ```text
> Scan issues
> ```
>
> After archive build:
>
> use:
>
> ```text
> Issues
> ```
>
> and include scan + import + parser + export findings.
>
> Suggested layout:
>
> ```text
> Issues 190
>
> [ All ] [ Messages ] [ Calls ] [ Voicemails ] [ Media ]
>
> Warning
> voicemail/source/file.html
> Referenced audio could not be resolved
>
> Warning
> message/source/file.html
> Attachment reference unresolved
> ```
>
> Each finding should preserve:
>
> - severity
> - category
> - exact issue message
> - source path/provenance
> - related record ID if available
>
> Clicking a finding should open its detail and, if practical, navigate to the related content record.
>
> Do not collapse distinct findings into generic summaries.
>
> ---
>
> # 8. Overview page
>
> Keep the current Overview dashboard.
>
> It should remain the landing page.
>
> It should show:
>
> - source archive
> - scan status
> - source integrity
> - discovered content counts
> - archive/import state
> - issue count
> - next action
>
> Avoid duplicating too much detail already available in dedicated content views.
>
> Overview is a dashboard, not a record browser.
>
> ---
>
> # 9. Export page
>
> Keep Export as its own workspace.
>
> Destination selection belongs here.
>
> Flow:
>
> ```text
> Scan
> ↓
> Inspect
> ↓
> Browse inventory
> ↓
> Export / build archive
> ↓
> Browse reconstructed archive
> ```
>
> After successful build:
>
> - enable content browsers
> - update sidebar counts
> - update archive health/status
> - allow opening destination/offline archive
> - enable optional CSV export
>
> ---
>
> # 10. Search behavior
>
> Search should remain local.
>
> Use existing SQLite/search infrastructure where possible.
>
> Minimum useful search:
>
> ### Messages
>
> search:
>
> - message body
> - participant/contact
> - phone number
>
> ### Calls
>
> search:
>
> - contact
> - phone number
>
> ### Voicemails
>
> search:
>
> - contact
> - phone number
> - transcript text
>
> ### Media
>
> search:
>
> - filename
> - source path
>
> Do not add a new external search dependency.
>
> ---
>
> # 11. Scrolling and performance
>
> These datasets are potentially large.
>
> Do not render all records at once.
>
> Use appropriate WinUI virtualization for:
>
> - conversation lists
> - messages
> - call history
> - voicemail lists
> - media lists
> - issue lists
>
> Scrolling must remain responsive with the real archive:
>
> - 18,732 messages
> - 4,837 calls
> - 344 voicemails
> - 1,000+ media records
>
> Avoid UI-thread blocking database queries.
>
> ---
>
> # 12. Visual behavior
>
> Preserve the current shell and dark/light theme support.
>
> Requirements:
>
> - sidebar remains persistent
> - current nav item visibly selected
> - content list pane scrolls independently
> - detail pane scrolls independently where appropriate
> - no giant dead space
> - search/filter controls remain visible at top
> - selected list row clearly highlighted
> - empty states are explicit
> - unknown data shown neutrally
> - no hover-only essential actions
>
> ---
>
> # 13. Data integrity constraints
>
> This is a browser over reconstructed archive data.
>
> Do not:
>
> - mutate the original Takeout
> - infer unknown values
> - combine records because they look similar
> - hide unresolved references
> - reparse business logic in the UI
> - bypass provenance
>
> The UI must consume existing Core/Storage APIs.
>
> If a required read API is missing, add the smallest read-only API necessary.
>
> ---
>
> # 14. Backend scope
>
> Do not create:
>
> - universal Takeout frameworks
> - Photos abstractions
> - Gmail abstractions
> - plugin systems
> - module marketplaces
> - licensing systems
> - purchase/bundle logic
>
> VoiceBridge remains a Google Voice application.
>
> The UI pattern should be extensible, but backend abstraction should remain evidence-driven.
>
> ---
>
> # 15. Required manual acceptance test
>
> Use the real Google Voice Takeout.
>
> Verify:
>
> 1. Launch app.
> 2. Select Takeout ZIP.
> 3. Scan.
> 4. Confirm sidebar shows scan counts.
> 5. Click Messages before archive build.
> 6. Confirm useful pre-build state appears.
> 7. Click Calls, Voicemails, Media before build.
> 8. Confirm none are dead navigation.
> 9. Build local archive from Export.
> 10. Confirm reconstructed counts replace/augment scan counts.
> 11. Open Messages.
> 12. Scroll conversation list.
> 13. Select multiple conversations.
> 14. Verify correct message thread appears.
> 15. Search messages.
> 16. Open Calls.
> 17. Scroll/search/select calls.
> 18. Open Voicemails.
> 19. Select voicemail.
> 20. Verify transcript/audio states.
> 21. Open Media.
> 22. Verify resolved and unresolved media are represented.
> 23. Open Issues.
> 24. Verify real parser/import issues appear.
> 25. Verify navigation back to Overview/Export.
> 26. Test Light mode.
> 27. Test Dark mode.
> 28. Test 125% and 150% scaling.
> 29. Confirm large lists remain responsive.
> 30. Run full existing tests and Release build.
>
> ---
>
> # 16. Completion definition
>
> This gate is **not complete** if the CONTENT sidebar only displays counts.
>
> It is complete when:
>
> - `Messages`, `Calls`, `Voicemails`, and `Media` are clickable
> - every item opens a meaningful workspace
> - post-build content is browseable
> - content lists scroll
> - selecting a record opens its detail
> - search works where specified
> - issues are inspectable
> - the original source remains immutable
> - existing tests remain green
>
> Commit this as a distinct UI/browser gate and report:
>
> - files changed
> - new read APIs added
> - page/view implementations
> - search implementation
> - virtualization/performance approach
> - real-archive validation results
> - test/build results
> - known limitations
