# BWOS 1.8.2

YouTube keeps its TV interface by default. Its document compatibility rule now
matches video links containing `#`, fixing a startup path that could show the
desktop redirect instead of the TV player. Standard web playback is an optional
fallback in Settings → Connections → YouTube interface. Saved videos,
recommendations, Continue Watching and voice search follow the selected/current
interface. Switching interfaces reuses the existing service tab and preserves its
video/playlist when available.

Home no longer waits behind playback reports or slow player responses. Background
recovery cannot undo an explicit pause, recreate a closed player, or override a
newer navigation. Playback connections receive heartbeats even during continuous
voice updates. Closing active WebSockets no longer mutates a collection while
shutdown enumerates it.

This also includes the October 18 stability repairs: controller reconnection,
settings without AirPlay, retained focus and shelf nodes, coalesced refreshes,
bounded asynchronous requests, atomic settings/history writes, and screen-sharing
cleanup. Focused game/history cards settle instead of floating continuously, and
Home updates watch history even when the game library has not changed.

Verification includes the Windows host/communication suites, 15 JavaScript
regression files, 12 Edge UI scenarios, and live public YouTube videos in TV and
Standard modes through the production UI/extension with Home and pause/resume controls. See
[the verification record](STABILITY_PASS_2026-10-28.md) for scope and limitations.
