# Stability follow-up — October 28, 2026

The default application is now 1.8.2 and its streaming adapter is 2.11.0. This
build continues the [October 18 repairs](STABILITY_PASS_2026-10-18.md). The
separate replacement preview is not promoted. No release tag is created by this
stability pass.

## YouTube startup and playback

The user reported YouTube failing to open or remaining on a loading screen.
An isolated Edge profile reproduced TV watch links losing the requested video
or showing YouTube's desktop redirect page. The document compatibility rule
matched `/tv` and `/tv?…` but excluded `/tv#…`, the format used by every TV video
card. After adding the missing fragment boundary, the real TV player loaded and
played a public video. Standard playback was also verified as an optional fallback.

- YouTube TV remains the default, as requested by the user. Standard is an explicit
  fallback under Settings → Connections → YouTube interface. There is no automatic
  switch away from the TV layout. Casting keeps its TV pairing route.
- App tiles, video cards, provider offers, Continue Watching and the embedded
  player's Open YouTube fallback use the selected interface. Links are validated;
  video IDs, valid playlists/indexes and web-player resume times are preserved.
- Voice search stays in the current interface and preserves Hindi/punctuation.
- Reopening reuses the same service tab. Switching from a retained TV session to
  Standard repairs its route once, preserving a valid current video/playlist.

## Responsiveness and crash repairs

- Home has a browser-operation lane separate from host/player requests. It
  activates before waiting for provider snapshots or playback recovery. An older
  queued opening cannot steal focus after Home. Rapid Home/open/Home follows the
  latest request.
- Short player-storage transactions prevent concurrent reports and Home work
  from overwriting other players. Recovery checks current navigation and explicit
  playback intents; late work cannot undo Pause or recreate a closed player.
- The playback heartbeat uses elapsed time rather than an idle wait. Frequent
  voice events previously prevented the timeout from firing at all. This also
  supports the extension worker's documented WebSocket keepalive behavior:
  [Chrome extension WebSockets](https://developer.chrome.com/docs/extensions/how-to/web-platform/websockets).
- A real native regression test reproduced `InvalidOperationException` in
  WebServer.Stop: aborting a socket could synchronously run a callback that
  removed it from the list being enumerated. Shutdown now detaches a snapshot
  before aborting, and failed sends remove sockets idempotently. Connections
  accepted after shutdown are rejected.
- Focused game/history cards retain their focus styling and settle after the
  focus transition instead of animating continuously.
- Home's rendering cache includes watch history; progress updates and dismissed
  history entries refresh the Home shelf without requiring a game-library change.
- Portable builds exclude Chromium's generated extension `_metadata` cache,
  including when development browser tests have generated it in the source tree.

## Verification

| Check | Result |
|---|---|
| Native feature suite | 216 assertions passed |
| Authenticated playback sockets, busy heartbeat, active-socket shutdown | 8 assertions passed |
| Loopback pairing/signaling HTTP | 8 assertions passed |
| Actual-host startup, settings, events and shutdown | 7 assertions passed |
| Default JavaScript regressions | 15 files passed |
| Edge UI regressions | 12 scenarios passed, including six screens, watch-history updates and a 360-video grid |
| Real public YouTube with production UI/extension | TV and Standard: video opening, Home with continuing playback, Pause and Resume passed |
| Separate preview compatibility | 16 JavaScript tests and 21 host assertions passed |
| Portable build/package | Passed; runtime data and generated extension cache excluded |

The live test runs in a disposable profile against a loopback fixture host. It
does not copy provider cookies or interact with the signed-in BWOS browser.
It uses the production UI, service-tab bridge, extension worker and media/key
adapters. Playback and Home use a real YouTube page/video. All test profiles and
screenshots remain under ignored `dist/verification/`.

Repeatable checks:

```powershell
.\tools\test-features.ps1
.\tools\test-features.ps1 -PlaybackSocket
.\tools\test-features.ps1 -StreamHttp
.\tools\test-features.ps1 -Lifecycle
$tests = @(Get-ChildItem tests/*.test.mjs | ForEach-Object FullName)
node --test @tests
# With Edge installed and Playwright available through node_modules or NODE_PATH:
node tests/ui-stability.mjs
# Optional: internet-dependent live provider test, excluded from deterministic CI:
node tests/youtube-live.mjs
# Optional explicit Standard-mode fallback check:
$env:BWOS_YOUTUBE_INTERFACE = 'web'
node tests/youtube-live.mjs
Remove-Item Env:BWOS_YOUTUBE_INTERFACE
.\build.ps1 -OutputDirectory dist\BWOS-Stability
```

## Applied local copy

After the user approved restarting active playback, the default copy at
`dist/BWOS/BWOS.exe` was rebuilt and restarted. The authenticated host reports
version 1.8.2, `youtubeInterface: tv`, a connected browser adapter and one
playback WebSocket. The installed TV rule matches hash-based watch links.
The existing data directory and browser profile were retained.

The previous executable/UI/extension payload is backed up at
`dist/BWOS-before-stability-20261028-2ce1e989/`. To roll back, exit BWOS and restore
only that payload's executable, UI and extensions, leaving `data/` in place.

## Limits

These checks do not establish that every device/account combination is crash-free.
Physical controller drivers, prolonged gaming, paid-provider sign-in/DRM, phone
casting, live microphone recognition and remote-device screen reception still
require their respective hardware/accounts. TV mode remains dependent on
YouTube's TV client and can require account selection. The live TV test selects
Watch as guest in its new profile; existing sign-ins are not changed. No provider response or
signed media request is rewritten.

Native Windows inspection stopped because the computer-use tool could not
determine the current browser URL confidently enough to enforce its policy.
No further native computer-use input or capture was attempted. Isolated development-browser
tests supplied the UI and playback evidence above.
