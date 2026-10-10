# BWOS 1.8.0 quality pass

## Streaming, keyboard and wake follow-up

Provider images occupy their whole For You filter tile. Search selection opens an on-screen keyboard with controller navigation, pointer selection, physical/phone typing and Unicode submission. Physical/phone typing focuses the draft, so Enter submits rather than activating a virtual key.

Settings → Connections → Stream BWOS captures the user-selected Windows screen/window and optional system audio. WebRTC carries media directly between local browsers. A distinct random, single-use link pairs one viewer; saved phone credentials grant no screen access. Signaling stays in memory, is bounded, and is scoped to a session. Stop, receiver Disconnect, host heartbeat timeout and session expiry revoke access. The LAN listener validates Host and Origin, reads UTF-8 byte lengths correctly and avoids logging request bodies. The host API remains authenticated and loopback-only.

The receiver offers arrows, Select, Back, Home, volume, playback, text entry and standard controller buttons. It does not provide arbitrary remote desktop pointer/game input. Windows requires screen selection for each session. For all apps and audio, select an entire screen with Share system audio. Local signaling uses HTTP on a trusted LAN; WebRTC media is encrypted. Firewall/guest-network isolation can prevent connection. No Internet relay is configured. Protected Netflix/Prime video can be black. Actual Mac/Safari and protected playback were not tested on this PC.

YouTube collection handles more rendered card formats and tries a TV source when desktop Home is sparse. It deduplicates both sources and preserves desktop cards if TV expansion fails. The pre-update live source exposed only two fresh YouTube cards; sample success does not prove its personalized feed is repaired. Netflix now collects 80 fresh titles, with 104 retained titles; Prime has 61.

Wake listening uses a four-second continuous-sound maximum with 400 ms overlap; armed commands retain the longer window. Service-name hints and explicit wake variants improve spelling handling. Missed-wake transcripts appear in diagnostics without activating the overlay. Generated speech passed on both CPU and the GTX 1050 NVIDIA backend. GPU transcription took approximately 2.4–6 seconds per generated phrase; this is not instant wake-word detection. The user's accent and live background music remain unverified.

Verification: 207 host assertions, 8 actual loopback HTTP assertions, 13 JavaScript test files, actual browser WebRTC decoded frames and generated-tone reception, and the production sender/viewer with generated audio/video, remote control messages and capture/session cleanup. CPU/NVIDIA speech checks each pass 9 assertions. Sample UI screenshots are under `dist/verification` and contain no signed-in recommendations.

The installed Windows app was also opened and inspected directly: For You logos fill their tiles, selecting search opens the keyboard, arrows move between keys and Select enters a character. An Edge saved-search popup covered the keyboard during this walkthrough; search fields now explicitly disable autocomplete. The actual installed streaming routes passed viewer-page loading, distinct pairing, signaling, single-use linking and Stop revocation without capturing personal screen data. Live YouTube refresh still returned two fresh cards.

## Sparse-feed and logo-filter follow-up

The installed 1.7.0 cache had 3 Netflix cards, 2 YouTube cards and 61 Prime cards. This pass adds Netflix `browse?jbv` links and numeric DOM title identifiers, modern YouTube lockup titles, bounded horizontal Netflix paging, progressive page/inner-container scrolling and a longer hydration window. Incomplete discovery no longer declares itself complete, and the host protects fuller recent feeds from sparse complete reports from older adapters. Fresh-card counts are distinguished from retained-card counts.

Startup discovery no longer depends on a timing-sensitive integration ping. Collection errors are visible, including profile/sign-in/Home-feed states. The adapter cache fingerprint includes script contents so same-version development builds cannot silently run older workers. The service strip now uses app logos, neutral glass buttons, accessible names and tooltips.

Verification: 184 host assertions, 12 JavaScript test files, and browser DOM regressions for Netflix browse links, modern YouTube cards, artwork, filtering and controller focus. Discovery regressions cover delayed hydration, virtualized vertical/horizontal shelves, nested scrolling and sparse completeness. The logo layout was visually inspected with sample data; `dist/verification/for-you-logo-filters.png` is a sample preview, not a signed-in recommendation screenshot.

At the end of 1.7.1, live collection was unresolved. The newer Netflix diagnostics above supersede that result. YouTube still needs a live feed check.

## Fixed findings

- Apps now launches original service pages. For You contains all recommendation browsing, provider selection and search; the separate internal service-page view is removed.
- YouTube's main grid had been skipped whenever a topic shelf was present. Collection now includes it and reads multiple lazy-loaded batches in a muted temporary tab. Virtualized cards survive scrolls; work and storage are bounded to 360 cards, with explicit Load more/refresh.
- Nested Prime collection-card nodes and sparse provider-filtered catalogue rows produced one-card shelves. Outer collection selection and row compaction keep distinct titles together.
- Native search had only filtered cached items. Supported service search now returns sanitized rendered results into a temporary preview without mutating recommendation history. Outdated search responses are discarded.
- Browser testing reproduced Enter activating the previously selected control, and delayed filtering removing result focus. Native focus now follows keyboard/input focus, pending filters are cleared, and grid directions prioritize neighboring video cards over the floating playback panel.
- Shared glass surfaces, calmer focus animation, a four-column YouTube grid and balanced mixed-service previews reduce visual clutter.

- Imported Netflix/Prime cards had artwork and backdrop URLs, but the renderer only read poster. The installed app's aggregate diagnostics showed 27 such cards. Cards now render those fields and try alternative artwork before a readable fallback.
- Lazy images and CSS-background covers were missed by the discovery collector. CDN queries were removed by host sanitization, potentially breaking delivery signatures or image sizes. Both are retained correctly, with provider-domain validation unchanged.
- Titles disappeared when a cover loaded, or duplicated on fallback cards. Each recommendation card now has one explicit title label; service badges sit clear of it.
- Repeated source paragraphs, headings and actions crowded the page. Source details are collapsed, secondary actions move to More, service navigation stays on one line, and identical shelves are removed.
- Single-provider recommendations showed an unnecessary provider chooser. They launch directly; ambiguous providers retain the chooser.
- Next/Previous rebuilt the YouTube player. Native playlists now reuse it, handle automatic progression and update metadata. Docked Next keeps the floating layout; upcoming titles are visible there.
- Playlist initialization could race with play. Visible launches use YouTube's dedicated playlist loader. A late readiness callback after an error cannot create hidden playback.
- Removed sync buttons had supplied the duplicate-request guard. Sync now has an explicit per-service guard independent of toolbar layout.
- Settings is grouped into expandable categories. Controller and numbered voice selection exclude closed category contents; source disclosures and categories keep pointer and controller focus aligned.
- Buffering is shown as buffering, and toggle can pause it. Covered or hidden playback cannot restart through a late buffering event.
- Home's snapshot, page notification and playback handoff have response deadlines. A frozen provider cannot indefinitely block Home or later app-opening commands; its tab remains open, with a recovery error in the playback cloud when needed.

## Verification and limits

Host checks: 181 assertions. JavaScript checks: 12 test files. Browser regressions cover artwork fallback, lazy/CSS imports, source disclosure, focus navigation, native menus, provider viewport fill and player lifecycle. A real public YouTube video automatically advanced to another in the same full-viewport iframe, with matching BWOS metadata. Visual screenshots use explicitly labelled sample recommendation data.

The bundled provider adapters still depend on website markup. Direct YouTube TV is an optional compatibility route. Netflix/Prime protected playback and their episode queues remain provider-controlled; BWOS does not replace their protected playback engines. Physical controller and paired-phone behavior require the installed Windows session to be accessible to the live tool. No claim is made that every issue in every provider or device is resolved.
