# HaloBar

A Dynamic Island–style bar for Windows. It sits at the top of the screen as a small
pill and expands when you rest the pointer on it.

![Surfaces and the Style tab](docs/themes.png)

## Features (v0.3)

| Feature | What it does |
|---|---|
| **Island** | Always on top, never takes focus, and lets clicks through everywhere except on itself. Spring-animated between states. Hides while a game, video or presentation is full screen. |
| **Media card** | Whatever Windows' media controls report (Spotify, browsers, Media Player…): artwork, title, artist, progress (click to seek), previous / play-pause / next. Visualiser bars follow the real output level. |
| **Volume HUD** | Shows on any volume or mute change and replaces the Windows volume pop-up. Scroll over the island to change volume; click the speaker to mute. |
| **Notifications** | Mirrors Windows toast notifications, and accepts its own via a local API (below). Hover a notice to keep it; click it to open the history. |
| **Clipboard shelf** | The last 16 things you copied (text, files, pictures). Click a card to copy it again. Copies that password managers mark as private are skipped. |
| **Every monitor** | One island at the top of each monitor, each at that monitor's scaling. Islands follow monitors as they are plugged in, unplugged or rescaled. |
| **Start with Windows** | A tray toggle (per-user Run key). Scripts can use `HaloBar.exe --startup on` or `off`. |
| **Tray icon** | Open the island; toggle start with Windows, the volume pop-up, every monitor, hiding from screenshots and sharing, and hiding in full screen; see memory use; open settings or the log; quit. |
| **Colours** | 12 surfaces: Black, Pure black (OLED, no edge), Graphite, Midnight, Forest, the light themes Rosé, Lavender, Peach, Sakura and Mint, Aurora (slowly drifting colours and a turning colour edge), and Album (follows the playing cover). 16 accents: the surface's own, White, Windows accent, Blue, Purple, Pink, Red, Orange, Yellow, Green, Teal, Blush, Lilac, Sakura pink, Album (from the cover) and Rainbow (cycles), plus any custom `#RRGGBB`. The music card can show the cover's colours as soft drifting light. Colours glide into each other when they change. Pick them in the island's **Style** tab, the tray's Surface / Accent / Music card menus, or the local API. |
| **Hotkey** | `Ctrl+Alt+I` opens and closes the island. |

## Build and run

Requires the .NET 10 SDK on Windows 10 2004 (19041) or later.

```bash
dotnet run --project src/HaloBar
```

Data lives in `%AppData%\HaloBar`: `settings.json`, `clipboard.json` (text and file
history) and `halobar.log`.

## Local API

Only listens on `127.0.0.1`, so nothing else on the network can reach it.

```bash
curl "http://127.0.0.1:47900/notify?title=Build%20passed&body=All%20tests%20green&app=CI"
```

```bash
curl -X POST http://127.0.0.1:47900/notify -d "{\"title\":\"Deploy\",\"body\":\"v2 is live\",\"app\":\"CD\"}"
```

Change colours from a script (any part can be left out):

```bash
curl "http://127.0.0.1:47900/style?surface=sakura&accent=%23FF8800&album=on"
```

## How it is put together

```
src/HaloBar
├─ Program.cs                 frame loop: messages → update → draw → present
├─ Platform/                  Win32: overlay window, tray, clipboard, full-screen check
├─ Rendering/CompositionSurface.cs   D3D11 swap chain on a DirectComposition visual
├─ Services/                  media (WinRT), volume (Core Audio), notifications, local API, clipboard
└─ UI/                        IslandView (state + layout + hit-testing), springs, theme, icons
```

* The window is a `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE |
  WS_EX_NOREDIRECTIONBITMAP` popup. Its pixels come from a premultiplied-alpha swap chain
  attached through DirectComposition, so the island has soft transparent edges.
* Skia draws each frame on the CPU and the result is copied into the back buffer. All monitors
  share one D3D11 device and one DirectComposition device, and while anything moves, frames for
  every monitor are paced to the display with a single `DwmFlush`. When nothing moves, the loop
  sleeps and only checks the pointer about 30 times a second (about 0.5% CPU).
* Memory: album art and clipboard pictures are kept as small thumbnails, with pictures stored as
  PNG for copying back; Skia's caches are capped; and when the island has been idle for 10 s it does
  a compacting GC and trims its working set.
* The window lets clicks through (`WS_EX_TRANSPARENT`) except while the pointer is over
  the island, so it never blocks the browser tabs underneath it.

## Known limits

* Windows notification mirroring depends on Windows granting access
  (Settings › Privacy › Notifications).
* The Windows volume pop-up is hidden by giving Explorer's pop-up window an empty region. Explorer only creates that window the first time the pop-up is needed, so after Explorer restarts the pop-up may show once before HaloBar catches it. On exit HaloBar gives it back. If HaloBar is killed, the pop-up stays hidden until Explorer restarts.
* More than one monitor has only been checked in code; it hasn't been tested with a second screen yet.
