using Microsoft.Web.WebView2.Core;

namespace HaloBar.Platform;

/// <summary>
/// Ad blocking for the web drawer's pages (phase 1 of docs/ad-blocking-plan.md), written for
/// HaloBar on the ideas Brave Shields and uBlock Origin use:
/// 1. requests to known ad and tracker hosts, and to YouTube's ad paths, get an empty answer;
/// 2. a script that runs before the page's own code removes the ad entries from YouTube's player
///    data, so the player (YouTube and YouTube Music alike) never plans an ad;
/// 3. if an ad plays anyway (one spliced into the stream, say), it is muted, played at 16× and
///    skipped as soon as YouTube allows.
/// The rules are kept short and in one place, so they can move to an updatable file later.
/// </summary>
internal static class AdBlocker
{
    /// <summary>Ad and tracker hosts; a request to one of them, or any subdomain, is refused.</summary>
    public static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com",
        "googletagservices.com", "google-analytics.com", "imasdk.googleapis.com",
        "adnxs.com", "amazon-adsystem.com", "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "scorecardresearch.com", "quantserve.com", "moatads.com", "adsrvr.org", "pubmatic.com",
        "rubiconproject.com", "openx.net", "casalemedia.com", "advertising.com", "ads-twitter.com",
        "adform.net", "smartadserver.com", "teads.tv", "zemanta.com", "yieldmo.com",
    };

    /// <summary>Paths on YouTube's own hosts that only serve or count ads.</summary>
    private static readonly string[] YouTubeAdPaths =
        ["/pagead/", "/api/stats/ads", "/ptracking", "/get_midroll_info", "/youtubei/v1/player/ad_break"];

    /// <summary>Blocks <paramref name="url"/>? Hosts above (and their subdomains) and YouTube's ad paths.</summary>
    public static bool Blocks(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        // The host itself, then each parent: ads.example.com, example.com, com.
        for (var host = uri.Host; host.Length > 0;)
        {
            if (Hosts.Contains(host)) return true;
            var dot = host.IndexOf('.');
            if (dot < 0) break;
            host = host[(dot + 1)..];
        }
        if (uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase))
            foreach (var path in YouTubeAdPaths)
                if (uri.PathAndQuery.Contains(path, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Runs in every page before its own scripts. Removes the ad entries YouTube's player reads
    /// (wherever the player data arrives: JSON.parse, fetch's json(), or the global the page sets),
    /// and, when <c>fastForward</c> is on, mutes and speeds through any ad that still plays.
    /// </summary>
    private static string Script(bool fastForward) => """
        (() => {
          if (window.__haloAds) return;
          window.__haloAds = true;
          const fields = ['adPlacements', 'playerAds', 'adSlots', 'adBreakHeartbeatParams', 'adBreakParams'];
          const clean = (o) => {
            if (!o || typeof o !== 'object') return o;
            for (const f of fields) if (f in o) { try { delete o[f]; } catch (e) {} }
            if (o.playerResponse && typeof o.playerResponse === 'object') clean(o.playerResponse);
            return o;
          };
          const parse = JSON.parse;
          JSON.parse = function () { return clean(parse.apply(this, arguments)); };
          const json = Response.prototype.json;
          Response.prototype.json = function () { return json.apply(this, arguments).then(clean); };
          for (const name of ['ytInitialPlayerResponse', 'ytInitialData']) {
            let value;
            try {
              Object.defineProperty(window, name, {
                configurable: true,
                get() { return value; },
                set(v) { value = clean(v); },
              });
            } catch (e) {}
          }
        """ + (fastForward ? """
          // Safety net: an ad that still plays is muted, sped up, and skipped when allowed.
          let muted = null;
          setInterval(() => {
            const player = document.querySelector('.html5-video-player');
            const video = player && player.querySelector('video');
            if (!video) return;
            if (player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting')) {
              if (muted === null) muted = video.muted;
              video.muted = true;
              if (video.playbackRate < 16) video.playbackRate = 16;
              if (isFinite(video.duration) && video.duration > 0 && video.currentTime < video.duration - 0.1) video.currentTime = video.duration - 0.1;
              const skip = document.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern');
              if (skip) skip.click();
            } else if (muted !== null) {
              video.muted = muted;
              video.playbackRate = 1;
              muted = null;
            }
          }, 250);
        """ : "") + """
        })();
        """;

    /// <summary>Hooks blocking into one drawer page; <paramref name="count"/> is told of each blocked request.</summary>
    public static async Task AttachAsync(CoreWebView2 web, CoreWebView2Environment environment, bool fastForward, Action count)
    {
        web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        web.WebResourceRequested += (_, e) =>
        {
            if (!Blocks(e.Request.Uri)) return;
            e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            count();
        };
        await web.AddScriptToExecuteOnDocumentCreatedAsync(Script(fastForward));
    }
}
