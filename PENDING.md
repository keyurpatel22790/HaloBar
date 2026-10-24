# Pending work (paused 2026-10-24)

Nothing below is committed. The last commit is `9248215` (v0.3); everything since (v0.4) is
uncommitted on purpose: commit only after these are built and you've checked them.
Pushing: `github.com/kp22790` doesn't exist; the saved GitHub login is `keyurpatel22790`.
Confirm which account and repo before pushing.

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
   - Shape Arabic, Hindi and Thai text with SkiaSharp.HarfBuzz.

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
