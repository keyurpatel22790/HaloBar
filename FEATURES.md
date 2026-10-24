# Feature parity with NotchPixel

What NotchPixel 0.5.88 does (from its shipped assemblies, settings and strings) and where
HaloBar stands. HaloBar is written from scratch; nothing is copied from NotchPixel.

Legend: ✅ done and checked · ☑️ done, not yet checked on real hardware or data · ⬜ not started

## Island and appearance
| Feature | Status |
|---|---|
| Pill at the top, opens on hover, spring animation | ✅ |
| One island per monitor | ☑️ (only one monitor here) |
| Hide in full screen / hide from captures | ✅ |
| Surfaces: Black, Pure black, Graphite, light themes, Aurora, Album + accents | ✅ |
| Album-colour music card, smooth colour fades | ✅ |
| Acrylic / Frost / Candy glass (see-through, blurred) | ☑️ |
| Glass (refracting lens over the screen) | ⬜ |
| Shape (island / notch), size, corner roundness | ☑️ |
| Position (left / centre / right), distance from top, edge margin | ☑️ |
| Hidden until hover, slim over maximised windows, open delay, animation speed | ☑️ |
| Settings (in the island: Settings panel) | ✅ |
| 21 languages | ⬜ |

## HUDs (quick read-outs in the pill)
| Feature | Status |
|---|---|
| Volume HUD, replaces the Windows pop-up | ✅ |
| Brightness HUD (laptop panel via WMI) | ☑️ |
| Battery: charging, unplugged, low battery, time-left estimate | ☑️ |
| Bluetooth device connected / disconnected | ☑️ |
| Caps / Num / Scroll Lock | ☑️ |
| Microphone muted / live, mic-mute hotkey | ☑️ |
| Privacy dots and HUD: camera or microphone in use, by which app | ✅ |

## Music
| Feature | Status |
|---|---|
| Media card, controls, seek, artwork, visualizer | ✅ |
| Lyrics line under the progress bar (LRCLIB) | ☑️ |
| Visualizer styles (bars / pixels) | ☑️ |
| Progress bar styles (wave, simple, pixels, comet, hearts) | ☑️ |
| Choose which apps count as media sources | ⬜ |

## Home widgets
| Feature | Status |
|---|---|
| Date, Weather (Open-Meteo, city search or Windows location) | ✅ |
| Stats: CPU, GPU, RAM, network | ✅ |
| Stats: temperatures | ⬜ |
| Timer / Pomodoro (focus + break), on the pill while running | ✅ |
| World clock | ☑️ |
| To-do list | ☑️ |
| Photos slideshow from a folder | ☑️ |
| Battery tile | ✅ |
| Calendar (ICS link), meeting countdown on the pill, Join button | ☑️ (parser tested) |
| Reminders in plain words, repeats, reminder hotkey | ☑️ (parser tested) |
| Stock and crypto ticker (Yahoo, CoinGecko) | ☑️ |
| Price alerts | ⬜ |
| Desktop widgets (pin a tile to the desktop) | ⬜ |

## Shelf and clipboard
| Feature | Status |
|---|---|
| Clipboard history (text, files, pictures) with filters | ✅ |
| Pinned snippets | ☑️ |
| File shelf: drop files on the island, open, show in Explorer, copy | ☑️ |
| Drag files back out of the shelf | ⬜ (copy works) |
| Screenshots (Ctrl+Alt+F) land on the shelf | ☑️ |
| Send to phone: QR code, LAN page, download shelf files, upload from phone | ☑️ (upload parser tested) |

## Notifications and activity
| Feature | Status |
|---|---|
| Mirror Windows notifications, history, click to open the app | ✅ |
| Local API: /notify, /style, /panel, /agent, /snapshot | ✅ |
| Download progress activity, done HUD | ☑️ |
| Call activity (calling app has the mic), duration, mute state | ☑️ |
| Camera mirror (Call panel) | ☑️ |

## Tools
| Feature | Status |
|---|---|
| Colour picker (Ctrl+Alt+C) | ☑️ |
| Screenshot via Windows' region snip | ☑️ |
| Screen recording of its own | ⬜ (record through OBS) |
| OBS: recording / streaming state, Ctrl+Alt+R | ☑️ |
| App launcher (six slots, every Start-menu app) | ✅ |
| Controls: Wi-Fi, Bluetooth, Night light, sound, mic, volume, brightness, output/input device | ✅ (shown); switching ☑️ |
| Focus assist switch | ⬜ |
| Hotkeys (fixed defaults) | ✅ |
| Hotkey editor | ⬜ |
| Per-app rules (normal / slim / hide over an app) | ☑️ |

## Gaming
| Feature | Status |
|---|---|
| Game strip: CPU, GPU, RAM, network, clock while a game is full screen | ☑️ |
| FPS counter (needs ETW, which needs admin rights) | ⬜ |

## AI agents
| Feature | Status |
|---|---|
| Claude Code hooks (Connect / Disconnect), live status, needs-you and done HUDs | ✅ (simulated); Connect ☑️ |
| Custom agents through the local API | ✅ |
| Codex, Gemini, Antigravity hooks | ⬜ |

## Web and streaming
| Feature | Status |
|---|---|
| Twitch live chat panel and pop-ups | ☑️ (parser tested) |
| YouTube live chat | ⬜ |
| Web panel (pinned sites, YouTube Music, WebView2) | ⬜ |
