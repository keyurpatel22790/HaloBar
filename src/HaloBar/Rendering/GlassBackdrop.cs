using HaloBar.UI;
using SkiaSharp;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HaloBar.Rendering;

/// <summary>
/// What's on the screen behind the island, for the Glass lens to bend and blur. Windows' Desktop
/// Duplication hands over each new desktop frame on the GPU; the island window is kept out of
/// captures while glass is on, so it never sees itself. Only the part behind the island is read
/// back, shrunk and blurred (cheap at that size), at most 30 times a second.
/// </summary>
internal interface IGlassSource : IDisposable
{
    /// <summary>False once this way of reading the screen stopped working.</summary>
    bool Working { get; }
    /// <summary>
    /// The blurred picture behind <paramref name="screen"/> (screen pixels), shrunk, or null for now.
    /// <paramref name="paceMs"/>: how often it should be refreshed while nothing moves.
    /// <paramref name="hole"/> (screen pixels): where the island itself shows in the copy (it's
    /// visible to captures while recording or shown in sharing), filled from around it.
    /// </summary>
    SKImage? Behind(SKRectI screen, int blur, int paceMs = 33, SKRectI? hole = null);
    /// <summary>The screen area the picture last handed out was taken from.</summary>
    SKRectI Area { get; }
    /// <summary>How bright the picture last handed out is, measured off the window thread.</summary>
    GlassTone Tone { get; }
    /// <summary>Goes up with every new picture; the caller redraws when it changes.</summary>
    int Version { get; }
    /// <summary>Looks for a new picture between frames (Desktop Duplication); a no-op for a copier with its own thread.</summary>
    void Poll();
}

/// <summary>
/// How bright a picture is (0–1, as the sRGB grey of the same luminance): on average, and its darker and brighter parts
/// (the 4th and 96th percentiles), which decide whether text over it can be read.
/// </summary>
/// <param name="Luma">
/// The plain average of sRGB luma (0.2126 R + 0.7152 G + 0.0722 B), which the classic glass's
/// smoke follows; it rates saturated colours darker than true luminance does. Null: as Mean.
/// </param>
internal readonly record struct GlassTone(double Mean, double Low, double High, double? Luma = null)
{
    public static readonly GlassTone Dark = new(0.3, 0.2, 0.4);

    /// <summary>The plain sRGB luma average (see the Luma parameter).</summary>
    public double PlainLuma => Luma ?? Mean;

    /// <summary>Eased toward <paramref name="to"/> by <paramref name="k"/> (0–1).</summary>
    public GlassTone Toward(GlassTone to, double k) =>
        new(Mean + (to.Mean - Mean) * k, Low + (to.Low - Low) * k, High + (to.High - High) * k,
            PlainLuma + (to.PlainLuma - PlainLuma) * k);
}

internal static class GlassLight
{
    /// <summary>
    /// The mean and the spread of brightness of a picture, from a sparse grid of its pixels.
    /// <paramref name="skip"/> (picture pixels) is left out: while recording, the island's own spot
    /// is patched from its surroundings, and that guess must not sway the brightness the glass reads,
    /// or it blinks as the patch comes out a shade darker or lighter each frame.
    /// </summary>
    public static GlassTone Tone(SKImage image, SKRectI? skip = null)
    {
        // Only the blurred picture (the top half; the sharp one is below it).
        using var all = SKBitmap.FromImage(image);
        using var pixels = new SKBitmap();
        if (!all.ExtractSubset(pixels, new SKRectI(0, 0, all.Width, GlassBackdrop.PictureHeight(image)))) return GlassTone.Dark;
        var histogram = new int[64];
        double sum = 0, luma = 0;
        var n = 0;
        // A denser grid, each row shifted, so fine stripes or checks can't all fall on one shade.
        var step = Math.Max(1, Math.Min(pixels.Width, pixels.Height) / 40);
        for (int y = step / 2, row = 0; y < pixels.Height; y += step, row++)
            for (var x = (row * 5 + step / 2) % step; x < pixels.Width; x += step)
            {
                if (skip is { } s && x >= s.Left && x < s.Right && y >= s.Top && y < s.Bottom) continue;
                var c = pixels.GetPixel(x, y);
                // True luminance (as contrast is judged), as the grey that has it: a pure red counts as the mid grey it looks.
                var l = LiquidGlass.Grey(0.2126 * LiquidGlass.Linear(c.Red / 255.0) + 0.7152 * LiquidGlass.Linear(c.Green / 255.0) + 0.0722 * LiquidGlass.Linear(c.Blue / 255.0));
                sum += l;
                luma += (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255;
                histogram[Math.Min(63, (int)(l * 64))]++;
                n++;
            }
        if (n == 0) return GlassTone.Dark;
        double Percentile(double p)
        {
            var want = p * n;
            var seen = 0;
            for (var i = 0; i < 64; i++)
            {
                seen += histogram[i];
                if (seen >= want) return (i + 0.5) / 64;
            }
            return 1;
        }
        return new GlassTone(sum / n, Percentile(0.04), Percentile(0.96), luma / n);
    }

    /// <summary>
    /// Average brightness (0–1) of a picture, or of the part <paramref name="part"/> covers (as
    /// fractions of its width and height), from a sparse grid of its pixels.
    /// </summary>
    public static double Of(SKImage image, SKRect? part = null)
    {
        using var pixels = SKBitmap.FromImage(image);
        var area = part ?? new SKRect(0, 0, 1, 1);
        int x0 = (int)(Math.Clamp(area.Left, 0, 1) * pixels.Width), x1 = (int)(Math.Clamp(area.Right, 0, 1) * pixels.Width);
        int y0 = (int)(Math.Clamp(area.Top, 0, 1) * pixels.Height), y1 = (int)(Math.Clamp(area.Bottom, 0, 1) * pixels.Height);
        double sum = 0;
        var n = 0;
        for (var y = y0; y < Math.Max(y0 + 1, y1) && y < pixels.Height; y += 3)
            for (var x = x0; x < Math.Max(x0 + 1, x1) && x < pixels.Width; x += 3)
            {
                var c = pixels.GetPixel(x, y);
                sum += (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255;
                n++;
            }
        return n == 0 ? 0 : sum / n;
    }
}

internal sealed class GlassBackdrop : IGlassSource
{
    /// <summary>
    /// How many times smaller than the screen the picture is kept: full size when the glass is
    /// clear (shrunk, sharp text behind turns blocky), smaller as the blur hides the detail anyway.
    /// </summary>
    public static int ShrinkFor(int blur) => Prefs.GlassClassic
        ? Math.Max(1, (int)Math.Round(ClassicSigma(blur) / 2.2f))
        : blur < 25 ? 1 : blur < 60 ? 2 : 3;

    /// <summary>
    /// The classic look's blur, in screen pixels: 2.2 at the middle of the slider, four times less
    /// or more at its ends (2.2 · 4^((blur − 50) / 50)); the picture is shrunk as it grows past 2.2.
    /// </summary>
    public static float ClassicSigma(int blur) => 2.2f * MathF.Pow(4, (Math.Clamp(blur, 0, 100) - 50) / 50f);

    /// <summary>
    /// The blur, in pixels of the shrunk picture: about 16 screen pixels at most. Even clear glass
    /// softens a little (as Apple's does), so sharp text behind never fights the island's own.
    /// </summary>
    public static float SigmaFor(int blur) => Prefs.GlassClassic
        ? ClassicSigma(blur) / ShrinkFor(blur)
        : (2.2f + blur / 100f * 14f) / ShrinkFor(blur);

    private static SKImageFilter? BlurFor(int blur) =>
        SigmaFor(blur) is var sigma && sigma < 0.3f ? null : SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);

    private readonly GraphicsDevice _gfx;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly SKRectI _desktop;
    private ID3D11Texture2D? _screen;   // the latest desktop frame, kept on the GPU
    private ID3D11Texture2D? _staging;  // the part behind the island, for reading back
    private int _stagingW, _stagingH;
    private SKImage? _image;
    private SKRectI _imageFor;
    /// <summary>The desktop's pixel format: 8-bit BGRA usually, 16-bit float on an HDR or advanced-colour screen.</summary>
    private Format _format = Format.B8G8R8A8_UNorm;
    private int _imageBlur = -1;
    private SKRectI? _imageHole;
    private bool _imageClassic;
    private bool _newFrame;
    private DateTime _lastRead;

    private GlassBackdrop(GraphicsDevice gfx, IDXGIOutputDuplication duplication, SKRectI desktop)
    {
        _gfx = gfx;
        _duplication = duplication;
        _desktop = desktop;
    }

    /// <summary>Duplication of the output showing <paramref name="monitor"/>, if this GPU drives it.</summary>
    public static GlassBackdrop? Create(GraphicsDevice gfx, IntPtr monitor)
    {
        try
        {
            using var dxgiDevice = gfx.Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
            {
                using (output)
                {
                    if (output.Description.Monitor != monitor) continue;
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    var duplication = output1.DuplicateOutput(gfx.Device);
                    var r = output.Description.DesktopCoordinates;
                    return new GlassBackdrop(gfx, duplication, new SKRectI(r.Left, r.Top, r.Right, r.Bottom));
                }
            }
            Log.Write("Glass: this screen is on another GPU; the lens keeps Windows' blur");
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Log.Write($"Glass: desktop duplication unavailable ({ex.Message}); the lens keeps Windows' blur");
        }
        return null;
    }

    public int Version { get; private set; }
    public SKRectI Area => _imageFor;
    public GlassTone Tone { get; private set; } = GlassTone.Dark;

    private readonly DateTime _bornAt = DateTime.Now;
    private bool _sawContent;
    /// <summary>
    /// True once duplication has run a while but only ever produced blank (all-zero) frames — which
    /// happens for some outputs (an HDR monitor, a mirrored or protected one): the caller then falls
    /// back to a plain screen copy, which always returns pixels.
    /// </summary>
    public bool NeverDelivered => !_sawContent && DateTime.Now - _bornAt > TimeSpan.FromSeconds(1.5);

    public void Poll()
    {
        TakeNewFrame();
        if (_newFrame && _screen != null && DateTime.Now - _lastRead > TimeSpan.FromMilliseconds(16)) Version++;
    }

    /// <summary>False once duplication stopped working (a full-screen exclusive game, a mode change).</summary>
    public bool Working { get; private set; } = true;

    /// <summary>
    /// The blurred picture behind <paramref name="screen"/> (screen pixels), at 1/<see cref="ShrinkFor"/>
    /// size, or null before the first desktop frame. <paramref name="blur"/> is 0–100.
    /// </summary>
    public SKImage? Behind(SKRectI screen, int blur, int paceMs = 33, SKRectI? hole = null)
    {
        TakeNewFrame();
        if (_screen == null) return null;
        var moved = screen != _imageFor || blur != _imageBlur || hole != _imageHole || Prefs.GlassClassic != _imageClassic;
        // A new desktop frame is read at most every paceMs; a moved or resized area at once.
        if (!moved && !(_newFrame && DateTime.Now - _lastRead > TimeSpan.FromMilliseconds(paceMs))) return _image;
        _newFrame = false;
        _lastRead = DateTime.Now;
        var picture = Read(screen, blur, hole);
        if (picture == null) return _image;
        _image?.Dispose();
        (_image, _imageFor, _imageBlur, _imageHole, _imageClassic) = (picture, screen, blur, hole, Prefs.GlassClassic);
        Tone = GlassLight.Tone(picture, HoleInPicture(hole, screen, blur));
        if (Tone.High > 0.01) _sawContent = true;
        else if (NeverDelivered)
        {
            Working = false; // only blank frames from this output; the caller switches to a screen copy
            Log.Write("Glass: desktop duplication gave only blank frames; switching to a screen copy");
        }
        Version++;
        return _image;
    }

    private void TakeNewFrame()
    {
        if (!Working) return;
        var hr = _duplication.AcquireNextFrame(0, out var info, out var resource);
        if (hr.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return;
        if (hr.Failure)
        {
            Working = false;
            Log.Write($"Glass: desktop duplication stopped (0x{hr.Code:X8})");
            return;
        }
        try
        {
            if (info.LastPresentTime != 0 || _screen == null)
            {
                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                if (_screen == null)
                {
                    var desc = texture.Description;
                    Log.Write($"Glass: duplicating {desc.Width}x{desc.Height} {desc.Format} at {_desktop}");
                    _format = desc.Format;
                    desc.BindFlags = BindFlags.None;
                    desc.MiscFlags = ResourceOptionFlags.None;
                    desc.CPUAccessFlags = CpuAccessFlags.None;
                    desc.Usage = ResourceUsage.Default;
                    _screen = _gfx.Device.CreateTexture2D(desc);
                }
                _gfx.Context.CopyResource(_screen, texture);
                _newFrame = true;
            }
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    /// <summary>Copies the screen behind the island to the CPU, shrinks and blurs it.</summary>
    private unsafe SKImage? Read(SKRectI screen, int blur, SKRectI? hole)
    {
        // Into the output's own coordinates, clamped to it.
        var left = Math.Clamp(screen.Left - _desktop.Left, 0, _desktop.Right - _desktop.Left);
        var top = Math.Clamp(screen.Top - _desktop.Top, 0, _desktop.Bottom - _desktop.Top);
        var right = Math.Clamp(screen.Right - _desktop.Left, 0, _desktop.Right - _desktop.Left);
        var bottom = Math.Clamp(screen.Bottom - _desktop.Top, 0, _desktop.Bottom - _desktop.Top);
        int w = right - left, h = bottom - top;
        if (w < 4 || h < 4) return null;
        if (_staging == null || w > _stagingW || h > _stagingH)
        {
            _staging?.Dispose();
            (_stagingW, _stagingH) = (Math.Max(w, _stagingW), Math.Max(h, _stagingH));
            _staging = _gfx.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_stagingW, Height = (uint)_stagingH, MipLevels = 1, ArraySize = 1,
                Format = _format, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
            });
        }
        // The hole in the copy's own pixels.
        var local = hole is { } o ? new SKRectI(o.Left - _desktop.Left - left, o.Top - _desktop.Top - top, o.Right - _desktop.Left - left, o.Bottom - _desktop.Top - top) : (SKRectI?)null;
        _gfx.Context.CopySubresourceRegion(_staging, 0, 0, 0, 0, _screen!, 0, new Vortice.Mathematics.Box(left, top, 0, right, bottom, 1));
        var mapped = _gfx.Context.Map(_staging, 0, MapMode.Read);
        try
        {
            return _format switch
            {
                Format.R16G16B16A16_Float => FromHalfFloats(mapped.DataPointer, w, h, (int)mapped.RowPitch, blur, local),
                Format.B8G8R8A8_UNorm => ShrinkAndBlur(mapped.DataPointer, w, h, (int)mapped.RowPitch, blur, local),
                _ => null, // a format this doesn't read: Windows' blur instead
            };
        }
        finally
        {
            _gfx.Context.Unmap(_staging, 0);
        }
    }

    /// <summary>
    /// Pixels of the screen (BGRA) to a picture 1/ShrinkFor(blur) the size, blurred by blur (0-100). Small
    /// first, then blur: far cheaper than blurring at full size, and the glass is soft anyway.
    /// </summary>
    /// <summary>
    /// An HDR or advanced-colour desktop (linear scRGB in 16-bit floats, 1.0 = SDR white): every
    /// <see cref="ShrinkFor"/>th pixel is turned into ordinary sRGB, which also does the shrinking,
    /// then blurred as usual.
    /// </summary>
    private static unsafe SKImage FromHalfFloats(IntPtr pixels, int w, int h, int rowBytes, int blur, SKRectI? hole)
    {
        var shrink = ShrinkFor(blur);
        int sw = Math.Max(1, w / shrink), sh = Math.Max(1, h / shrink);
        var small = new SKBitmap(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
        var target = (uint*)small.GetPixels();
        for (var y = 0; y < sh; y++)
        {
            var row = (Half*)((byte*)pixels + y * shrink * rowBytes);
            for (var x = 0; x < sw; x++)
            {
                var p = row + x * shrink * 4;
                target[y * sw + x] = 0xFF000000u | (uint)ToSrgb((float)p[0]) << 16 | (uint)ToSrgb((float)p[1]) << 8 | ToSrgb((float)p[2]);
            }
        }
        using (small)
        using (var image = SKImage.FromBitmap(small))
            return Finish(image, blur, hole, shrink);
    }

    /// <summary>
    /// The shrunk picture made vivid and blurred, with the island's own spot (<paramref name="hole"/>,
    /// in screen pixels of the copy) filled from around it first.
    /// </summary>
    /// <remarks>
    /// The picture is two pictures stacked: the blurred one on top, the same unblurred below it
    /// (Apple's glass is only half blurred near its rim, so the edge reads crisper). See
    /// <see cref="PictureHeight"/>.
    /// </remarks>
    private static SKImage Finish(SKImage small, int blur, SKRectI? hole, int shrink)
    {
        var source = small;
        if (hole is { } o)
            source = GlassFill.Fill(small, new SKRectI(o.Left / shrink, o.Top / shrink, (o.Right + shrink - 1) / shrink, (o.Bottom + shrink - 1) / shrink));
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(small.Width, small.Height * 2, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var paint = new SKPaint { ImageFilter = BlurFor(blur), ColorFilter = Prefs.GlassClassic ? null : Vivid })
            {
                surface.Canvas.Save();
                surface.Canvas.ClipRect(new SKRect(0, 0, small.Width, small.Height));
                surface.Canvas.DrawImage(source, 0, 0, paint);
                surface.Canvas.Restore();
            }
            using (var paint = new SKPaint { ColorFilter = Prefs.GlassClassic ? null : Vivid })
                surface.Canvas.DrawImage(source, 0, small.Height, paint);
            return surface.Snapshot();
        }
        finally
        {
            if (!ReferenceEquals(source, small)) source.Dispose();
        }
    }

    /// <summary>Linear light (0–1, brighter clipped) to an 8-bit sRGB value, through a table.</summary>
    private static byte ToSrgb(float linear) => SrgbTable[(int)(Math.Clamp(linear, 0, 1) * (SrgbTable.Length - 1))];

    private static readonly byte[] SrgbTable = Enumerable.Range(0, 4096).Select(i =>
    {
        var c = i / 4095.0;
        var s = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(s, 0, 1) * 255);
    }).ToArray();

    /// <summary>A little more saturation and light, as colours look through Apple's glass.</summary>
    private static readonly SKColorFilter Vivid = Saturate(1.12f, 1.0f);

    private static SKColorFilter Saturate(float s, float gain)
    {
        const float r = 0.2126f, g = 0.7152f, b = 0.0722f;
        float[] m =
        [
            gain * (r + (1 - r) * s), gain * g * (1 - s), gain * b * (1 - s), 0, 0,
            gain * r * (1 - s), gain * (g + (1 - g) * s), gain * b * (1 - s), 0, 0,
            gain * r * (1 - s), gain * g * (1 - s), gain * (b + (1 - b) * s), 0, 0,
            0, 0, 0, 1, 0,
        ];
        return SKColorFilter.CreateColorMatrix(m);
    }

    /// <summary>The height of the blurred picture in a glass picture (the sharp one sits below it).</summary>
    public static int PictureHeight(SKImage glass) => Math.Max(1, glass.Height / 2);

    /// <summary>A hole (screen pixels) as a rectangle in the shrunk picture, a little wider to be safe, or null.</summary>
    public static SKRectI? HoleInPicture(SKRectI? hole, SKRectI screen, int blur)
    {
        if (hole is not { } h) return null;
        var k = ShrinkFor(blur);
        return new SKRectI((h.Left - screen.Left) / k - 1, (h.Top - screen.Top) / k - 1,
            (h.Right - screen.Left) / k + 2, (h.Bottom - screen.Top) / k + 2);
    }

    public static SKImage ShrinkAndBlur(IntPtr pixels, int w, int h, int rowBytes, int blur, SKRectI? hole = null)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var full = new SKBitmap();
        full.InstallPixels(info, pixels, rowBytes);
        var shrink = ShrinkFor(blur);
        var small = new SKImageInfo(Math.Max(1, w / shrink), Math.Max(1, h / shrink), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(small);
        using var source = SKImage.FromBitmap(full);
        surface.Canvas.DrawImage(source, new SKRect(0, 0, w, h), new SKRect(0, 0, small.Width, small.Height), new SKSamplingOptions(SKFilterMode.Linear));
        using var shrunk = surface.Snapshot();
        return Finish(shrunk, blur, hole, shrink);
    }

    public void Dispose()
    {
        _image?.Dispose();
        _staging?.Dispose();
        _screen?.Dispose();
        _duplication.Dispose();
    }
}

/// <summary>
/// The fallback where Desktop Duplication isn't offered (a laptop screen wired through the other
/// GPU, say): the screen behind the island is copied with GDI on a thread of its own, 30 times a
/// second, so the island's frames never wait for it. The island is kept out of captures meanwhile,
/// so the copy never contains it (unless it must show in recordings: then it's filled in, see GlassFill).
/// </summary>
internal sealed class ScreenCopyBackdrop : IGlassSource
{
    private readonly object _gate = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private SKRectI _want;
    private int _blur;
    private SKRectI? _hole;
    private bool _classic;
    // _given is the picture last handed out (the caller may still be drawing with it); it is only
    // let go once the caller has asked again and been handed a newer one. _image is the newest.
    private SKImage? _image, _given;
    private readonly int _openMs, _smallMs;
    private DateTime _askedAt = DateTime.Now;

    /// <param name="openMs">Pause between copies for a big area (an open card, a window).</param>
    /// <param name="smallMs">Pause for a small one (the closed pill).</param>
    public ScreenCopyBackdrop(int openMs = 33, int smallMs = 100)
    {
        (_openMs, _smallMs) = (openMs, smallMs);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Glass screen copy", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Log.Write("Glass: copying the screen behind the island (no desktop duplication on this screen)");
    }

    public bool Working { get; private set; } = true;

    private volatile int _version;
    public int Version => _version;
    private SKRectI _imageArea, _givenArea;
    private GlassTone _imageLight = GlassTone.Dark, _givenLight = GlassTone.Dark;
    public SKRectI Area { get { lock (_gate) return _givenArea; } }
    public GlassTone Tone { get { lock (_gate) return _givenLight; } }
    public void Poll() { }

    public SKImage? Behind(SKRectI screen, int blur, int paceMs = 33, SKRectI? hole = null)
    {
        lock (_gate)
        {
            // A different area (the window moved) or blur: copy now rather than at the next turn.
            if (screen != _want || blur != _blur || hole != _hole || Prefs.GlassClassic != _classic) _wake.Set();
            _classic = Prefs.GlassClassic;
            (_want, _blur, _askedAt, _pace, _hole) = (screen, blur, DateTime.Now, paceMs, hole);
            if (!ReferenceEquals(_given, _image))
            {
                _given?.Dispose(); // the caller is about to replace it with this newer one
                (_given, _givenArea, _givenLight) = (_image, _imageArea, _imageLight);
            }
            return _given;
        }
    }

    private void Loop()
    {
        while (!_stop)
        {
            SKRectI want;
            int blur;
            DateTime asked;
            SKRectI? hole;
            lock (_gate) (want, blur, asked, hole) = (_want, _blur, _askedAt, _hole);
            // Nobody drawing with it (the window hidden, the surface changed): don't copy.
            if (DateTime.Now - asked > TimeSpan.FromSeconds(0.5))
            {
                Thread.Sleep(100);
                continue;
            }
            if (want.Width >= 4 && want.Height >= 4)
            {
                var image = Copy(want, blur, hole is { } o ? new SKRectI(o.Left - want.Left, o.Top - want.Top, o.Right - want.Left, o.Bottom - want.Top) : null);
                if (image == null)
                {
                    // A single GDI copy can fail for a moment (common while a recording starts or the
                    // island's capture flag flips). Keep the last picture and try again shortly rather
                    // than killing the thread, or one blip would leave the glass dead until restart.
                    _copyFails++;
                    if (_copyFails == 1 || _copyFails % 25 == 0)
                        Log.Write($"Glass: the screen copy failed ({_copyFails}×); retrying, the lens keeps the last picture meanwhile");
                    _wake.WaitOne(Math.Min(1000, 100 * _copyFails));
                    continue;
                }
                _copyFails = 0;
                // Same as last time (nothing moved behind): keep the old picture, so nothing redraws.
                var print = Fingerprint(image);
                if (print == _lastPrint && want == _lastWant && blur == _lastBlur && hole == null && _classic == _lastClassic)
                {
                    image.Dispose();
                    _wake.WaitOne(_pace);
                    continue;
                }
                (_lastPrint, _lastWant, _lastBlur, _lastClassic) = (print, want, blur, _classic);
                var light = GlassLight.Tone(image, GlassBackdrop.HoleInPicture(hole, want, blur));
                lock (_gate)
                {
                    // A newer picture replaces one nobody was given; the one handed out stays.
                    if (!ReferenceEquals(_image, _given)) _image?.Dispose();
                    (_image, _imageArea, _imageLight) = (image, want, light);
                    _version++;
                }
            }
            // The open card 30 times a second; the small closed pill needs far less.
            _wake.WaitOne(_pace);
        }
    }

    private readonly AutoResetEvent _wake = new(false);
    private int _pace = 33;
    private int _copyFails;
    private long _lastPrint;
    private SKRectI _lastWant;
    private int _lastBlur;
    private bool _lastClassic;

    /// <summary>A cheap summary of a picture (a sparse grid of its pixels) to tell whether it changed.</summary>
    private static long Fingerprint(SKImage image)
    {
        using var pixels = SKBitmap.FromImage(image);
        long hash = 17;
        for (var y = 0; y < pixels.Height; y += 6)
            for (var x = 0; x < pixels.Width; x += 6)
                hash = hash * 31 + (uint)pixels.GetPixel(x, y);
        return hash;
    }

    private static unsafe SKImage? Copy(SKRectI r, int blur, SKRectI? hole)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER { Size = sizeof(BITMAPINFOHEADER), Width = r.Width, Height = -r.Height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero) return null;
            var old = SelectObject(memory, bitmap);
            var copied = BitBlt(memory, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, 0x00CC0020 /* SRCCOPY */);
            SelectObject(memory, old);
            if (!copied) return null;
            return GlassBackdrop.ShrinkAndBlur(bits, r.Width, r.Height, r.Width * 4, blur, hole);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(200);
        lock (_gate)
        {
            if (!ReferenceEquals(_image, _given)) _image?.Dispose();
            _given?.Dispose();
            _image = _given = null;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
