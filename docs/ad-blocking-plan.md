# Ad blocking in the web drawer: plan

Only the web drawer (HaloBar's WebView2 pages: YT Music, YouTube, Spotify web and so on).
Nothing system-wide, nothing in other apps.

## How it would work: four layers, the same ideas as Brave Shields and uBlock Origin

1. **Network blocking.** WebView2's `WebResourceRequested` sees every request the page makes. Requests
   to known ad and tracker addresses (from EasyList / EasyPrivacy) are answered with an empty response.
   This catches display ads and trackers on most sites, but not YouTube's video ads, which come from the
   same servers as the videos.

2. **Player-data cleaning (the YouTube and YouTube Music part).** A small script of HaloBar's own,
   added with `AddScriptToExecuteOnDocumentCreatedAsync`, runs before YouTube's code. It wraps
   `JSON.parse`, `Response.prototype.json` and `fetch` so the ad fields of the player response
   (`adPlacements`, `playerAds`, `adSlots`, `adBreakHeartbeatParams`) are removed before the player sees
   them. This is the idea behind uBlock Origin's `json-prune` scriptlet, written for HaloBar.
   The field names live in an updatable rules file, not in the code, because YouTube renames them.

3. **Hiding leftovers (cosmetic rules).** CSS injected per site hides ad boxes, banners and "promoted"
   cards: YouTube's masthead ad, ad slots in the feed, the Music "upgrade" banner.

4. **Safety net for ads that get through (server-side inserted ads).** If an ad still plays, the
   script sees YouTube mark the player as showing an ad. It then mutes it, plays it at 16× and clicks
   Skip the moment it appears, so a missed ad lasts about a second and is silent. This also covers ads
   YouTube splices into the video stream, which nothing can remove from the data.

## Rules and updates

- `adblock-rules.json` ships with HaloBar: host and URL patterns, the YouTube fields, the CSS selectors.
- HaloBar checks for a newer rules file once a day from the project's GitHub repo, so fixes don't need
  a new HaloBar version.
- Optionally the full EasyList / EasyPrivacy lists are downloaded at run time (they are not bundled),
  and only their simple network rules are read.

## Settings (Web page of the Settings window)

- **Block ads in the web drawer:** on / off.
- **Sites where it's off:** per site, for sites that break or that you want to support.
- **Fast-forward ads that get through:** on / off (layer 4).
- A count on the Web tab, e.g. "142 ads blocked today", and the rules version and last update in About.

## Also possible: a ready-made blocker

HaloBar can already load unpacked browser extensions into the drawer (Settings › Web extensions).
uBlock Origin Lite might work there. Worth a quick test, but WebView2's extension support is limited
and an extension can't be updated automatically there, so the built-in blocker is the main plan.

## Checking it works

- A test page for each layer: a request to a listed ad host is blocked; a fake player response loses
  its ad fields; a fake `.ad-showing` player is muted and sped up.
- By hand: ten YouTube videos and an hour of YT Music, counting ads before and after.
- The log records each day's blocked count and any rule that failed to load.

## Risks

- **Terms of service:** YouTube says ad blockers break its terms, and it shows anti-adblock warnings
  or pauses playback. Accounts aren't known to be banned for it, but the drawer could see those warnings.
- **Back-and-forth:** YouTube changes things often; rules will need updates (hence the updatable file).
- **Breakage:** an over-eager rule can break a site, hence the per-site switch.
- **Licences:** EasyList and uBlock Origin's lists have their own licences; downloading them at run
  time rather than bundling them, and writing the scripts ourselves, keeps HaloBar clean.

## Phases

1. Network blocking plus the YouTube / YT Music player script plus fast-forward (the main win). About a day.
2. Cosmetic rules, the per-site switch, the blocked counter. Half a day.
3. Daily rule updates from GitHub, EasyList download. Half a day.
4. Try uBlock Origin Lite as an extension; write down how it went.
