# Pending work (updated 2026-10-26)

Items marked done below (7, 8, 9) are committed on `main`. The repo is
`github.com/keyurpatel22790/HaloBar` (private); push only when asked.

## To build, in this order

1. **Hotkey editor** *(started; no code yet)*
   - Move the fixed hotkey table in `Program.cs` into `settings.Hotkeys` (action + gesture text like `Ctrl+Alt+I`).
   - Add a "Keys" section to the Settings panel: click a shortcut, then press the new keys.
   - Backspace clears a shortcut and Esc cancels.
   - Unregister all hotkeys while capturing, and handle `WM_SYSKEYDOWN` for Alt combinations.
   - Re-register after each change and show which shortcuts are taken by another app.
   - Add actions for screen recording and launcher slots 1–6.

2. **Codex and Gemini hooks**
   - Codex: add `notify = [...]` to `~/.codex/config.toml`. Codex appends the event JSON as the last argument, so the hook posts it with `curl -d` to `/agent/codex`.
   - Codex: also watch `~/.codex/sessions/**/rollout-*.jsonl` growing, to show it as working.
   - Gemini: add hooks to `~/.gemini/settings.json`, piping stdin JSON to `/agent/gemini`. Check the hook format first.
   - Same rules as Claude: a backup file, a marker, and Disconnect removes exactly what was added.
   - Agents panel: one Connect/Disconnect chip per installed agent.

3. **Web panel**
   - The island panel shows pinned sites (YouTube Music, Spotify, ChatGPT, Claude, plus custom ones).
   - Clicking a site opens a drawer window under the island hosting WebView2: a normal HWND controller, the `Microsoft.Web.WebView2` package, and a profile kept in `%AppData%\HaloBar\WebView2` so logins persist.
   - Hiding the drawer keeps music playing.
   - Add `/web?site=` to the local API.

4. **Desktop widgets**
   - Pin any Home tile to the desktop. Each pinned tile is its own composition-surface window kept at the bottom of the z-order, draggable, with a right-click menu to unpin.
   - Store pinned tiles in `settings.DesktopWidgets`.
   - Pull the tile drawing out of `HomePanel` into a public `DrawTile`.

5. **Screen recorder**
   - Capture the monitor with Windows.Graphics.Capture, feed the frames to a MediaStreamSource, and encode with MediaTranscoder to MP4 (H.264).
   - Optional microphone through WASAPI as PCM.
   - Use a separate D3D11 device from the one drawing the island.
   - Save to `Videos\HaloBar`, put the file on the shelf, show a REC activity, and add a hotkey.

6. **Languages**
   - Wrap every visible string in a lookup keyed by the English text.
   - Add 20 translation files (ar, cs, de, es, fr, hi, id, it, ja, ko, nl, pl, pt-BR, ru, th, tr, uk, vi, zh-Hans, zh-Hant).
   - Add a "Language" setting, defaulting to the Windows language.
   - Text shaping for Arabic, Hindi, Gujarati and Thai is already in (SkiaSharp.HarfBuzz, 2026-10-26).

7. **Windows banner control** *(done 2026-10-26)*
   - Settings › Island › "Turn off Windows banners" (off by default). While it's on, each app whose notification HaloBar shows gets `ShowBanner=0` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\<app id>`; switching it on also covers apps already in the island's notification list.
   - HaloBar remembers what each value was before; switching it off puts those back and leaves apps you changed yourself alone.
   - Windows' own alerts (`Windows.SystemToast.*`: low battery, USB…) keep their banners. Desktop apps whose id is a path (PowerShell's, for one) are nested keys, as Windows keeps them; checked by Windows writing its own counters there.
   - Checked live: Windows obeys the change straight away, no sign-out. The first notification from an app HaloBar hasn't seen yet still shows one banner.

8. **Icon-only tab bar** *(done 2026-10-26: a soft rounded square slides to the open tab, no ring; name in a hover tooltip; icons keep fixed spacing on every panel)*
   - The open island's tabs should show only their icons: no name next to the selected tab, and no panel name in place of the clock.
   - Mark the selected tab with a highlight around its icon instead.
   - Hovering a tab can still show its name, as a small tooltip.

9. **Home tab rebuild** *(done 2026-10-26)*
   - Pages of four slots, with dots and wheel paging; tiles can be wide (two slots). Edit: click a name to add it, again to make it wide or narrow, again to remove it.
   - 27 tiles: Date, Weather, Air & sun, Stats, Focus timer, World clock, To-do, Battery, Next meeting, Month, Screen time, Storage, Network, This PC, Note, Countdown, Counter, Stopwatch, Calculator, Unit converter, Translator, Dictionary, Currency, Markets, Git, Docker, Photos.
   - Break reminder: a pill message after 50 minutes at the screen without a 5-minute break (set in Edit).
   - `/panel?name=home:2` opens page 2; `home:edit` opens the editor.
   - Fixed in passing: Indian-script text (Hindi, Gujarati…) drew as empty boxes, because the font fallback skipped everything below U+2000.
   - Not yet checked on the island itself: typing into the translator, calculator and dictionary (the web look-ups and Gujarati drawing were checked separately).

## After everything is built

- **Publishing for others:** make a self-contained installer, so friends don't need .NET installed. It should add a Start-menu shortcut, an Apps entry with an uninstaller, and an optional start-with-Windows choice.
- Decide on code signing (SmartScreen warns about unsigned apps) and whether the repo goes public or friends get invited.

## Still to check on real hardware

- Acrylic, Frost and Candy glass surfaces
- Brightness, Bluetooth, Night light and Wi-Fi switching
- OBS, the camera mirror, drag-and-drop onto the island, and the screenshot hotkey
- Phone share: it opens a port on your Wi-Fi and Windows asks about the firewall, and your network is currently Public
- Connect Claude Code: it edits `~/.claude/settings.json`
- More than one monitor

## Not planned yet

- Glass lens surface
- FPS counter (needs admin rights for ETW)
- YouTube live chat
- Price alerts
- Choosing music sources
- Dragging files out of the shelf
- Temperatures in the stats tile
