# BWOS 1.8.1

YouTube video cards now open YouTube TV instead of a second embedded player. Recommendations, native search results, saved videos, options and Continue Watching share the same TV playback route.

Opening a service during recommendation collection previously could reuse the muted temporary discovery tab. That tab rejects Home commands and is removed after collection, causing silent or interrupted playback and failed Home navigation. Live app selection now excludes temporary discovery tabs. Explicitly reopening a player also repairs a tab mute set by BWOS itself, while preserving deliberate user or other-extension mutes.

Regression checks reproduce concurrent discovery and playback, verify Home retains the live player, and ensure discovery cleanup cannot remove it. TV URL conversion preserves validated video and playlist identifiers. Local packaging excludes Chromium-generated extension caches.

This is a focused YouTube correction; the proposed full application redesign is not part of this update. YouTube TV remains a browser compatibility integration, and provider playback errors or device/audio settings can still require separate diagnosis.
