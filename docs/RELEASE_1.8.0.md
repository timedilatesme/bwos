# BWOS 1.8.0

BWOS brings controller-friendly browsing, voice commands, persistent playback and local screen/audio streaming into the Windows console experience.

## What's new

- Stream this PC's selected screen and system audio to a Mac, phone or browser on the same local network through WebRTC. A temporary QR/link pairs one viewer; the viewer includes BWOS navigation, playback, volume and text controls. Stop or disconnect revokes access.
- Free local English/Hindi voice recognition with automatic NVIDIA acceleration, a CPU fallback, Windows echo cancellation/noise suppression, spoken acknowledgements and an assistant overlay. "Hey console" is the default wake phrase for new installs; wake handling and diagnostics have been improved.
- Broader voice commands for apps, games, search, navigation, selection, volume and playback.
- Persistent fullscreen Home navigation and service tabs, with a Now Playing cloud, a desktop playback companion and controller-accessible playback controls.
- A cleaner glass interface with compact app-logo filters, readable titles, artwork fallbacks, balanced recommendation rows and grouped Settings.
- Apps opens original service interfaces; For You collects recommendations and searches supported services. Discovery handles more Netflix, Prime Video and YouTube card formats and preserves fuller feeds when refreshes are incomplete.
- A controller-friendly on-screen search keyboard with physical/phone typing and Unicode text support.
- A Devices view for controllers and paired Bluetooth devices, with driver-supported audio device controls.

## Getting started

Download and extract `BWOS-1.8.0.zip`, then run `BWOS.exe` on Windows. Keep an existing installation's `data` directory to preserve settings and browser sign-ins.

For streaming, open **Settings → Connections → Stream BWOS to another device**, select an entire screen and enable **Share system audio**. Open the temporary link or scan its QR on another device on the same trusted network, then select **Enable sound**. The browser requires screen selection for each session. Voice assets download automatically when voice control is enabled; no paid API key is required.

## Validation and current limits

The release passed 207 host assertions, 8 loopback HTTP assertions and 13 JavaScript test files. Browser verification covered real WebRTC video decoding, generated audio reception, remote controls and session cleanup. Generated speech checks passed on CPU and the GTX 1050 NVIDIA backend. The installed Windows UI was opened and checked directly.

- Live YouTube discovery still returned only two fresh cards on this PC. The original YouTube TV interface remains available; a complete personalized recommendation feed is not guaranteed.
- Wake recognition of the user's voice while music plays remains unverified. Generated GPU speech transcription took approximately 2.4–6 seconds per phrase.
- Actual Mac/Safari streaming and protected playback were not tested. Netflix/Prime DRM can produce a black capture. Streaming requires a reachable trusted LAN, uses local HTTP signaling and encrypted WebRTC media, and has no Internet relay.
- Remote controls target BWOS; this release does not provide arbitrary remote desktop mouse or game input. Provider websites, playback controls and Bluetooth actions depend on service markup and device support.

See `docs/VOICE_AND_DEVICES.md` and `docs/QUALITY_PASS.md` for setup and verification details.
