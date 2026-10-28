# Stability pass — October 18, 2026

This pass repairs the established default app (`src/BWOS.Host`, `ui`, and
`extensions/bwos-tv`). It does not promote the withdrawn replacement preview.
The application version remains 1.8.1; no release was published.

## Reproduced UI failures and fixes

- Unchanged state refreshes replaced the entire library and recommendation DOM,
  restarting image work and discarding focused elements. Each section now checks
  its rendering inputs and retains its nodes when those inputs have not changed.
  Direct filter/service changes update that cache too.
- Bursts of host events issued redundant state requests. Refreshes now share one
  active request and one requested follow-up, preventing older responses from
  overwriting newer state. The 12-event regression permits at most two requests
  and verifies a maximum of one active request.
- Playback and settings requests returned promises from inside a try/catch
  without awaiting them. Failures bypassed the error display and became unhandled
  rejections. Async actions now reach the existing recoverable error display.
- Controller bridge and Show tools settings accidentally fell through the AirPlay
  installation check. Both work when AirPlay is absent.
- A background refresh could replace a modal's return card, leaving focus in the
  closed panel. Closing now finds the replacement card or a visible fallback.
  Recommendation updates preserve focused cards and text selections.
- A resting mouse could reclaim focus after phone/voice navigation. Semantic
  remote input now hides the cursor until the mouse moves, like controller input.
- Disconnected browser controllers retained their held-button/direction state.
  Reconnects reset it. Input polling handles a denied Gamepad API and failed
  handlers, respects IME composition and modifier shortcuts, and accepts keyboard
  input immediately after startup.
- Late app-opening responses and game-launch completion timers could dismiss a
  newer launch overlay. They now check which launch they belong to. Stale logo
  handlers and disconnected/hidden controls cannot activate a previous action.

## Native host and communication repairs

- XInput empty slots used a frame counter that waited roughly six seconds when
  all slots were empty. Missing devices are now probed on a one-second clock.
  Interrupted Hold View gestures reset their hold interval.
- Native tray/controller and watch-history lookup callbacks contain exceptions
  rather than letting an unhandled ThreadPool exception terminate the host.
- Shutdown signals and joins the show-event waiter before disposing its handles,
  closes phone/event/WebSocket connections, stops game polling, and fences delayed
  wake/game/voice actions. UI Quit allows its response to arrive before closing.
- Voice/controller producers enqueue bounded, coalesced event notifications;
  they no longer perform browser network writes. Slow event readers have bounded
  writes and the server limits retained event streams.
- The browser worker bounds host requests, shares connection attempts, coalesces
  queued playback polls, and retains the latest queued media report per frame.
  A stalled host cannot accumulate a fresh poll every second indefinitely.
- An empty browser-process cache now respects its five-second cache interval
  instead of causing repeated WMI scans.
- Settings and watch history use atomic UTF-8 file replacement. Failed writes
  preserve the previous complete file. JSON formatting correctly handles escaped
  quotes after backslashes; invalid/non-finite progress numbers are sanitized.
- Cancelling a pending screen picker or host-session request releases capture
  tracks and revokes late sessions. Viewer requests have deadlines and a failed
  initial connection can be retried.

## Build and regression checks

The build no longer force-terminates a running app. `-OutputDirectory` supports
isolated outputs under `dist/`, with path and reparse-point validation. The
release workflow now reads the default app's version and uses supported build
arguments. A separate Windows checks workflow runs host, JavaScript, browser,
and compilation checks on branch pushes and pull requests.

Verified locally:

| Check | Result |
|---|---|
| Native feature suite | 216 assertions passed |
| Authenticated playback WebSocket | 3 assertions passed |
| Actual loopback pairing/signaling HTTP | 8 assertions passed |
| Isolated actual-host startup, state, events, shutdown | 4 assertions passed |
| Default JavaScript regressions | 15 files passed |
| Edge UI regressions | 9 scenarios passed, including all six screens and a 360-video grid |
| Separate preview JavaScript core/session tests | 16 tests passed |
| Separate preview compilation/host checks | 21 assertions passed |
| Default app compilation | Passed |

UI screenshots under `dist/verification/stability/` contain generated test data,
not provider accounts. The fixture handles bridge acknowledgements; provider tab
ownership, handoff, discovery, and stalled-host behavior are separately exercised
by the worker regressions. The checks workflow has been added but has not run on
GitHub in this session.

Commands from the repository root:

```powershell
.\tools\test-features.ps1
.\tools\test-features.ps1 -PlaybackSocket
.\tools\test-features.ps1 -StreamHttp
.\tools\test-features.ps1 -Lifecycle
$tests = @(Get-ChildItem tests/*.test.mjs | ForEach-Object FullName)
node --test @tests
# With Playwright available through node_modules or NODE_PATH, and Edge installed:
node tests/ui-stability.mjs
.\build.ps1 -OutputDirectory dist\BWOS-Stability
```

## Applied copy and rollback

The idle default app was exited gracefully and rebuilt at
`dist/BWOS/BWOS.exe`, retaining its existing `data/` directory and browser profile.
The restarted host returned an authenticated state snapshot with its existing
library, a connected browser adapter, a playback socket, and voice status ready.
These status checks do not prove provider playback or microphone accuracy.

The previous executable/UI/extension payload is backed up at
`dist/BWOS-before-stability-20261018-abe9ba04/`. It contains no copied runtime
data. For rollback, exit BWOS and restore only that payload's `BWOS.exe`, `ui`,
and `extensions` to `dist/BWOS`, leaving `data` in place, then restart.

## Remaining verification limits

No finite test pass establishes that the app is universally crash-free. Real
Xbox/PlayStation/Switch/generic-controller driver combinations, long gaming
sessions and launchers, provider sign-in/DRM/ad behavior, audible background
continuity, live Hindi/English recognition, actual Bluetooth connection changes,
and Mac/Safari screen reception still need physical/account testing. No game was
launched or terminated, no Bluetooth connection was changed, and no new screen
or microphone capture was authorized by the regression fixtures. The existing
app's voice preference was preserved during its restart.

A final native-window inspection found the restarted BWOS Edge window, but its
state capture returned `Computer Use app approval timed out`; no native input was
issued. Visual inspection instead used the isolated Edge screenshots, alongside
the live host/adapter health checks above.
