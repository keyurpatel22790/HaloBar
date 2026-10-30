using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI;

/// <summary>The pet's coat: outline, fur, belly and paws, eyes.</summary>
internal sealed record PetLook(string Id, string Name, SKColor Outline, SKColor Fur, SKColor Belly, SKColor Eye)
{
    public static readonly PetLook[] All =
    [
        new("ginger", "Ginger cat", new(0x3A, 0x2A, 0x1E), new(0xF0, 0xA0, 0x4B), new(0xFF, 0xF2, 0xE2), new(0x1B, 0x1B, 0x1F)),
        new("grey", "Grey cat", new(0x2A, 0x2E, 0x34), new(0x9A, 0xA3, 0xAE), new(0xEE, 0xF1, 0xF4), new(0x1B, 0x1B, 0x1F)),
        new("black", "Black cat", new(0x0C, 0x0C, 0x10), new(0x2C, 0x2C, 0x33), new(0x46, 0x46, 0x50), new(0xF5, 0xC5, 0x42)),
        new("snowy", "Snowy cat", new(0x5A, 0x5A, 0x66), new(0xF6, 0xF6, 0xF8), new(0xFF, 0xFF, 0xFF), new(0x3A, 0x6E, 0xD8)),
    ];

    public static PetLook? Find(string id) => All.FirstOrDefault(l => l.Id == id);
}

internal enum PetPose { Sit, Blink, Walk1, Walk2, Sleep, Jump, Happy }

/// <summary>
/// The pet's feelings, shared by the island and the Pet tile: how happy it is (saved, slowly
/// fading over hours), and what just happened to it (petted, fed, played with, startled).
/// </summary>
internal sealed class PetMind(AppSettings settings)
{
    public DateTime HeartsUntil { get; private set; }
    public DateTime JumpUntil { get; private set; }
    public DateTime EatingUntil { get; private set; }
    public DateTime PlayingUntil { get; private set; }

    /// <summary>0–100: drops about 6 an hour; petting, feeding and playing bring it back.</summary>
    public double Happiness
    {
        get
        {
            var hours = (DateTime.Now - settings.PetMoodAt).TotalHours;
            return Math.Clamp(settings.PetHappiness - Math.Max(0, hours) * 6, 0, 100);
        }
    }

    private void Raise(double by)
    {
        settings.PetHappiness = Math.Clamp(Happiness + by, 0, 100);
        settings.PetMoodAt = DateTime.Now;
        settings.Save();
    }

    public void Pet()
    {
        HeartsUntil = DateTime.Now.AddSeconds(1.6);
        Raise(6);
    }

    public void Feed()
    {
        EatingUntil = DateTime.Now.AddSeconds(2.5);
        HeartsUntil = DateTime.Now.AddSeconds(3);
        Raise(25);
    }

    public void Play()
    {
        PlayingUntil = DateTime.Now.AddSeconds(3);
        Raise(15);
    }

    /// <summary>Nobody has touched the keyboard or mouse for a few minutes, or it's the middle of the night.</summary>
    public static bool Quiet
    {
        get
        {
            var info = new LASTINPUTINFO { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
            var idle = GetLastInputInfo(ref info) ? (Environment.TickCount64 - info.Time) / 1000.0 : 0;
            return idle > 180 || DateTime.Now.Hour is >= 1 and < 6;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint Size; public uint Time; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Something happened (a notification): it jumps.</summary>
    public void Startle() => JumpUntil = DateTime.Now.AddSeconds(0.7);
}

/// <summary>
/// One pet on screen: where it is, which way it faces, and what it's doing right now. The island
/// and the Pet tile each have one; they share the <see cref="PetMind"/>.
/// </summary>
internal sealed class PetBody
{
    private static readonly Random Dice = new();
    private double _x = double.NaN, _target, _until, _blinkAt = 2, _time;
    private bool _walking;

    public bool FacingLeft { get; private set; }
    public double X => _x;
    public PetPose Pose { get; private set; } = PetPose.Sit;
    /// <summary>Up-and-down offset in pixels of the sprite (a hop, a bob to music).</summary>
    public float Lift { get; private set; }
    public bool Asleep { get; private set; }
    public double Time => _time;

    /// <summary>
    /// Moves the pet on along its lane (<paramref name="from"/>–<paramref name="to"/>, in DIPs).
    /// It rests, blinks, and now and then strolls; music makes it bob, the focus timer keeps it
    /// still, and when nobody's around (or late at night) it sleeps.
    /// </summary>
    public void Step(double dt, double from, double to, PetMind mind, bool music, float beat, bool focus, bool idle)
    {
        _time += dt;
        if (double.IsNaN(_x)) _x = _target = (from + to) / 2;
        _x = Math.Clamp(_x, from, to);
        var now = DateTime.Now;
        Asleep = idle && !music;
        Lift = 0;

        if (now < mind.JumpUntil)
        {
            var k = (mind.JumpUntil - now).TotalSeconds / 0.7;
            Lift = (float)(Math.Sin(k * Math.PI) * 7);
            Pose = PetPose.Jump;
            return;
        }
        if (now < mind.HeartsUntil || now < mind.EatingUntil)
        {
            Pose = PetPose.Happy;
            return;
        }
        if (now < mind.PlayingUntil)
        {
            // Bounding about after the ball.
            Lift = (float)Math.Abs(Math.Sin(_time * 9) * 5);
            Pose = (int)(_time * 8) % 2 == 0 ? PetPose.Walk1 : PetPose.Walk2;
            FacingLeft = Math.Sin(_time * 1.7) < 0;
            return;
        }
        if (Asleep)
        {
            Pose = PetPose.Sleep;
            _walking = false;
            return;
        }
        if (music)
        {
            // Nodding along: a little hop on each beat.
            Lift = (float)(Math.Abs(Math.Sin(_time * 7.5)) * (1.5 + beat * 3));
            Pose = PetPose.Sit;
            return;
        }

        _blinkAt -= dt;
        if (_blinkAt < -0.14) _blinkAt = 2 + Dice.NextDouble() * 4;

        if (_walking)
        {
            var step = 16 * dt * Math.Sign(_target - _x);
            if (Math.Abs(_target - _x) <= Math.Abs(step))
            {
                _x = _target;
                _walking = false;
                _until = _time + 4 + Dice.NextDouble() * 7;
            }
            else _x += step;
            Pose = (int)(_time * 6) % 2 == 0 ? PetPose.Walk1 : PetPose.Walk2;
            return;
        }
        if (!focus && _time > _until && to - from > 8)
        {
            _target = from + Dice.NextDouble() * (to - from);
            if (Math.Abs(_target - _x) > 6)
            {
                _walking = true;
                FacingLeft = _target < _x;
            }
            else _until = _time + 3;
        }
        Pose = _blinkAt < 0 ? PetPose.Blink : PetPose.Sit;
    }

    /// <summary>How often it needs a new frame (seconds).</summary>
    public double FrameInterval => Asleep ? 0.4 : Pose == PetPose.Sit ? 0.12 : 1 / 15.0;
}

/// <summary>The pet's pixel art, drawn square by square, with a tail that swishes and little effects.</summary>
internal static class PetArt
{
    // 12 × 11 pixels, front view. k outline, f fur, w belly and paws, p pink, e eye, h eye glint.
    private static readonly string[] Head =
    [
        "..k......k..",
        ".kpk....kpk.",
        ".kffkkkkffk.",
        "kffffffffffk",
    ];
    private static readonly string[] EyesOpen = ["kfheffffhefk", "kfeeffffeefk"];
    private static readonly string[] EyesShut = ["kffffffffffk", "kfkkffffkkfk"];
    private static readonly string[] Body =
    [
        "kffffppffffk",
        ".kffwwwwffk.",
        "..kfwwwwfk..",
        ".kffwwwwffk.",
    ];
    private static readonly string[] Paws = ["..kwk..kwk..", "..kwk...kwk.", ".kwk...kwk..", "kwk......kwk", ".kkkkkkkkkk."];

    public const int Width = 12, Height = 11;

    /// <summary>
    /// Draws the pet with its paws on <paramref name="bottom"/>, centred on <paramref name="cx"/>,
    /// each art pixel <paramref name="px"/> DIPs.
    /// </summary>
    public static void Draw(SKCanvas c, PetLook look, PetBody body, float cx, float bottom, float px, double time)
    {
        var pose = body.Pose;
        var rows = new List<string>(Height);
        rows.AddRange(Head);
        var shut = pose is PetPose.Blink or PetPose.Sleep or PetPose.Happy;
        rows.Add(shut ? EyesShut[0] : EyesOpen[0]);
        rows.Add(shut ? EyesShut[1] : EyesOpen[1]);
        rows.AddRange(Body.Take(pose == PetPose.Sleep ? 3 : 4));
        rows.Add(pose switch
        {
            PetPose.Walk1 => Paws[1],
            PetPose.Walk2 => Paws[2],
            PetPose.Jump => Paws[3],
            PetPose.Sleep => Paws[4],
            _ => Paws[0],
        });

        var height = rows.Count * px;
        var left = cx - Width * px / 2;
        var top = bottom - height - body.Lift;
        var flip = body.FacingLeft;
        using var paint = new SKPaint { IsAntialias = false };

        DrawTail(c, look, left, top, px, time, flip, pose == PetPose.Sleep);
        for (var y = 0; y < rows.Count; y++)
        {
            var row = rows[y];
            for (var x = 0; x < Width; x++)
            {
                var ch = row[flip ? Width - 1 - x : x];
                SKColor? colour = ch switch
                {
                    'k' => look.Outline,
                    'f' => look.Fur,
                    'w' => look.Belly,
                    'p' => new SKColor(0xF4, 0xA7, 0xB9),
                    'e' => look.Eye,
                    'h' => SKColors.White,
                    _ => null,
                };
                if (colour is not { } col) continue;
                paint.Color = col;
                c.DrawRect(left + x * px, top + y * px, px + 0.02f, px + 0.02f, paint);
            }
        }

        // Little extras: Zzz while asleep, hearts when happy, a food bowl while eating.
        if (pose == PetPose.Sleep)
        {
            for (var i = 0; i < 3; i++)
            {
                var k = (float)((time * 0.6 + i / 3.0) % 1);
                paint.Color = SKColors.White.WithAlpha((byte)(220 * (1 - k)));
                DrawZ(c, left + Width * px + 1 + k * 4 * px, top + px * 2 - k * 6 * px, px * (0.6f + k * 0.4f), paint);
            }
        }
        if (pose == PetPose.Happy)
        {
            for (var i = 0; i < 2; i++)
            {
                var k = (float)((time * 0.8 + i / 2.0) % 1);
                paint.Color = new SKColor(0xFF, 0x5A, 0x7A, (byte)(255 * (1 - k)));
                DrawHeart(c, left + (i == 0 ? -px : Width * px - 2 * px), top - k * 7 * px, px * 0.7f, paint);
            }
        }
    }

    /// <summary>A tail of squares curling from the hip, swishing slowly (curled round while asleep).</summary>
    private static void DrawTail(SKCanvas c, PetLook look, float left, float top, float px, double time, bool flip, bool asleep)
    {
        using var paint = new SKPaint { IsAntialias = false };
        var swing = asleep ? 0 : Math.Sin(time * 2.4) * 0.45;
        var baseX = flip ? left + px * 0.5f : left + (Width - 0.5f) * px;
        var baseY = top + 8.5f * px;
        var dir = flip ? -1 : 1;
        for (var i = 0; i < 5; i++)
        {
            var a = -Math.PI / 2 * (i / 4.0) * (asleep ? 0.2 : 1) + swing * (i / 4.0);
            var x = baseX + dir * (float)(Math.Cos(a) * (i + 1) * px * 0.95);
            var y = baseY + (float)(Math.Sin(a) * (i + 1) * px * 0.95);
            paint.Color = look.Outline;
            c.DrawRect(x - px * 0.6f, y - px * 0.6f, px * 1.2f, px * 1.2f, paint);
            paint.Color = i == 4 ? look.Belly : look.Fur;
            c.DrawRect(x - px * 0.4f, y - px * 0.4f, px * 0.8f, px * 0.8f, paint);
        }
    }

    private static void DrawZ(SKCanvas c, float x, float y, float s, SKPaint paint)
    {
        c.DrawRect(x, y, 3 * s, s, paint);
        c.DrawRect(x + 2 * s, y + s, s, s, paint);
        c.DrawRect(x + s, y + 2 * s, s, s, paint);
        c.DrawRect(x, y + 3 * s, 3 * s, s, paint);
    }

    private static void DrawHeart(SKCanvas c, float x, float y, float s, SKPaint paint)
    {
        string[] heart = [".x.x.", "xxxxx", ".xxx.", "..x.."];
        for (var r = 0; r < heart.Length; r++)
            for (var q = 0; q < 5; q++)
                if (heart[r][q] == 'x') c.DrawRect(x + q * s, y + r * s, s + 0.02f, s + 0.02f, paint);
    }
}
