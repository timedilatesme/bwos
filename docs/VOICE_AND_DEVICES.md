# Voice, browsing, playback and devices (BWOS 1.8.0)

## Stream your screen to another device

Open **Settings → Connections → Stream BWOS** and choose **Choose screen & start**. In Windows' browser chooser, select an entire screen and enable **Share system audio** to include all apps and sound. A window shares only that window; some capture selections do not offer system audio. Windows requires this user selection each sharing session.

Use the displayed link on your Mac, or scan its QR from another device on the same trusted local network. Select **Connect to BWOS**, then **Enable sound**. Arrows/Enter/Escape, the viewer's touch buttons and a standard gamepad control BWOS. Text entry types into the selected BWOS field. If BWOS is not in front on Windows, select Home first. This does not provide arbitrary mouse or remote game input.

Each link pairs one viewer and expires after ten minutes. **Stop sharing**, receiver **Disconnect**, sender closure/heartbeat timeout and session expiry revoke access. Persistent phone pairings are separate. The feature uses free WebRTC without an API key, public relay or cloud audio processing. Signaling is local HTTP and media uses WebRTC encryption, so keep it on your trusted LAN. Windows Firewall may need BWOS allowed on private networks; guest Wi-Fi isolation can block it. Protected Netflix/Prime playback may appear black. Screen sharing cannot bypass their playback protection.

## BWOS browsing

**Apps** opens each original service; YouTube uses its `/tv` route. Recommendation browsing and search live under **For You**. That combines the cached availability catalogue with rendered recommendation shelves from your existing service browser session. Authentication, subscriptions and protected playback remain with the provider.

YouTube's BWOS page combines saved links (`data/videos.json`) and recent videos. **Add video link** accepts standard YouTube, Shorts and TV-watch links; public oEmbed metadata supplies the title/channel when available. Saving still works if metadata is unavailable. Press Options/Y on a saved video to remove it or watch directly. No Data API key is required. **Search all of YouTube** opens YouTube's own search; BWOS does not provide a complete independent discovery catalogue.

“Hey console, browse YouTube” opens the library. “Hey console, open YouTube app” opens the original service. Plain “search for …” on a BWOS browsing screen filters the local library/catalogue; “search YouTube for …” explicitly opens provider-wide search. English/Hindi parser equivalents are supported.

Selecting an embeddable video uses the official YouTube IFrame API. Home docks the **same visible iframe** without restarting BWOS; selecting a different shelf starts a new player, while Next/Previous within its native playlist reuse the current iframe. X/Square enters floating controls, D-pad navigates within them, A/Cross selects, and B/Circle leaves. Expand, seek, Play/Pause, queue controls, retry on errors, direct TV watch and Close are available. Hidden, minimized or obscured embedded playback pauses; resuming requires a visible player. Protected/unavailable videos can refuse embedding; **Open YouTube TV** opens the selected video in the provider's TV route.

The URL authentication token moves into session storage before a third-party player loads, so the SDK's embedding URL does not contain it. Local player reports use a reserved player ID and a separate command queue; browser-worker reports cannot impersonate that player. [YouTube IFrame API](https://developers.google.com/youtube/iframe_api_reference), [embedded-player requirements](https://developers.google.com/youtube/terms/required-minimum-functionality#embedded-player-api-client-identity), [developer policies](https://developers.google.com/youtube/terms/developer-policies).

## Recommendation pages and account-page sync

**For You** combines recommendations across visible services. Each service retains the same BWOS navigation and controller focus, with its own accent and presentation: YouTube thumbnail shelves, Netflix poster/landscape shelves and a large spotlight, Prime landscape shelves. Continue Watching uses tracked BWOS progress; completed titles do not appear as unfinished. Search filters shelves and watch history, and empty states never invent recommendations. Services without an available catalogue show history and a direct Open app route.

For **YouTube, Netflix and Prime Video**, **Sync from [service]** opens a temporary, inactive discovery tab in the BWOS browser profile, mutes it, reads rendered title cards and shelf names, and closes only that temporary tab. It does not navigate, reload or stop an existing player. Temporary discovery previews are excluded from playback/progress reporting. The extension also imports shelves when you browse the supported service home page normally. Sync has a bounded deadline and reports missing cards, sign-in or profile selection as an actionable failure.

Sign in and select the desired provider profile **inside BWOS's Open app session** first. A regular Edge/Chrome session uses a different browser profile and does not automatically sign BWOS in. BWOS does not copy credentials, read cookies, call undocumented provider APIs, intercept responses or automate authentication. This adapter reads only DOM title links, titles, artwork and shelf headings; account identifiers are not cached. Provider title links are validated and stripped of tracking queries. Artwork is restricted to known provider CDNs. Local snapshots live in `data/recommendations.json`; this file is excluded from release ZIPs.

Snapshots show their last update time and stop appearing as current recommendations after 24 hours. Opening a supported BWOS service page attempts a background sync when its snapshot is missing or more than six hours old and the browser adapter is connected. Failed automatic attempts have a five-minute cooldown; the Sync button remains available. There is no scheduled background login or authentication automation. Click Sync again after changing the provider profile. A cached snapshot can belong to the previously selected profile until a successful sync replaces it; BWOS does not infer profile identity. A snapshot reflects cards loaded by the provider page, not its complete paginated recommendation feed. Layout changes, lazy loading and profile pickers can prevent import. These are best-effort integrations; unsupported services continue using the BWOS availability catalogue/history. Playback and subscriptions remain with the provider.

Relevant files: `RecommendationFeeds.cs`, `extensions/bwos-tv/recommendations.js`, `ui/js/recommendations.js`, and `ui/js/media-hub.js`. YouTube's public [`activities.list` home parameter is deprecated](https://developers.google.com/youtube/v3/docs/activities/list); account-page DOM reading is separate from that API.

## Full-window viewing and remote controls (1.7.0)

Opening saved, recent or synced YouTube recommendations uses one embedded player occupying the whole BWOS viewport. For You includes synced YouTube shelves as video cards. Expanded playback has no BWOS bar: YouTube's own controls appear over the video, and opening controls never changes its size. The isolated browser adapter focuses native timeline, transport buttons, captions and settings, supporting desktop and responsive player layouts. No foreign control overlay is inserted. Loading/error recovery replaces the video with an actionable panel; the floating browsing player retains its compact BWOS controls. YouTube prohibits third-party overlays over its embedded player, so expanded viewing uses provider controls rather than a custom skin.

In viewing mode, A/Cross toggles playback, X/Square always toggles, left/right skips 10 seconds, Up focuses the native timeline, Down focuses Play/Pause, and Menu/Y opens native settings. D-pad navigates controls and menu items; left/right on the timeline uses YouTube's own seek shortcut. A/Cross activates the focused native button or menu item. Back closes native menus before leaving control navigation; another Back docks on the current page. Home docks and returns to Home, retaining the same iframe. Native controls return to idle after five seconds of playback without navigation; paused controls and open menus stay available. Keyboard Space/P toggles playback. Keys inside the iframe are handled by the isolated adapter or relayed to BWOS; phone Options works even when the frame has focus. Menu navigation needs the bundled adapter; if it is unavailable, BWOS reports the problem while play/pause and seeking remain usable. Native markup changes may require adapter updates.

The phone remote now sends Play/Pause and ±10-second buttons as playback commands, with a foreground check and explicit missing/not-ready player feedback. It selects the active foreground player before a background one. Local playback commands are consumed immediately on playback events, with polling as a fallback. This does not depend on whether a player button or the iframe has keyboard focus.

Netflix/Prime recommendation launches carry the existing explicit playback intent. When a video starts in a smaller native container, the adapter expands its whole player wrapper to the browser viewport, retaining controls, captions and ad layers. It leaves already-full players alone. Muted browsing previews do not trigger expansion unless the user explicitly launched playback. YouTube's own TV layout remains provider-controlled. These adapters still depend on provider markup and do not guarantee every third-party player or hardware controller. Real protected playback and physical devices require testing on that provider/account setup.

## Artwork and simpler browsing (1.7.0)

Apps opens the original services directly. For You contains all integrated recommendation browsing and provider selection. Its Search button (or Enter) searches supported visible YouTube/Netflix/Prime pages and displays validated results in BWOS; these previews do not replace Home recommendations. Search still depends on the rendered website and an appropriate signed-in/profile-selected BWOS browser session.

YouTube's primary grid is collected alongside topic shelves. Muted temporary discovery tabs load multiple visible DOM batches, preserving earlier cards when the provider virtualizes its list. Load more requests a larger snapshot, up to 360 cards with bounded scroll/time limits; it is not an unlimited provider recommendation API. Normal live app/player tabs are never scrolled by discovery. Tiny Prime shelves combine into useful collections. Grid navigation stays within neighboring videos before leaving for the rest of the UI. Glass highlights and blur are concentrated on shared navigation and floating surfaces.

Recommendation cards accept poster, artwork and backdrop fields. Landscape imports prefer backdrops without stretching them into portrait covers. Every title remains readable if artwork fails; image loading tries distinct known alternatives once, then uses a title fallback. YouTube video IDs supply a thumbnail fallback. Rendered provider imports read lazy image attributes, responsive sources and CSS backgrounds. Valid CDN query parameters survive host validation because they can specify required delivery signatures and image sizes; title-link tracking queries are still stripped. Older snapshots with already-damaged URLs need **More → Refresh from app**.

For You and service pages use a single horizontally scrolling service strip. Detailed source and sync information is collapsed but reachable by controller. **More** contains import, refresh and link-saving actions; **Open app** stays directly available. Identical shelves are removed, and Home avoids repeating a saved YouTube shelf identical to an imported one. Imported recommendations without real watch history are not labelled “Recently watched”. Unknown movie/series metadata is not invented. A single-provider title launches immediately; a multi-provider title retains the provider chooser. Focus starts on the featured watch action or first shelf item rather than secondary toolbar actions. Inactive views no longer remain visible during the transition.

Settings uses expandable categories rather than a continuous list of every control. Open categories are retained during state refreshes. Controller navigation and numbered voice choices exclude closed category contents. Home-button setup instructions are also collapsed. Native buffering is labelled correctly, and Pause works while buffering; late buffering cannot restart a covered player.

The selected YouTube shelf is normalized into a bounded native playlist through the documented `loadPlaylist`/`cuePlaylist` API. Native Previous/Next and automatic progression retain one iframe; playlist index changes update BWOS metadata and progress. Docked playback stays docked when advancing. Hidden playback cannot start the next item through BWOS commands. The floating panel shows the next title and only available queue buttons. Browser autoplay policy may require pressing the native Play button. A late SDK readiness callback after failure is destroyed instead of starting a hidden player. This queue applies to YouTube; Netflix/Prime episode progression remains provider-controlled. There is no shared decoder or automatic cross-provider playlist for protected streams.

## Getting started

Enable **Settings → Voice → Voice control**. BWOS automatically downloads the free local Whisper runtime and model once, with progress and retry in Settings. No account, API key, Python, CUDA toolkit installation, or paid service is required. Files live under `data/tools/whisper`; later launches work offline. Microphone access must be allowed for desktop apps in Windows settings. Select the physical microphone if the Windows default is wrong. No audio is uploaded or saved by BWOS.

NVIDIA PCs receive a quantized multilingual large-v3-turbo model, CUDA runtime and required cuBLAS libraries (about 1.3 GB downloaded, roughly 2 GB installed). Other 64-bit PCs receive a smaller multilingual base model and CPU runtime (about 68 MB downloaded). BWOS falls back to the base model if GPU initialization fails. First-use GPU kernel warm-up happens before listening, and can take a minute. The legacy Vosk engine remains selectable.

**Hey console** wake mode is the default for new installs. Say **“Hey console, volume up”** in one sentence, or say **“Hey console”**, watch for the assistant to show “Ready”, then give a command. Supported follow-up commands work for 12 seconds after the last accepted command. The microphone still captures locally to detect the phrase, but ordinary speech outside that window does not execute commands. Existing installations retain their selected mode; this development PC was changed to wake mode at the user's request.

**Direct commands** remain optional: when BWOS is on screen, including streaming apps, say the command directly. BWOS ignores commands while another application or a game has focus. Recognition remains local while waiting for BWOS to return.

**Commands anywhere** accepts supported commands while a streaming app, the desktop or a game is in front. No wake phrase is required. Say **“Hey assistant”**, **“Show assistant”** or **“असिस्टेंट दिखाओ”** to bring up its transient overlay. It also appears after a recognized command, then hides after seven seconds (or after a longer spoken reply finishes). **×** hides the overlay while listening continues. **Stop listening** pauses command acceptance; **Hey assistant** wakes it again. Turning voice control off releases the microphone. Navigation and typing continue to target BWOS rather than unrelated desktop windows.

Hindi wake phrases include **“हे कंसोल”** and **“हेलो कंसोल”**. “Hey assistant” remains an alias. Wake mode lets “Open YouTube” or “Go home” bring BWOS forward from the desktop or a game after waking it.

“Stop listening” / “सुनना बंद करो” pauses command acceptance until “Hey console” / “हेलो कंसोल”. Turning Voice control **off** closes the microphone. A green microphone and listening caption indicate active command acceptance.

**Automatic** recognition handles English and Hindi without choosing an accent. You can select English or Hindi to avoid language detection on short commands. Whisper handles a broader range of speech than the previous small Vosk models; no local model guarantees every accent, noisy room or mixed-language phrase. The CPU model trades some accuracy for responsiveness. BWOS recognizes common service-name aliases including “you tube” and “net flix”. Use a microphone near the couch. Wake mode uses transcription, rather than a dedicated low-power wake-word engine. On the development GTX 1050, generated wake/command speech took about 2–3 seconds to transcribe after capture; this is not an instant wake detector. Punctuation inserted by dictation (for example “Hey, console.”) is accepted. A short bounded queue preserves a wake phrase and its immediate follow-up during recognition; old queued speech expires. Wake-only acknowledgement is visual so it does not mute the next sentence.

The assistant orb glows with microphone activity, changes while processing, and pulses during spoken acknowledgements. **Spoken replies** use installed Windows voices; available voice quality and Hindi speech depend on that PC's installed voices. Recognition pauses during replies and briefly afterward to avoid the assistant hearing itself. Replies currently use English acknowledgement text. This is command control and search, not a conversational chatbot.

The 1.4.1 fix shares Windows audio COM activation across Bluetooth, volume and microphone features, preventing an audio-enumerator cast failure that disabled echo cancellation. A live six-second probe of the selected Realtek input confirmed echo-cancelled PCM after the fix.

Microphone capture now uses Windows' built-in **Voice Capture DSP** for acoustic echo cancellation and noise suppression before recognition. It compares microphone input with the default speakers' audio to reduce music leaking back into the microphone. It selects the configured physical microphone, runs locally and requires no additional download. A silent shared output stream keeps the DSP capturing in quiet rooms. BWOS checks for default speaker/input changes every five seconds and reopens the DSP when needed. If Windows cannot initialize it or stops delivering audio, capture falls back to the basic microphone; Settings shows the processing mode and fallback reason. Echo cancellation improves the input but does not guarantee recognition over loud speakers or audio routed to a different output device. See [Microsoft's Voice Capture DSP documentation](https://learn.microsoft.com/en-us/windows/win32/medfound/voicecapturedmo).

### If commands do nothing

Settings → Voice shows a live microphone activity meter and the last recognized sentence, including unclear or unrecognized speech. A moving meter means audio is arriving, not that a command was understood. If the meter stays at zero while speaking, check the selected physical microphone, its mute switch and Windows input volume. "Waiting for BWOS to be on screen" means direct commands are paused because another window has focus. Wait for the green microphone indicator, speak, then pause for two seconds so recognition can finish the sentence. "Open YouTube now" and "Please open Netflix" also work. Feedback remains in memory only; BWOS does not save audio or speech transcripts to logs.

## Commands

Additional commands in 1.3.0:

| Say after “Hey console” | Behavior |
|---|---|
| Volume up / Louder / Please go volume up | Raises Windows master volume by five percentage points |
| Volume down / Quieter | Lowers master volume by five points |
| Set volume to fifty percent / आवाज पचास प्रतिशत करो | Sets a specific level (0–100) |
| Increase volume by ten percent / Lower volume by twenty | Changes volume by the requested amount |
| Mute / Unmute / आवाज चालू करो | Sets mute explicitly; saying Mute twice leaves it muted |
| Pause music / Resume playback / Play pause | Controls the foreground player, or the latest player in the background |
| Stop music / Close player | Closes that player tab; foreground playback returns Home first |
| Show player controls / Show now playing | Brings BWOS Home forward and focuses its floating controls |
| Reopen player / Back to music | Selects the latest retained background player |
| Skip forward twenty seconds / Rewind ten seconds | Seeks 1–100 seconds, clamped to the media duration |
| Restart song / Start from the beginning | Seeks to the beginning |
| Next song / Previous video | Activates an exposed next/previous player button, when available |
| Subtitles on / Subtitles off | Sets exposed HTML subtitle/caption tracks; bespoke provider menus may require manual control |
| Show keyboard | Opens the existing phone keyboard/QR interface |
| Reload page / Retry YouTube | Reloads the foreground streaming page; on Home, refreshes its data |
| Show voice commands / What can you do | Opens the voice help panel |

Live streams without a finite duration cannot use the seek controls. Unsupported provider controls return an explanation. The assistant uses explicit local intents; these additions do not make it understand arbitrary conversation or every possible app operation. Precise master volume uses [Windows EndpointVolume](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolume).

| English | Hindi | Result |
|---|---|---|
| Open YouTube / Open Netflix | यूट्यूब खोलो / नेटफ्लिक्स खोलो | Opens the service |
| Launch Rocket League | `[game name] चलाओ` | Matches installed games; ambiguous matches show choices |
| Open games / Open devices / Open settings | गेम्स खोलो / डिवाइस दिखाओ / सेटिंग्स खोलो | Changes the screen |
| Up / Down / Left / Right | ऊपर / नीचे / बाएं / दाएं | Moves focus |
| Select / Back / Go home | चुनो / वापस / घर चलो | Selects or navigates |
| Pause / Resume | रोक दो / जारी रखो | Controls the streaming player or background player from Home |
| Resume game | गेम जारी रखो | Returns to the running game |
| Volume up / Volume down / Mute | आवाज बढ़ाओ / आवाज कम करो / आवाज बंद करो | Changes Windows volume; mute toggles |
| Search YouTube for guitar lessons | यूट्यूब पर संगीत खोजो | Submits a YouTube search |
| Show numbers / Select three | नंबर दिखाओ / नंबर तीन चुनो | Numbers visible choices and activates one |
| Hide numbers | नंबर हटाओ | Removes numbered overlays |
| Voice help | मदद | Opens the command guide |

Numbers follow the visible screen or open dialog. Voice commands never run arbitrary executables, shell commands, or power actions. Use normal navigation to access power controls.
Numbers up to 99 work in English. Hindi supports common names up to 20; for larger numbers, speak the digits separately (for example, “नंबर दो एक” for 21).

## YouTube search

The UI carries the recognized search text to YouTube TV with `#/search?q=…`. YouTube's TV client resolves `q` as a native search deep link. Searches inside YouTube update that same TV route; they do not depend on locating its on-screen keyboard or switch to desktop YouTube. English and Hindi queries are URL encoded. A request marker lets the same query be repeated. The legacy `#bwos-search=…` marker remains accepted for compatibility. YouTube can change this undocumented route; real website behavior must be checked when its client changes.

YouTube's own microphone button is not required. Playback and numbered choices use the extension; Home and directional commands use the existing Windows input bridge. Third-party player DOM changes can affect playback controls.

Inside YouTube, say **“Search for Arijit Singh songs”**, **“Search guitar lessons”** or **“हिंदी गाने खोजो”**. The extension sends the query to the current TV interface's native search route. **“Search YouTube for …”** also works from Home or another app. A heartbeat reports the actual foreground page, so routing no longer depends only on its Windows title. Settings shows whether the streaming integration is connected.

## Devices

The **Devices** tab lists browser-visible controllers (plus an XInput fallback), paired classic Bluetooth devices, Windows-known Bluetooth LE peripherals, and Bluetooth audio endpoints. It refreshes every 10 seconds, with a manual Refresh action.

**Connect / Disconnect** uses the documented `KSPROPSETID_BtAudio` one-shot driver requests. Only audio endpoints whose drivers advertise both operations show those controls. An accepted request is an attempt, not proof of a connection; the screen refreshes its observed audio state after the request. Disconnect asks for confirmation in the UI.

Pairing and peripherals without supported connection controls open Windows Bluetooth settings. BLE PnP presence is not a reliable connection signal, so those entries explicitly say when live connection status is unavailable. Bluetooth link state and audio endpoint state are shown separately.

## Implementation and verification

- `VoiceCommands.cs`: English/Hindi intent parsing and listening-session rules.
- `Microphone.cs`, `SpeechSegmenter.cs`, `WhisperEngine.cs`, `WhisperAssets.cs`, `VoiceService.cs`: native capture, in-memory utterances, local recognition, commands and automatic installation.
- `SpeechFeedback.cs`, `ui/js/assistant.js`: Windows spoken replies and assistant animation.
- `PlaybackService.cs`, `PlaybackWindow.cs`, `ui/js/playback.js`, `extensions/bwos-tv/media.js`: live player inventory, controls, desktop companion and media reports. The extension worker preserves media tabs on Home and receives commands over an authenticated loopback WebSocket.
- `EchoCapture.cs`, `Microphone.cs`: local Windows echo cancellation, speaker reference, device selection and basic capture fallback.
- `Devices.cs`: classic Bluetooth/BLE inventory and Core Audio/KS connection controls.
- `ui/js/voice.js`: numbered choices and title/service matching.
- `extensions/bwos-tv/voice.js`: streaming playback, numbers, and YouTube search adapter.
- `tests/FeatureTests.cs`: command/session tests, model loading, generated English speech, and read-only hardware enumeration; never captures the microphone or changes connections.
- `tests/preview.mjs`: deterministic UI fixture; `tests/voice-ui.test.mjs`: Unicode and title-alias tests.

Generated English speech, automatic language detection, unrelated speech and silence passed with GPU and CPU Whisper. On this PC (i5-9300H, 16 GB RAM, GTX 1050 with 4 GB VRAM), short English requests took approximately 1.6 seconds with English selected, 2.3 seconds with Automatic, and 2.9–3.4 seconds on the CPU fallback, plus the 700 ms pause that ends an utterance. GPU startup after warm-up caching took around 3–4 seconds. These are generated-speech measurements, not an accuracy guarantee for a real microphone.

Background handoff, concurrent player reports, pause/resume/focus/stop, home-token transfer and sender validation have isolated extension tests. The UI was checked with a deterministic fixture. Real Hindi speech, microphone accuracy, provider-specific background playback, live YouTube search and Bluetooth connection changes still need a user hardware trial.

On the development PC, the browser adapter was initially disconnected despite its extension being installed. Its worker registration was stuck on version 2.1.0 and another extension repeatedly reported internal errors. Backing up and rebuilding only `data/profiles/shell/Default/Service Worker/Database` and `ScriptCache` while BWOS and its private browser were closed restored registration of version 2.3.2 and authenticated live browser heartbeats. Cookies and site storage were retained. This is a local repair, not proof that every disconnected adapter has the same cause. Never remove the entire browser profile as a first troubleshooting step.

## Background playback

Start a video or music in a BWOS streaming app, then press Home. BWOS selects a Home tab in the **same fullscreen window**, keeping the original player alive in a background tab without changing its URL. It uses a normal fullscreen browser window so retained tabs stay in that window; it no longer uses browser app mode or creates another Home window. The **Now Playing** cloud displays the actual paused/playing state, a playing animation, elapsed time and a progress bar for media with a finite duration. Timing reports update without rebuilding the cloud or interrupting controller focus. Play/Pause controls that player, the title or arrow selects it again, and **×** stops playback by closing only that player tab. Multiple players remain separate. Say **Pause** or **Resume** from Home to control the most recent background player.

In 1.3.0, Home remains loaded when opening a service. Its extension bridge creates or reuses a tab for that service's hostname, instead of replacing the Home document. Opening a landing page such as YouTube's Apps tile selects its existing session; an explicit search or title request navigates that service tab. Returning Home activates its retained tab **without a URL update or reload**, so the boot animation does not repeat. Tabs are protected from automatic discarding. Navigation requests validate the Home sender and HTTPS destination; the UI gets an error after a bounded wait if the adapter is unavailable. [Chrome Tabs API](https://developer.chrome.com/docs/extensions/reference/api/tabs).

Press **X** on Xbox, **Square** on PlayStation, or **Y** on Switch while on a BWOS screen to focus the floating controls. Use the D-pad to choose a control and **A / Cross** to activate it. **B / Circle** or the same shortcut returns to the previous focused item. A **Now playing** entry is also available in the top bar; moving down from the final Home shelf can reach the cloud. Focus has a visible outline and survives playback timing updates and pause/resume changes. Multiple players are intentional across different services; reopening the same service reuses its tab.

Player command responses now have deadlines. A provider's stalled Play request produces an error rather than leaving the worker's serialized command queue blocked. Foreground voice media intents use the same adapter as the floating controls, including embedded players already reported by the media adapter.

For YouTube stability, 1.3.0 retains the TV user agent and search route but removes global JSON/fetch response rewriting, embedded player-data interception, and forced ad seeking. These were compatibility risks, not a verified explanation for every provider error. Ads may now play. “Hey console, reload page” offers an explicit retry without erasing sign-ins. Live YouTube playback still requires verification on the user's setup.

Version 1.3.1 tried adding Xbox identification to modern Edge for `/tv` documents. That did not reliably retain the TV interface: the live session redirected to desktop `/watch`. Version 1.3.2 restores the earlier TV compatibility identity and suppresses desktop client hints on TV documents only. Desktop YouTube and player API, script and video requests retain native headers. The actual browser engine is unchanged. Startup removes the stale 1.3.1 dynamic rule, while the packaged navigation rule is available before the worker starts. Opening the YouTube TV tile migrates a retained desktop video to its TV watch route once, preserving the video/playlist in the same tab. Existing TV sessions continue without reloads. This addresses the interface regression, not a verified cause of every playback error. [YouTube playback troubleshooting](https://support.google.com/youtube/answer/3037019).

The `/tv` adapter is a compatibility workaround in a desktop browser. Google's Living Room integration has a device-partner certification process; BWOS is not a certified TV device. No user-agent choice can guarantee indefinite compatibility. A more maintainable Windows approach would give BWOS its own TV-sized navigation around supported YouTube web playback, with explicit testing of search, sign-in, controller/voice controls and playback. That would be a BWOS TV experience rather than Google's official TV app. An IFrame-based design would also need to account for unavailable embeds and the API's restrictions on hidden/background players, rather than promising feature parity. [Living Room integration](https://developers.google.com/youtube/devices/living-room), [Player API](https://developers.google.com/youtube/iframe_api_reference), [Developer policies](https://developers.google.com/youtube/terms/developer-policies).

Failed HTML media and the visible YouTube TV error dialog now retain a cloud with **Retry** instead of a misleading Play button. Retry reloads only that service tab at its existing route; it does not restart BWOS or create a second player. There are no automatic retry loops. Passive, non-blocking network observers report only failed player API/video status codes and browser network errors. Normal aborted range requests are ignored. The authenticated `GET /api/playback/diagnostics` holds at most 30 sanitized records; `data/bwos.log` records error categories/codes. Response bodies, signed stream URLs, cookies and headers are not captured. [Chrome webRequest API](https://developer.chrome.com/docs/extensions/reference/api/webRequest).

Voice and controller Home send a single F24 signal, handled by the streaming page adapter even inside embedded frames. The original window receives an explicit fullscreen update after tab activation, because leaving an HTML fullscreen player can exit fullscreen. Active player tabs are excluded from automatic discarding, and BWOS avoids backgrounding its occluded browser windows. If a provider pauses on focus loss, the media adapter makes up to two resume attempts during the first 900 ms of the handoff (with a bounded wait for each play request). It does this only for a controllable player that was playing before Home. A cloud Pause command or manual page interaction cancels recovery. This is a short transition repair, not continuous automatic playback; later pauses remain paused.

BWOS also prepares the private browser's worker cache once per extension version, before opening its browser. It moves `Database` and `ScriptCache` into a recoverable `BWOS-backup-*` directory and records the prepared version in the profile. This addresses Edge retaining older background-adapter code after an app update. Ordinary restarts keep the current cache. Cookies, site storage and `CacheStorage` are retained.

Home now asks for a live player snapshot instead of relying solely on a periodic report. Media detection includes open shadow roots and short videos. An audible tab is kept alive even if its media element cannot be controlled; the cloud then offers reopen/stop and says to reopen for playback controls. Handoff failures keep the player open and display an error. Home-window lookup uses actual tab URLs rather than a port-specific browser match pattern.

When another desktop app is in front, a small topmost companion offers the same controls without taking focus. Drag its title to move it; **−** collapses it to a music bubble; **›** cycles through multiple players. It hides when BWOS is foreground and reappears outside BWOS while a player exists. Exclusive fullscreen games can cover desktop overlays.

This version tracks HTML video/audio playing inside BWOS's browser profile. Native Windows apps and media in unrelated browser profiles are not yet integrated. Some streaming providers pause hidden/minimized playback, disallow programmatic resume, or replace their player DOM. The cloud reflects their actual state and reports control failures; it does not bypass provider restrictions. Closing BWOS also closes its player windows.
