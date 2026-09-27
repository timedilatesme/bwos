# BWOS

A console-style, controller-first home screen for a Windows PC connected to a TV or projector.

Press the **home button on your controller** from anywhere, even in the middle of a game, and BWOS slides in:
your games from **Steam, Epic Games, GOG and Ubisoft Connect**; **Netflix, Prime Video, YouTube** and free
services running *inside* BWOS; a **For You** tab that learns what you watch; and power controls. Everything is
driven with the controller, and the frosted-glass design is inspired by tvOS.

<p align="center"><img src="assets/bwos.png" width="96" alt="BWOS icon"></p>

## Features

**Games**
- **Home button → BWOS, anywhere.** A small tray service watches your controllers in the background:
  - Xbox and XInput pads through the Guide-button API.
  - PlayStation and Switch Pro through raw HID.
  - **Ctrl + Alt + Home** as a keyboard fallback.
  - Pressing Home again jumps back into the running game.
- **One library.** Steam (every library folder, playtime, last played, local artwork), Epic Games, GOG and
  Ubisoft Connect are found automatically. Titles from other stores get artwork from Steam's public store data,
  and anything else (emulators, Xbox app games) can be added in `config.json`.
- **Now playing.** BWOS detects the running game, offers *Resume* and *Quit*, gets out of the way when a game
  starts, and comes back when a game it launched closes.

**Watching**
- **Apps inside BWOS.** Netflix, Prime Video, YouTube, Einthusan, Tubi, Pluto TV and Plex open *inside* the BWOS
  window, in one persistent browser profile, so you sign in to each service once.
- **A controller layer for every site.** The bundled `bwos-tv` extension adds:
  - A TV focus ring you move with the D-pad, including onto posters built as plain clickable `<div>`s.
  - A = select, B = back.
  - Automatic full screen for in-page players.
  - Blocking of pop-up and new-tab ads.
- **One press to play.** Picking a title in For You opens it on the right service and presses its Play button.
- **For You.** Series and films from *your* services (catalogue data from JustWatch, plus Einthusan's own
  lists). It starts with a few clean rows and grows as you watch:
  - **Up Next** follows each show across services, with progress bars, and resumes where you stopped.
  - **"Because you watched …"** rows appear on their own.
  - Reality TV is filtered out.
- **YouTube TV.** YouTube opens in its 10-foot TV interface with ad schedules and promotions stripped out.
- **Type on your phone.** Press **Menu** in any app: a QR code appears, you scan it, and you type sign-ins or
  searches on your phone.

**Feel**
- **Any controller.** Xbox, PlayStation, Switch Pro and generic pads, with matching button prompts (A/B, ✕/○)
  and a Nintendo-style confirm swap. Keyboard and mouse also work.
- **Polished 10-foot UI.** Frosted glass, a focus parallax with moving glare, artwork-tinted colour, and shelves
  that remember your place. A soft, **generative ambient soundtrack** never repeats and fades out for games
  and videos. There's also an idle screensaver.
- **Triggers = volume** whenever BWOS is on screen.

**No dependencies to build.** BWOS compiles with the C# compiler that ships inside Windows (.NET Framework 4.8),
so you don't need an SDK, Node or Visual Studio. The interface runs in Microsoft Edge's app mode, which every
Windows 10/11 PC has.

## Install

1. Download `BWOS-<version>.zip` from Releases (or build it, below) and unzip it anywhere.
2. Run `BWOS.exe`. That's it: BWOS is portable.

To install it properly, with a Start-menu entry and optionally start-with-Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\install.ps1 -Autostart
```

This copies BWOS to `%LOCALAPPDATA%\Programs\BWOS`. Run `install.ps1 -Uninstall` to remove it.

### One-time setup for the home button

Steam and the Xbox Game Bar also listen for the Guide button. Turn them off once:

1. **Steam → Settings → Controller →** turn off **Guide Button Focuses Steam**.
   If Big Picture still opens, also unbind Guide in Steam's *Desktop Layout*.
2. **Windows Settings → Gaming → Game Bar →** turn off **Open Game Bar using this button on a controller**.
3. **BWOS → Settings → Start BWOS with Windows**, so the home button always works.

If you'd rather not change Steam, set **Settings → Open BWOS with** to *View + Menu together* or *Hold View*.

## Controls

| On the BWOS screens | Xbox | PlayStation |
|---|---|---|
| Move | D-pad / left stick | D-pad / left stick |
| Select / play | A | ✕ |
| Back | B | ○ |
| Options (hide, quit game, remove from Up Next…) | Y | △ |
| Switch tab | LB / RB | L1 / R1 |
| Settings | Menu | Options |
| Volume down / up | LT / RT | L2 / R2 |
| Open BWOS / back to the game | Home | PS |

**Inside streaming apps**
- **Navigation:** D-pad moves the focus ring · A selects · B goes back · Home returns to BWOS.
- **Playback:** X play/pause · Y full screen · LT/RT volume.
- **Pointer:** the right stick moves a mouse pointer and R3 clicks.
- **Menu:** type on your phone.
- **Full-screen players** (Einthusan, Tubi, Plex…): ◀ ▶ skip 10 s, and B leaves full screen.

## Build from source

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1          # → dist\BWOS\BWOS.exe
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run     # build and start
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Zip     # also dist\BWOS-<version>.zip (no personal data)
```

`BWOS.exe --background --dev` starts without opening the UI and writes the UI's address to `data\bwos.log`, so you
can open it in any browser with developer tools.

## Configuration

Settings live in `data\config.json` next to `BWOS.exe` (or `%LOCALAPPDATA%\BWOS` if that folder is read-only).
Most are changed from the Settings screen; the file is safe to edit while BWOS is closed.

```jsonc
{
  "trigger": "guide",               // guide | back+start | hold-back
  "controllerBridge": true,         // controller → keyboard/mouse inside streaming apps
  "country": "IN",                  // For You region (blank = Windows region)
  "hidden": ["watch:twitch"],       // hidden games/apps (Y → Hide in the UI)
  "artOverrides": { "epic:Sugar": "252950" },   // borrow a Steam app's artwork for any game
  "customGames": [
    { "name": "RetroArch", "exe": "C:\\RetroArch\\retroarch.exe", "steamAppId": "1118310" },
    { "name": "My Xbox game", "uri": "shell:AppsFolder\\<PackageFamilyName>!App" }   // AppID: Get-StartApps
  ],
  "watch": [                        // the Apps; add, remove or reorder freely
    { "id": "prime", "name": "Prime Video", "section": "stream", "url": "https://www.primevideo.com",
      "jw": "prv", "country": "IN", "hosts": "primevideo.com", "color": "#1a98ff" }
    // section: stream | free | music · jw: JustWatch provider codes · country: per-app catalogue region
    // hosts: extra domains of the service · icon: optional icon URL · langs: Einthusan languages ("hindi,tamil")
  ]
}
```

Other files in `data\`: `watching.json` (Up Next history), `catalog.json` (recommendation cache), `art\` (app
icons), `profiles\shell\` (the BWOS browser profile with your sign-ins), `bwos.log`.

## How it works

```
 controller ─► BWOS.exe (tray, C#) ─────────────────► Edge --app, fullscreen ◄─ the BWOS UI (ui/)
               • home button: XInput Guide + HID       ▲   HTTP + SSE on 127.0.0.1 (token)
               • game libraries, launcher, now playing │
               • For You catalogue + watch tracker ────┘   same window navigates to Netflix, Prime, YouTube TV…
               • input bridge: pad → keys on sites          └─ bwos-tv extension: focus ring, full screen,
               • phone keyboard (LAN, one-time code)            ad-popup guard, progress, one-press play
```

| Path | What |
|---|---|
| `src/BWOS.Host/` | Tray host. `Gamepad.cs` / `HidWatcher.cs` (home button), `Library.cs` + `Vdf.cs` (game discovery), `GameManager.cs`, `ShellManager.cs` (window & focus), `InputBridge.cs`, `Catalog.cs` (For You), `Tracker.cs` (Up Next), `WatchArt.cs` (app icons), `PhoneKeyboard.cs`, `WebServer.cs` + `App.cs` (API) |
| `ui/` | The 10-foot interface: plain HTML/CSS/ES modules, no build step (`focus.js` spatial navigation, `input.js` controllers, `sound.js` UI sounds + ambient music) |
| `extensions/bwos-tv/` | Loaded into the BWOS window: `remote.js` (controller layer), `youtube.js` (TV UA + ad removal), `popups.js` (ad pop-up guard), `qr.js`, `background.js`, `rules.json` |
| `tools/` | `install.ps1`, `make-icon.ps1` |
| `docs/RESEARCH.md` | Research notes and design decisions |

## Privacy & security

- The UI talks to BWOS only on `127.0.0.1`, protected by a per-install token and a Host-header check.
- The phone keyboard listens on your home network only after you first use it. It needs a fresh random code
  (from the QR), expires after 15 minutes, and only types while the BWOS window is in front.
- Watch history and sign-ins stay on your PC (`data\`). Nothing is uploaded. For recommendations, BWOS queries
  JustWatch's public catalogue with the titles you watch.

## Troubleshooting

- **The home button also opens Steam or Game Bar.** Do the one-time setup above.
- **The home button does nothing.** Check that the BWOS tray icon is there. For PlayStation/Switch pads that
  Steam holds exclusively, use *View + Menu* or Ctrl+Alt+Home.
- **The phone can't open the keyboard page.** The phone must be on the same Wi-Fi, and Windows must allow BWOS
  on private networks (it asks the first time).
- **A site asks you to sign in again.** Sign in once *inside* BWOS; its browser profile is separate from your
  everyday Edge.
- **YouTube shows an ad anyway.** YouTube changes often, so update `extensions/bwos-tv/youtube.js`. Blocking ads
  is against YouTube's terms, and YouTube Premium is the supported ad-free option.
- **Logs:** `data\bwos.log`.

## Roadmap ideas

EA app and Xbox (Game Pass) auto-detection · owned-but-not-installed Steam games (Steam Web API key) · a local
media library (Plex/Jellyfin server) · multiple user profiles · MSIX packaging.

## License

[MIT](LICENSE) © 2026 Paras Sharma
