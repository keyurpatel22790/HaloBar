# Pending work (updated 2026-10-30, afternoon)

All nine items are built and committed on `main`. Everything up to 2026-10-31 is committed (280caac, not pushed).
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

## Done 2026-10-30 (not committed)

- **Installer:** `installer\build.ps1` publishes a self-contained HaloBar (no .NET needed) and builds `installer\out\HaloBar-Setup-0.5.0.exe` (43 MB) with Inno Setup 6 (`installer\HaloBar.iss`). Per-user install to `%LOCALAPPDATA%\Programs\HaloBar`, Start menu entry, Apps entry with uninstaller, optional start-with-Windows and desktop shortcut; closes a running HaloBar first. Tested: silent install over the dev copy, Apps entry, Run value, Start menu, app starts as 0.5.0.
- **App icon:** drawn in code (`IconArt`, `HaloBar.exe --write-icon <path>`), saved as `Assets\HaloBar.ico` and used for the exe and installer. Version is 0.5.0.
- **Temperatures:** CPU (ACPI thermal zone counter) and GPU (NVIDIA NVML) beside the percentages on the Stats tile and the game strip. No admin needed. Checked: 54° / 48°.
- **Music sources:** click the app name on the music card to show only that app's music (again for any app). Settings › Music sources has a switch per app seen playing, to keep e.g. a browser's videos off the island.
- **Price alerts:** the bell on the Markets tile takes "BTC > 70000; AAPL < 180". Prices for alerts are checked every five minutes even with the tile off screen; crossing a level shows a notice once, again only after it crosses back.
- **Drag out of the shelf:** press on a file card and drag it into Explorer, a mail or a chat (the shell's own drag; files are copied, not moved). First try froze the island: the drag ran inside the frame loop. Now it starts from a posted action, a timer keeps frames drawing during the drag, and the island ignores its own drag as a drop. Each step logs `Drag out:` lines. Checked by hand 2026-10-30: works.
- **Drop onto the island (fixed 2026-10-30, checked by hand):** Explorer's OLE calls never reached the island while a file hovered, so the island now takes files via `DragAcceptFiles`/`WM_DROPFILES`, and shows the shelf as a drop zone while the button is held over it (pressed elsewhere). Shelf thumbnails were upside down (shell thumbnails are labelled bottom-up but stored top-down); they are now copied out with `GetDIBits`.
- **Moving a desktop widget (fixed 2026-10-30):** it vanished while dragged with the desktop in front, because each move sent it to the bottom (behind the raised desktop); moves now leave the z-order alone. Checked by hand.
- **Pinning widgets and Home › Edit › Done (fixed 2026-10-30, checked by hand):** a widget pinned while the desktop was in front started behind it; new widgets now take the current "desktop in front" state. Done sometimes didn't respond because editor chips scrolled up under the tab bar still took clicks; panels' click areas are now clipped to the panel body (`Ui.HitClip`).
- **Microphone in recordings:** Settings › "Microphone in screen recordings" mixes the mic with the PC's sound (resampled to one rate, padded with silence). Checked: a 5 s recording with both has a continuous AAC track; the voice level itself is untested (the room was silent).
- **Widgets and Win+D:** while the desktop is in front, widgets rise above it, and drop back when an app comes forward (a foreground-change hook). Not tried by hand (the PC was locked).
- **Glass lens surface:** a clearer glass than Acrylic with a light-catching rim and a sheen across the top (Style › Glass lens).
- **YouTube live chat:** the Chat panel takes a YouTube link, a video id link or an @handle as well as a Twitch name. No key needed: the stream's chat page and YouTube's own chat endpoint. Checked against a 24/7 stream: the live video and chat chain are found.
- **Do not disturb (new):** notifications, chat pop-ups and agent pings wait in the Notifications list instead of popping up. Switch on Controls ("Quiet"), the tray, Settings, or a hotkey (none by default); on automatically during a focus-timer session ("Quiet during focus").
- **Small fixes:** the Controls "Microphone" switch is "Mic" so seven switches fit; new strings translated into all 21 languages.
- **Not feasible:** an FPS counter for games needs ETW tracing, which Windows allows only with admin rights.

## NotchPixel settings parity (2026-10-30 evening, not committed)

Every option NotchPixel 0.5.88 saves that HaloBar lacked, compared from its settings.json and the descriptions in its assemblies:
- **Look:** Glass blur (thinner or thicker tint over the blurred desktop; Windows' blur strength itself is fixed), Open size (card 90–120%; the island window grew to 720×300 DIPs), Motion (Fluid / Balanced / Battery saver caps animation at 60 fps).
- **Accessibility:** Text size in Settings, Higher contrast text, Reduce motion (springs never overshoot) — each "Same as Windows" by default, read from Windows' own settings; Keep notices longer (×2).
- **Island:** song title in the closed island, Opens on Main or Last tab (remembered across restarts), 12/24-hour clock everywhere, Files and Controls tabs can be turned off, Reminder sound (repeat every 15 s for a minute / once / off), Night light warning for colour-work apps (click turns it off), Local API on/off.
- **Stats and game strip:** MB/s or Mb/s, °C or °F, strip "Show it everywhere" (hover opens the card), each strip reading on/off, small usage bars.
- **Clipboard:** hours to keep entries, list view, copied text on the phone page on/off.
- **Chat overlay:** the chat in its own always-on-top window (drag; right-click for size or close) with background, opacity, text colour and size.
- **Files and exports:** photo/video details (resolution, length, size) when dropped on the shelf; export folders watched for finished renders (notice + shelf).
- **Privacy dots:** a switch per app seen using the camera or microphone. **Web extensions:** unpacked extension folders loaded into the web drawer.
- Not built: an FPS reading on the strip (needs admin ETW, as before).
- Also fixed: widgets stayed lifted over apps when a foreground change was missed (now re-checked every half second against the real window state); widgets hidden behind windows aren't redrawn; snapshots no longer redraw every widget.
- Still to try by hand: the chat overlay with a live chat, an export folder with a real render, extensions in the web drawer, Battery saver motion on battery.

## Performance and widget fixes (2026-10-30 night, not committed)

- **Lag:** the shared D3D device allowed one queued frame (MaximumFrameLatency 1), so with an island per monitor plus widgets every Present waited ~16 ms. Now 3 queued frames, 3 swap-chain buffers, frames go through a WriteDiscard upload texture (no waiting on the buffer the screen holds), and the device is made on the GPU that drives the main screen. Measured with six open/close cycles: slow frames 781 of 1304 → 86 of 836 (10%). A "skip the present when busy and redraw" attempt made a tight loop (1798 of 1798 slow) and was removed. Idle frames (once a second) still take ~20 ms, likely the NVIDIA GPU waking; drawing with Skia on the GPU would be the next step.
- Widgets no longer redraw whenever anything on the island changes; at most four a frame, none while the island animates, none while covered. The island window is 640×260 again unless Open size is above 100%. A "Frames:" line in the log each minute there were slow frames (island draw vs GPU, slowest widgets).
- Reduce motion (on by default here because Windows' animation effects are off) is quicker, not slower; the slim-island hover wait is +100 ms (was +250).
- Widgets sit just above the desktop instead of HWND_BOTTOM, which put a clicked or typed-into widget under the desktop ("last widget hides").
- Text boxes: Shift-selection, Ctrl+A/C/X/V/Z, Ctrl+arrows and Ctrl+Backspace/Delete by word; long text shrinks before it scrolls (calculator).
- Markets: each symbol is fetched alone (one bad symbol used to blank them all) and unknown ones are looked up on Yahoo ("JPL" → JPL.NS, Jio Platforms, listed 2026-10-28).
- Translate and dictionary look up text typed while an earlier look-up was running; the dictionary suggests a spelling ("defination" → "definition", Datamuse). This PC fits a narrow tile.

- **Glass lens is real glass now:** the screen behind the island is read (Desktop Duplication where the GPU offers it, as on the external monitor; otherwise a GDI copy on its own thread, 30/s open and 10/s closed, as on this laptop's panel, where duplication returns DXGI_ERROR_UNSUPPORTED), shrunk to a third and blurred (Glass blur), and drawn with a lens shader of HaloBar's own on the rim (magnifying bend, slight colour fringe, light) plus a thin tint. While it's on, the island is kept out of screenshots and recordings (it would otherwise see itself), as NotchPixel's glass is.
- **Paste:** files or folders copied in Explorer paste as their paths; the Git tile says when it was given a web link or a missing folder.

## Settings window (built 2026-10-31, not committed)

A separate window (SettingsWindow, built in code, no XAML), sidebar of pages with a "Search settings" box: General, Appearance, Island/Behaviour, Music (lyrics), Widgets, Notifications, Shelf/Files, Hotkeys, Gaming, AI agents (+ custom agents), Chat/Streaming, Web, Tools/Recording, Accessibility, About. Each option has a one-line description; there's an app picker for per-app choices. HaloBar's settings were only a scrolling list inside the island.

HaloBar now has its own (UI/SettingsWindow.cs): a normal resizable window (dark title bar, remembers its page; closing hides it) drawn with the island's colours and fonts. Sidebar with a search box (typing anywhere searches; Esc clears) and 12 pages: General, Appearance (surface, accent, album colours, look and place), Accessibility, Read-outs, Music, Stats and gaming, Clipboard and files, Chat, Web, Privacy, Shortcuts, About (version, settings file, log, folder). Every option shows its explanation under it. The rows are the island Settings panel's own, so both always agree; shortcut recording and folder pickers work in the window too. Opens from the tray (Settings…), the island's Settings tab ("Open window"), and the local API (/settings?page=Music); /snapshot?panel=settings-window saves a picture of it.

- **Text colour (2026-10-31):** Appearance › Text colour: Auto (the surface's own; on Glass lens dark over a bright background and light over a dark one, measured from the picture the lens reads, with a gap so it doesn't flicker), White, Black, Warm white, Soft grey. Text shadow: Auto (see-through surfaces), On, Off. Widget cards turn light when the text turns dark. The Settings window sits on Windows' Acrylic glass (DWMWA_SYSTEMBACKDROP_TYPE, frame extended over the client) with a see-through tint.

- **Sliders (2026-10-31):** Size, Corners, Distance from top, Open size, Glass blur, Open delay, Notice time, chat opacity and text size are dragged on a slider (window and island) in steps of 1, saved when let go; size changes apply when let go.
- **Hover flicker in the Settings window:** the pointer is read each frame instead of trusting WM_MOUSELEAVE (the glass frame over the client sent leave notices while inside); auto text colour only switches after the background stays on the other side for 1.2 s. Not confirmed by hand yet.
- **Liquid Glass look:** a steeper lens rim (bigger bend, colour fringe), a specular highlight from the top left and a faint glint opposite, a darker bevel facing away, a more vivid backdrop (saturation 1.3), almost no tint, a soft shadow under the glass; Glass blur defaults to 35.

- **Settings window glass (2026-10-31):** Windows' Acrylic was faint and off when unfocused, so the window now draws its own glass: the screen behind it copied 10 times a second on a background thread (skipped when nothing changed, stopped when the window is hidden), blurred, under a tint. The window is kept out of screen captures meanwhile. Pictures handed between the copy thread and drawing are now only freed once replaced (a crash risk before).

- **Glass follows instantly (2026-10-31):** the island copies its whole window area, not just the island, so opening, closing and resizing use a picture that's already there; a moved window (Position) wakes the copier at once instead of at its next turn. 30 copies a second while open, 10 closed; brightness for auto text is measured under the island only.

- **Glass redraws when the picture changes (2026-10-31):** the closed island used to redraw about once a second, so a new glass picture waited up to a second; now a new picture draws a frame at once. Open: the whole window area, 60 copies a second; closed: a band around the pill, 20 a second; Battery saver (or Balanced on battery) copies three times less often. Brightness for auto text is measured on the copier's thread. About 16% of a core over this PC's animated wallpaper on two screens; far less over a still background.
- **Recording:** while HaloBar records, the island uses Windows' blur instead of glass so it shows in the video (glass keeps it out of captures). A finished recording is also copied to the clipboard ("Recording saved and copied").

- **HDR screens (2026-10-31):** the external monitor's desktop duplicates as R16G16B16A16_Float (scRGB); it was read as 8-bit BGRA, so the glass there was a whitish smear with lines. Now read in its own format, every third pixel turned into sRGB (that is the shrink), then blurred; other unknown formats fall back to Windows' blur. /snapshot?panel=glass:N saves screen N's glass picture and logs what it reads.

- **Pet (2026-10-31, removed the same night, to discuss later; code parked in docs/parked/pet, not built):** a pixel cat (ginger, grey, black, snowy; Appearance › Pet, or Off) beside the closed pill: blinks and swishes its tail, strolls, bobs to music, sits still in focus time, jumps at a notification, hearts when clicked, sleeps (Zzz) after 3 idle minutes or 1–6 am. Over a slim line (maximized windows) it shows smaller, just under the line's end, keeping to a short stretch; hidden over a game strip, the open card, or when the island is hidden until hover. Pet tile (Home or desktop): a room, five hearts of happiness (saved, −6 an hour), Feed and Play. All drawn in code (UI/Pet.cs, UI/Tiles/PetTile.cs).
- **Desktop widgets:** Windows' blur behind each card (AcrylicBackdrop per widget window), a lighter tint, a sheen across the top, a rim that fades down; radius 20.
- **Notifications:** a compact pill 300–420 wide and 54 tall: icon, bold title with the app name small on the right, one line of message.
- **Web drawer:** a bar across the top with the site's name, Open in browser, Close page (stops it), and ✕ (hide).

- **Ad blocking, phase 1 (2026-10-31):** Platform/AdBlocker.cs in every drawer page: requests to a short list of ad and tracker hosts and to YouTube's ad paths are refused; a script run before the page's own removes adPlacements / playerAds / adSlots / adBreak params from YouTube's player data (JSON.parse, Response.json, ytInitialPlayerResponse); an ad that still plays is muted, played at 16×, jumped to its end and skipped. Settings › Web › Web drawer: Block ads, Skip ads that get through (for pages opened afterwards). Checked: blocking is active when YT Music opens; whether ads between songs are gone needs listening. Plan for phases 2–4 in docs/ad-blocking-plan.md.
- **Keep open (web drawer):** a button in its bar; kept open (now the default, WebDrawerKeepOpen), the drawer doesn't hide when you click elsewhere, isn't always on top, drags by its bar, resizes by its edges, and reopens where it was left (WebDrawerKeepOpen, WebDrawerBounds).
- **Drawer as a window (2026-10-31):** kept open, the drawer is an app window (no WS_EX_TOOLWINDOW, WS_EX_APPWINDOW): in Alt+Tab and the taskbar, titled "<site> · HaloBar" with HaloBar's icon. The bar has Back, Forward (dim when there's nowhere to go), Reload and Home (the site's start page) on the left.
- **A window per site (2026-10-31):** WebDrawer is now a manager of SiteWindow (one NativeWindow + WebView2 per site, sharing one profile and environment). Staying open, sites open side by side, each in Alt+Tab and the taskbar, with its own saved place (WebWindowBounds by address; a new one opens a little down and right of the last). With "Hides on click" a site still drops down under the island and the others hide. "Stays open" switched in any window's bar applies to all. Checked: YT Music and YouTube open as two visible app windows, and switching between them (as Alt+Tab does) brings each to the front. Opening a site from the island brings its window to the screen being worked on (keeping its size); Alt+Tab leaves windows where they are.
- Open: the web extensions list fails in the drawer ("Extensions: Unexpected HRESULT" from GetBrowserExtensionsAsync); look into it with the extension test in ad-blocking phase 4.

## Next ideas

- Sign the installer (SmartScreen warns about unsigned apps), and decide whether the repo goes public or friends get invited; then a "check for updates" from GitHub releases becomes possible.
- Arabic: mirror the layout right to left (text is already shaped and ordered).
- Choose a window or area to record, and turn off Windows' yellow capture border (needs a newer Windows SDK target).
- A small "quiet" mark on the pill while Do not disturb is on.

## Still to check by hand

- Win+D with widgets; changing a shortcut in Settings › Keys; signing in to a site in the web drawer; the microphone level in a recording.
- Acrylic, Frost, Candy and Glass lens over a real wallpaper.
- Brightness, Bluetooth, Night light and Wi-Fi switching.
- OBS, the camera mirror, and the screenshot hotkey.
- Phone share: it opens a port on your Wi-Fi and Windows asks about the firewall, and your network is currently Public.
- More than one monitor.
