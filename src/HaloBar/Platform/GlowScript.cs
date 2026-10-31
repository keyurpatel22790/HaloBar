namespace HaloBar.Platform;

/// <summary>
/// Light around the video inside the page, in the colours of the picture, like ambient light strips.
/// A small canvas shows the current frame stretched past the player and heavily blurred, lightening
/// the page around the player (screen blend) with the player itself cut out, so the video is
/// untouched. It sits under YouTube's top bar and menus, and is off in full screen. Runs in the page;
/// nothing leaves the PC.
/// </summary>
internal static class GlowScript
{
    public const string Source = """
        (() => {
          if (window.top !== window) return;
          let cv, g;
          const make = () => {
            cv = document.createElement('canvas');
            cv.width = 64; cv.height = 36;
            cv.setAttribute('aria-hidden', 'true');
            Object.assign(cv.style, {
              position: 'fixed', left: '0', top: '0', width: '0', height: '0', pointerEvents: 'none',
              zIndex: '1999', mixBlendMode: 'screen', filter: 'blur(42px) saturate(1.6) brightness(1.1)',
              opacity: '0', transition: 'opacity .8s ease'
            });
            document.documentElement.appendChild(cv);
            g = cv.getContext('2d');
          };
          setInterval(() => {
            if (!document.documentElement) return;
            if (!cv || !cv.isConnected) make();
            const v = [...document.querySelectorAll('video')].find(x =>
              x.videoWidth && !x.ended && x.currentTime > 0 && x.readyState >= 2 && x.getBoundingClientRect().width > 200);
            const box = v && (v.closest('.html5-video-player') || v.parentElement || v).getBoundingClientRect();
            const full = !box || document.fullscreenElement || (box.width >= innerWidth - 4 && box.height >= innerHeight - 4);
            if (!v || full || document.hidden) { cv.style.opacity = '0'; return; }
            const pad = Math.round(Math.min(150, Math.max(70, box.width * 0.12)));
            const w = box.width + 2 * pad, h = box.height + 2 * pad;
            try { g.drawImage(v, 0, 0, cv.width, cv.height); } catch (e) { cv.style.opacity = '0'; return; }
            // The player is cut out, so only the page around it lights up; the outer edge reaches past the canvas so the blur fades out.
            const o = pad, inner = `${pad}px ${pad}px, ${w - pad}px ${pad}px, ${w - pad}px ${h - pad}px, ${pad}px ${h - pad}px, ${pad}px ${pad}px`;
            Object.assign(cv.style, {
              left: `${box.left - pad}px`, top: `${box.top - pad}px`, width: `${w}px`, height: `${h}px`,
              clipPath: `polygon(evenodd, ${-o}px ${-o}px, ${w + o}px ${-o}px, ${w + o}px ${h + o}px, ${-o}px ${h + o}px, ${-o}px ${-o}px, ${inner})`,
              opacity: '0.8'
            });
          }, 100);
        })();
        """;
}
