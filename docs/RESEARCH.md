# Research notes & design decisions

Notes from building BWOS, recorded so the reasoning behind each choice is preserved.

## 1. Catching the controller's home button on Windows

| Option | Verdict |
|---|---|
| `XInputGetState` (public) | Does **not** report the Guide button; Windows reserves it. |
| **`XInputGetStateEx`**, ordinal **100** in `xinput1_4.dll` | Same struct as `XInputGetState`, but `wButtons & 0x0400` is the Guide button. Undocumented but stable for over a decade; Steam, DS4Windows and x360ce rely on it. **Used.** Works while games are fullscreen because it's polled. |
| Raw Input (HID) with `RIDEV_INPUTSINK` | Receives reports in the background. **Used for PlayStation (DS4/DualSense) and Switch Pro/Joy-Con**; the PS/Home bit sits at known report offsets over USB and Bluetooth. |
| Global hotkey | `RegisterHotKey(Ctrl+Alt+Home)`, a fallback for any other device. |

Conflicts:
- **Steam** also grabs Guide ("Guide Button Focuses Steam"), and Big Picture takes ~3 s to steal focus.
  BWOS holds the foreground for 4 s after a press and logs who stole it; turning the Steam option off is the real fix.
- **Xbox Game Bar** opens on Guide unless "Open Game Bar using this button on a controller" is off.
- The tested 8BitDo Ultimate 2C (VID 2DC8) in XInput mode reports Home as Guide. Its L4/R4 paddles send nothing
  in XInput mode unless mapped on the controller, so volume went to the triggers.

## 2. Reading game libraries without APIs or keys

- **Steam**: `HKCU\Software\Valve\Steam\SteamPath` → `steamapps\libraryfolders.vdf` → `appmanifest_<id>.acf`.
  Playtime and last played come from `userdata\<accountid>\config\localconfig.vdf`, the persona from
  `config\loginusers.vdf`, and the running game live from `HKCU\Software\Valve\Steam\RunningAppID`. Artwork is in
  `appcache\librarycache\<id>\` (newer clients use hash-named sub-folders), with a CDN fallback at
  `shared.steamstatic.com/store_item_assets/steam/apps/<id>/…`. Launch with `steam://rungameid/<id>`.
- **Epic**: JSON manifests in `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`; launch with
  `com.epicgames.launcher://apps/<ns>%3A<item>%3A<app>?action=launch&silent=true`. An empty `MainGameAppName`
  means a base game; only a *different* non-empty value marks DLC.
- **GOG**: `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<id>`.
- **Ubisoft Connect**: `HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\<id>\InstallDir`. The registered name
  is often a code name ("AFOP"), so the title comes from the game exe's `ProductName`. Launch with `uplay://launch/<id>/0`.
- **Artwork for non-Steam games**: the public Steam store search (`store.steampowered.com/api/storesearch`),
  cached in `data/artmap.json`. `artOverrides` covers delisted titles (e.g. Rocket League → 252950).

## 3. Architecture

Constraints on the development PC: no Node, Git or .NET SDK; only **.NET Framework 4.8.1**, whose `csc.exe` (C# 5)
ships with Windows, plus Microsoft Edge.

| Option | Verdict |
|---|---|
| Electron / Tauri | Great UI, but needs toolchains and ships a 100+ MB runtime. |
| WPF / WinUI 3 | Native, but needs an SDK (WinUI), or means hand-building glass effects in XAML with C# 5. |
| **Tray host in C# 5 + HTML/CSS UI in Edge `--app` mode** | Builds with nothing but Windows. Chromium gives GPU-composited frosted glass (`backdrop-filter`), the Gamepad API for any controller, and smooth transforms. **Chosen.** |

Streaming services open **inside the same fullscreen window** (top-level navigation), not in a second browser
window. That keeps cookies first-party, so sign-ins and DRM work, and it feels like one app. A bundled MV3
extension (`extensions/bwos-tv`) is loaded into that window's dedicated profile:

- **`remote.js`: controller layer for any site.** BWOS.exe turns the pad into *real* key presses while a site is
  in front (trusted events, so sites and players accept them). The script turns those keys into spatial navigation:
  - It collects anything selectable: links, buttons and ARIA roles, plus the outermost element with
    `cursor: pointer`, because many streaming sites build posters as plain clickable `<div>`s.
  - It penalises pinned headers and banners, and presses a carousel's own ◀ ▶ arrows when a row uses CSS
    transforms instead of scrolling.
- **Home can never be trapped.** Home is sent both as F24 (handled by the page script) and as `Ctrl+Shift+Home`,
  an extension *command* handled by the background worker. Pages and ad iframes can't swallow browser commands.
  Scripts in sub-frames forward Back/Menu to the top frame, because video-ad iframes grab keyboard focus.
- **Auto full screen**: in-page players (Einthusan, Tubi, Plex) are pinned to fill the window when the film
  starts. The whole player container is used, because Einthusan plays its ad as a second video beside the film,
  and pinning only the film's `<video>` left the ad running hidden (which looked like a freeze).
- **One-press play**: titles opened from For You carry `#bwos-play`; the script presses the site's Play button
  (scored by label, id/class and position).
- **Pop-up guard** (`popups.js`, page world): `window.open` and new-tab links to other sites are dropped (these are
  ad tabs); sign-in providers are allow-listed.
- **YouTube TV**: `rules.json` sets a TV user agent on `www.youtube.com` requests (declarativeNetRequest) and
  `youtube.js` mirrors it in `navigator`, so `youtube.com/tv` serves the leanback UI without a browser-wide UA switch.

Other details that mattered:
- Each Edge instance needs its own `--user-data-dir`; otherwise an already-running Edge swallows the flags. A
  window left over from a previous BWOS.exe run is closed at start-up so new flags and extension versions apply.
- A background tray app may not steal focus: BWOS injects an empty mouse input (the PowerToys trick) and attaches
  to the foreground thread before `SetForegroundWindow`.
- Exes built by the in-box compiler run in legacy TLS mode, so TLS 1.2/1.3 must be enabled explicitly.
- Some CDNs (Hotstar manifests, Einthusan pages) gzip responses regardless of `Accept-Encoding`, hence a
  decompressing `WebClient`. Einthusan also rate-limits bursts, so list pages are fetched slowly and cached for 6 h.
- Chromium keeps only the last `--disable-features` switch, so everything goes in one flag. Edge 154 honours
  `--load-extension` (tested); branded Chrome 137+ does not.
- The UI ↔ host API is `127.0.0.1` only, with a persistent per-install token and a Host-header check (so websites
  can't trigger launches and DNS rebinding is blocked). The token persists so an open window survives host restarts.

## 4. For You: recommendations that grow

- **Catalogue**: JustWatch's public GraphQL endpoint (no key) answers "what is popular / similar, on which
  service, in which country", with posters, backdrops and deep links. Queries are limited to the provider codes
  of the apps you keep visible; `excludeGenres: ["rly"]` removes reality TV, and
  `productionCountries: ["IN"]` + `excludeProductionCountries` gives genuinely Indian titles.
- **Per-app region**: apps can carry their own catalogue country (e.g. Indian Netflix/Prime accounts vs US-only
  Tubi/Pluto). Rows are queried per country and merged, interleaving the services.
- **Einthusan** isn't in JustWatch; its own "popular (30 days)" and "recent" list pages are parsed.
- **Watch tracking**: the extension reports title/episode/artwork/position every 15 s (Netflix and Prime from their
  player DOM, otherwise Media Session metadata, then `og:` tags). Entries are keyed by *show*, not URL, so Up Next
  follows a series. New titles are matched to JustWatch once for artwork and to seed "Because you watched …" rows
  (`similarTitlesV2`). Sites that don't resume by themselves are seeked to the saved position.
- **Service icons**: each site's web-app manifest (largest icon), then `apple-touch-icon`, then a high-resolution
  favicon, requiring ≥ 128 px where possible; the user's config can override.

## 5. Sign-in and typing

- There is no cross-service single sign-on. One persistent BWOS profile means signing in once per service.
- QR/device-code sign-in exists only in the services' *TV apps* (e.g. youtube.com/activate, which works because
  BWOS runs YouTube's TV client); Netflix and Prime websites don't offer it.
- **Phone keyboard** instead: a tiny HTTP server on the LAN (started on first use; Windows asks once to allow it)
  serves a typing page behind a random one-time code encoded in a QR code. The QR generator is written in-house
  (byte mode, EC level M, v1–6) and was verified by decoding. Text is typed with `SendInput` Unicode events,
  only while the BWOS window is in front.

## 6. Design language & sound

- tvOS-style frosted glass: translucent fills with `blur(42px) saturate(185%)` and a lit 1px top edge. Focus is
  a clear scale-and-lift with a white ring (readable from the couch), a directional parallax nudge, and an idle
  tilt with moving glare, animated through registered custom properties (`@property`), so no per-frame JS.
  Sizing is `1rem = 1/120 of the screen width`, identical on a 720p projector and a 4K TV.
- Service tiles are tvOS-style app icons: the official icon on its brand colour, with the name only on focus.
- **Ambient music** is generative. Bell-like notes (a sine plus a faster-fading octave partial) random-walk
  through a pentatonic set matched to the current chord, over a faint pad and a synthesized convolution reverb.
  The chord changes every 30–60 s and note density drifts, so it never repeats. A high-pass keeps it free of
  rumble. An earlier drone design with filter sweeps sounded like a jet engine and was replaced.
