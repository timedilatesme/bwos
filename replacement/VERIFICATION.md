# Withdrawal and repair — October 13, 2026

BWOS 2.0.0 is unpublished (draft). GitHub main was restored by normal revert commits to exactly the tree at 6f6d4b9 (1.8.0), verified with git diff and the remote main hash. The local default payload was restored from its 1.8.1 backup without replacing data or changing its startup path. The repair branch is local only.

The replacement preview now copies the established UI in full. Liquid Glass CSS and the focus engine match the preserved source byte for byte. The discarded flat shell is removed. A small compatibility module connects acknowledged native/playback operations and phone navigation/text to the original API, semantic input listeners and focus.current. Root build.ps1 builds the established app again; replacement/build.ps1 builds the separate preview by default.

Validation of the repair:
- Preview compilation and 21 host assertions passed.
- All 29 JavaScript tests and 207 existing host assertions passed.
- Isolated headless Edge with simulated gamepads passed: initial focus, A activation while native focus is on body, B return, D-pad/stick shelf movement, bumper tab switching, held-button edges, Steam duplicate suppression, Nintendo confirm swap, PlayStation glyph selection, generic DirectInput POV, six screen layouts and Unicode phone editing.
- Delayed launch acknowledgement exposed a Back/overlay race in the preserved UI. The fix checks the original launch attempt before attaching its timer; a deterministic delayed-response fixture confirms dismissal remains safe.
- Generated fixture screenshots were visually inspected. They are not live provider or controller evidence.

The repaired preview has not been promoted or published. Real controller behavior, provider Home/reopen/audio journeys, microphone recognition and real phone/viewer sessions still need testing. The earlier report below is historical evidence for the withdrawn build, not approval of the repaired preview.

---
# BWOS 2.0 verification — October 13, 2026

## Passed

| Check | Result and scope |
| --- | --- |
| Windows build | .NET Framework compiler produced `dist/BWOS-Next/BWOS-Next.exe` |
| Replacement host tests | 19 assertions: acceptance/completion, duplicate dispatch, expiry, overflow, native handoff/deduplication, cancellation, remote identity/Unicode, diagnostic text redaction, English/Hindi wake parsing and screen-session revocation |
| Replacement JavaScript | 16 tests: frozen adapter, bounded lanes, cancellation, duplicate protection, session registry, TV URL validation, provider playback intent/Prime identity, artwork fallback, deduplication, sparse rows, Unicode edits, discovery/open/Home/cleanup, eight reopen cycles, mute handling, worker restart and pre-handoff playback intent |
| Isolated headless UI | All six screens, no horizontal overflow at 1920×1080, Unicode keyboard/remote text/search, paging, stable cloud focus under progress updates, shared game/open/playback actions, QR SVG rendering, capture under user activation, requested system-audio options and sender revocation using generated tracks |
| Existing regressions | 207 host assertions and all 13 original JavaScript test files passed |
| Actual Windows launch | Tool inventory returned a separate `BWOS Next - Profile 1 - Microsoft Edge` window |
| Actual host contract | Authenticated API returned version 2.0.0-dev, 8 games, 17 services, YouTube/Netflix/Prime caches and a connected session adapter in the initial candidate |
| Preservation | Original tracked edits remain the same nine changed files; no originals were reset or replaced. Migration wrote only the candidate data tree. |

## Default promotion

The primary build and isolated preview each passed **21 host assertions**, including the endpoint/title checks added for promotion. The primary app uses `BWOS.exe`, the original singleton/Windows startup identity, original data/profile folder and ports 47913/47914. The optional preview retains its separate identities and ports.

On this PC the original app payload was backed up to `dist/BWOS-before-2.0.0-20261013-181056`, the rebuilt executable/UI/adapter payload replaced the default files, and the Start menu shortcut points to the existing `dist/BWOS/BWOS.exe`. The existing Windows startup entry already points to that same path. A local authenticated probe returned version **2.0.0**, native Home ownership enabled, the adapter connected and a **completed** Home acknowledgement. File hashes for `config.json`, `phones.json`, `session.key`, `videos.json`, `recommendations.json` and `watching.json` matched before/after promotion. Voice remained enabled in the user's existing direct mode. No microphone accuracy or audio-continuity claim follows from this probe.

After promotion, the legacy host suite passed 207 assertions and the combined JavaScript run passed all **29 tests** (13 original files plus 16 new tests). The release archive was checked for the primary executable and absence of runtime data, tokens, profiles and generated browser caches. GitHub's tag workflow repeats these checks against the committed source before publishing.

Headless screenshots are generated fixture artifacts in `replacement/verification/`. They were visually inspected; artwork path handling, screen scroll reset, control spacing and cloud clutter were corrected. They are **not** real account/provider screenshots. Initial test failures (collector test double awaiting an event callback, asynchronous QR assertions, and unavailable sandbox browser runtime) were corrected; final runs are the results above.

## Run locally

```powershell
.\replacement\build.ps1 -Test
& 'C:\Users\nutan\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' --test replacement/tests/*.test.mjs
& 'C:\Users\nutan\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' replacement/tests/ui-check.mjs
.\tools\test-features.ps1
```

The UI fixture runner needs Playwright and a headless Edge process. Its current package path uses this workspace's bundled runtime; adjust the path on another development PC. Sandbox restrictions required an approved escalation for that isolated headless process and the loopback API probe. It uses no signed-in profile or microphone.

## Outstanding live matrix

| Journey | Required evidence |
| --- | --- |
| YouTube playback → Home → reopen, repeatedly | Same window, TV route, audible playback continuity, native focus/controller behavior |
| Discovery concurrent with real playback | Neither muting nor closing the real provider tab; actual returned fresh card counts |
| Netflix/Prime native title launches | Account/profile selected, protected playback, native controls and progress; no claim from generated fixtures |
| Network loss / renderer freeze | Home still returns; action timeout and retry visible; actual browser behavior |
| Devices/controllers | Xbox/PS/Switch/generic hardware, correct hints, mapped buttons, battery where reported, driver-supported Bluetooth request and observed state |
| Microphone/music | Real Indian English and Hindi wake/commands, wake latency distribution, loud music false positives/misses, echo/noise mode and CPU fallback |
| Phone/viewer | Real pairing, Unicode text, navigation/playback/volume acknowledgement, restart retention and explicit revocation |
| Mac/Safari screen stream | Entire selected Windows interface plus system audio received, viewer sound enabled, disconnect revocation, protected-content blackouts reported |
| Offline profile migration | Source browser closed, original unchanged, copied sign-ins verified, rollback usable |

The live Computer Use attempt ended with the guard message that Edge's current URL could not be determined confidently enough. Actual window rendering, live audio and physical Home were not verified. The separate fast wake detector also remains an implementation gap. The release retains these limits explicitly.
