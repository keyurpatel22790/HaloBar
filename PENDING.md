# Pending work (updated 2026-10-30)

All nine items are built and committed on `main`. Publishing (below) is next, when you decide.
The repo is `github.com/keyurpatel22790/HaloBar` (private); push only when asked.

## To build, in this order

1. **Hotkey editor** *(done 2026-10-29, not committed)*
   - Shortcuts live in `settings.Hotkeys` (action id → `Ctrl+Alt+I`; no entry = default, `""` = none). `HotkeyService` registers them and notes any another app holds.
   - Settings › Keys: click a shortcut, press the new keys. Backspace clears, Esc cancels. All shortcuts are let go while choosing (Alt combinations arrive as `WM_SYSKEYDOWN`).
   - A key given to one action is taken from any other action that had it. "Back to the usual shortcuts" resets all.
   - New actions: next and previous track, launcher slots 1–6 (no keys by default). The tray menu and hints show the current keys.
   - Screen recording has its own action too (Ctrl+Alt+V, from item 5).
   - Not yet tried by hand: clicking a row and pressing keys on the island.

2. **Codex and Gemini hooks** *(done 2026-10-29, not committed)*
   - Agents › Connect lists Claude Code, Codex and Gemini CLI, each with Connect/Disconnect.
   - Codex: hooks go in `~/.codex/hooks.json` (not `notify`: the Codex app already uses that for computer use, and Codex allows only one). They carry a `commandWindows` that uses `curl.exe`. **Codex runs a hook only after you trust it with `/hooks`.**
   - Gemini CLI: hooks in `~/.gemini/settings.json` with Gemini's own event names; timeouts are in milliseconds.
   - All three post to `/agent/<name>`, which answers `{}` so hook output is valid JSON. Backups and Disconnect work as for Claude.
   - Checked live with test events for Codex and Gemini, and with this Claude Code session. Codex and Gemini themselves aren't connected yet (that edits their settings, so it's your call).
   - Sessions silent for two hours now fade, so a crashed agent doesn't stay "Working".

3. **Web panel** *(done 2026-10-29, not committed)*
   - A "Web" tab with pinned sites: YT Music, Spotify, ChatGPT, Claude, Gemini, WhatsApp by default. "+" adds one ("Notion notion.so"); Edit removes sites or closes an open page.
   - Clicking a site opens it in a drawer under the island (WebView2 1.0.4258.31, profile in `%AppData%\HaloBar\WebView2` so logins stay). Clicking it again, clicking elsewhere or Esc hides it.
   - Each site keeps its own page while hidden (green dot), so music keeps playing.
   - Icons come from the site itself (touch icon, favicon, or the page's icon links), cached in `%AppData%\HaloBar\icons`. ChatGPT's blocks plain requests, so it shows a letter.
   - Ctrl+Alt+W shows or hides the drawer; `/web?site=ChatGPT` (or an address, or `none`) in the local API.
   - Checked: the panel, icons, and ChatGPT opening in the drawer via the API. Not yet checked by hand: hiding on click-away and Esc, whether YT Music shows on the music card, and signing in.

4. **Desktop widgets** *(done 2026-10-29, not committed)*
   - Home › Edit › Desktop: click a tile to pin it to the desktop, again for wide, again to unpin. Any of the 27 tiles works, including typing into the calculator or translator.
   - Each widget is its own composition-surface window (`WidgetWindow`), kept at the bottom of the window stack, drawn with the same tile code on a card in the island's colours.
   - Drag a widget anywhere (its position is saved in `settings.DesktopWidgets`); right-click for wide/narrow or "Remove from desktop". New ones line up on the right of the main screen.
   - `/snapshot?panel=widget:date` saves a widget's picture; `home:desktop` opens the editor in Desktop mode.
   - Pinned for you to try: Date and Weather (settings backed up as `settings.json.before-widgets`).
   - Not yet checked by hand: dragging, the right-click menu, typing into a widget, and what Win+D (show desktop) does to them.

5. **Screen recorder** *(done 2026-10-29, not committed)*
   - Records the screen the pointer is on to `Videos\HaloBar\HaloBar <date time>.mp4`: H.264 at 30 fps and the screen's size, plus AAC 48 kHz stereo of what the PC plays.
   - `ScreenRecorder`: Windows.Graphics.Capture on its own D3D11 device, frames copied to memory and written with Media Foundation's sink writer (`Vortice.MediaFoundation`; hardware encoder when available). Silence is filled in while nothing plays, so sound stays in step.
   - Start/stop: Ctrl+Alt+V, the Record switch on Controls, the tray menu, or `/record?action=start|stop|toggle`. A red "Recording" timer shows on the pill; the saved file goes on the shelf.
   - Settings › Island › "Sound in screen recordings" turns sound off.
   - Checked: a 5-second test recording plays as 1920×1080 H.264 + AAC, the right way up, correct colours.
   - Not built: microphone (mixing it with system sound), choosing a window or area, and turning off Windows' yellow capture border (needs a newer Windows SDK target).

6. **Languages** *(done 2026-10-30)*
   - 21 languages besides English: ar, cs, de, es, fr, gu, hi, id, it, ja, ko, nl, pl, pt-BR, ru, th, tr, uk, vi, zh-Hans, zh-Hant. Settings › Island › Language (default: Windows language).
   - Text is translated where it's drawn: `Lang.T` in `Ui.Text`/`Wrapped`/`Measure` and the tray menu, keyed by the English text. Text with numbers or names in it matches templates like `{0} days to go`. Dates and numbers follow the language's culture.
   - `tools/extract_strings.py` collects the English text into `Languages/en.txt`; `tools/translate.py` machine-translates only new strings and keeps everything already in a file, so hand fixes stay.
   - Translations were machine-made, then corrected by context: brand, key and protocol names kept as written, and "island"/"pill" written as each language's Dynamic Island term (灵动岛, 動態島, "Dynamic Island", Island, आइलैंड…). A full review covered gu, hi, ar, cs, de, es, fr, id, it, ja, ko, nl, pl, pt-BR; a shorter one covered ru, uk, tr, vi, th, zh-Hans, zh-Hant.
   - Checked on the island in Gujarati (Home, Settings, Controls, Agents). Arabic text isn't mirrored right-to-left in the layout.
   - Text that's built by joining pieces in code (not interpolation) stays English; most are converted.

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
