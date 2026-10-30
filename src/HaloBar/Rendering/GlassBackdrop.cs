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
    /// </summary>
    SKImage? Behind(SKRectI screen, int blur, int paceMs = 33);
    /// <summary>The screen area the picture last handed out was taken from.</summary>
    SKRectI Area { get; }
    /// <summary>How bright the picture last handed out is (0–1), measured off the window thread.</summary>
    double Brightness { get; }
    /// <summary>Goes up with every new picture; the caller redraws when it changes.</summary>
    int Version { get; }
    /// <summary>Looks for a new picture between frames (Desktop Duplication); a no-op for a copier with its own thread.</summary>
    void Poll();
}

internal static class GlassLight
{
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
    /// <summary>The blurred picture is this many times smaller than the screen.</summary>
    public const int Shrink = 3;

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
    public double Brightness { get; private set; }

    public void Poll()
    {
        TakeNewFrame();
        if (_newFrame && _screen != null && DateTime.Now - _lastRead > TimeSpan.FromMilliseconds(16)) Version++;
    }

    /// <summary>False once duplication stopped working (a full-screen exclusive game, a mode change).</summary>
    public bool Working { get; private set; } = true;

    /// <summary>
    /// The blurred picture behind <paramref name="screen"/> (screen pixels), at 1/<see cref="Shrink"/>
    /// size, or null before the first desktop frame. <paramref name="blur"/> is 0–100.
    /// </summary>
    public SKImage? Behind(SKRectI screen, int blur, int paceMs = 33)
    {
        TakeNewFrame();
        if (_screen == null) return null;
        var moved = screen != _imageFor || blur != _imageBlur;
        // A new desktop frame is read at most every paceMs; a moved or resized area at once.
        if (!moved && !(_newFrame && DateTime.Now - _lastRead > TimeSpan.FromMilliseconds(paceMs))) return _image;
        _newFrame = false;
        _lastRead = DateTime.Now;
        var picture = Read(screen, blur);
        if (picture == null) return _image;
        _image?.Dispose();
        (_image, _imageFor, _imageBlur) = (picture, screen, blur);
        Brightness = GlassLight.Of(picture);
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
    private unsafe SKImage? Read(SKRectI screen, int blur)
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
        _gfx.Context.CopySubresourceRegion(_staging, 0, 0, 0, 0, _screen!, 0, new Vortice.Mathematics.Box(left, top, 0, right, bottom, 1));
        var mapped = _gfx.Context.Map(_staging, 0, MapMode.Read);
        try
        {
            return _format switch
            {
                Format.R16G16B16A16_Float => FromHalfFloats(mapped.DataPointer, w, h, (int)mapped.RowPitch, blur),
                Format.B8G8R8A8_UNorm => ShrinkAndBlur(mapped.DataPointer, w, h, (int)mapped.RowPitch, blur),
                _ => null, // a format this doesn't read: Windows' blur instead
            };
        }
        finally
        {
            _gfx.Context.Unmap(_staging, 0);
        }
    }

    /// <summary>
    /// Pixels of the screen (BGRA) to a picture 1/Shrink the size, blurred by blur (0-100). Small
    /// first, then blur: far cheaper than blurring at full size, and the glass is soft anyway.
    /// </summary>
    /// <summary>
    /// An HDR or advanced-colour desktop (linear scRGB in 16-bit floats, 1.0 = SDR white): every
    /// <see cref="Shrink"/>th pixel is turned into ordinary sRGB, which also does the shrinking,
    /// then blurred as usual.
    /// </summary>
    private static unsafe SKImage FromHalfFloats(IntPtr pixels, int w, int h, int rowBytes, int blur)
    {
        int sw = Math.Max(1, w / Shrink), sh = Math.Max(1, h / Shrink);
        var small = new SKBitmap(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
        var target = (uint*)small.GetPixels();
        for (var y = 0; y < sh; y++)
        {
            var row = (Half*)((byte*)pixels + y * Shrink * rowBytes);
            for (var x = 0; x < sw; x++)
            {
                var p = row + x * Shrink * 4;
                target[y * sw + x] = 0xFF000000u | (uint)ToSrgb((float)p[0]) << 16 | (uint)ToSrgb((float)p[1]) << 8 | ToSrgb((float)p[2]);
            }
        }
        using (small)
        {
            var info = small.Info;
            using var surface = SKSurface.Create(info);
            using var paint = new SKPaint();
            var sigma = 0.6f + blur / 100f * 5f;
            paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
            paint.ColorFilter = Vivid;
            surface.Canvas.DrawBitmap(small, 0, 0, paint);
            return surface.Snapshot();
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
    private static readonly SKColorFilter Vivid = Saturate(1.3f, 1.05f);

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

    public static SKImage ShrinkAndBlur(IntPtr pixels, int w, int h, int rowBytes, int blur)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var full = new SKBitmap();
        full.InstallPixels(info, pixels, rowBytes);
        var small = new SKImageInfo(Math.Max(1, w / Shrink), Math.Max(1, h / Shrink), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(small);
        using var paint = new SKPaint();
        var sigma = 0.6f + blur / 100f * 5f;
        paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        paint.ColorFilter = Vivid;
        using var source = SKImage.FromBitmap(full);
        surface.Canvas.DrawImage(source, new SKRect(0, 0, w, h), new SKRect(0, 0, small.Width, small.Height), new SKSamplingOptions(SKFilterMode.Linear), paint);
        return surface.Snapshot();
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
/// so the copy never contains it.
/// </summary>
internal sealed class ScreenCopyBackdrop : IGlassSource
{
    private readonly object _gate = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private SKRectI _want;
    private int _blur;
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
    private double _imageLight, _givenLight;
    public SKRectI Area { get { lock (_gate) return _givenArea; } }
    public double Brightness { get { lock (_gate) return _givenLight; } }
    public void Poll() { }

    public SKImage? Behind(SKRectI screen, int blur, int paceMs = 33)
    {
        lock (_gate)
        {
            // A different area (the window moved) or blur: copy now rather than at the next turn.
            if (screen != _want || blur != _blur) _wake.Set();
            (_want, _blur, _askedAt, _pace) = (screen, blur, DateTime.Now, paceMs);
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
            lock (_gate) (want, blur, asked) = (_want, _blur, _askedAt);
            // Nobody drawing with it (the window hidden, the surface changed): don't copy.
            if (DateTime.Now - asked > TimeSpan.FromSeconds(0.5))
            {
                Thread.Sleep(100);
                continue;
            }
            if (want.Width >= 4 && want.Height >= 4)
            {
                var image = Copy(want, blur);
                if (image == null)
                {
                    Working = false;
                    Log.Write("Glass: the screen copy failed; the lens keeps Windows' blur");
                    return;
                }
                // Same as last time (nothing moved behind): keep the old picture, so nothing redraws.
                var print = Fingerprint(image);
                if (print == _lastPrint && want == _lastWant && blur == _lastBlur)
                {
                    image.Dispose();
                    _wake.WaitOne(_pace);
                    continue;
                }
                (_lastPrint, _lastWant, _lastBlur) = (print, want, blur);
                var light = GlassLight.Of(image);
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
    private long _lastPrint;
    private SKRectI _lastWant;
    private int _lastBlur;

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

    private static unsafe SKImage? Copy(SKRectI r, int blur)
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
            return GlassBackdrop.ShrinkAndBlur(bits, r.Width, r.Height, r.Width * 4, blur);
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
