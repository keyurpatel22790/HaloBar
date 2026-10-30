using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Tiles;

/// <summary>
/// The pet's own little room: it potters about, shows how happy it is, and can be fed and played
/// with (Feed, Play) or petted (click it). Happiness is the same as the island's pet and is saved.
/// </summary>
internal sealed class PetTile(Hub hub) : Tile(hub)
{
    private readonly PetBody _body = new();
    private DateTime _last = DateTime.Now;

    public override string Id => "pet";
    public override string Name => "Pet";
    public override double FrameInterval => 0.08;

    public override void Draw(Ui ui, SKRect t)
    {
        var look = PetLook.Find(Settings.Pet) ?? PetLook.All[0];
        var now = DateTime.Now;
        var dt = Math.Clamp((now - _last).TotalSeconds, 0, 0.1);
        _last = now;

        // The room: a soft wall, a floor, a window of light.
        var floor = t.Top + t.Height * 0.72f;
        using (var wall = new SKPaint { IsAntialias = true })
        {
            wall.Shader = SKShader.CreateLinearGradient(new SKPoint(t.MidX, t.Top), new SKPoint(t.MidX, floor),
                [Theme.Accent.WithAlpha(40), Theme.Accent.WithAlpha(10)], SKShaderTileMode.Clamp);
            ui.Canvas.DrawRect(new SKRect(t.Left, t.Top, t.Right, floor), wall);
            wall.Shader = null;
            wall.Color = Theme.Overlay(22);
            ui.Canvas.DrawRect(new SKRect(t.Left, floor, t.Right, t.Bottom), wall);
        }

        // Happiness: five hearts at the top left.
        var mood = Hub.Pet.Happiness;
        for (var i = 0; i < 5; i++)
        {
            var filled = mood >= (i + 1) * 20 - 10;
            DrawHeart(ui.Canvas, t.Left + 12 + i * 11, t.Top + 12, filled ? new SKColor(0xFF, 0x5A, 0x7A) : Theme.Faint);
        }
        ui.Text(mood > 70 ? "Happy" : mood > 35 ? "Content" : "Lonely", t.Right - 10, t.Top + 20, 70, 10, Theme.Weight.SemiBold, Theme.Dim, SKTextAlign.Right);

        // The pet itself, strolling the floor.
        var px = Math.Clamp(MathF.Floor((t.Height - 50) / 13f), 3, 6);
        var half = PetArt.Width * px / 2 + 6;
        _body.Step(dt, t.Left + half, t.Right - half, Hub.Pet, Hub.Media.Snapshot?.IsPlaying == true, Hub.Volume.Peak, false, PetMind.Quiet);
        var x = (float)_body.X;
        if (now < Hub.Pet.EatingUntil) DrawBowl(ui.Canvas, x + (_body.FacingLeft ? -half - 6 : half + 6), floor + 8, px);
        if (now < Hub.Pet.PlayingUntil)
        {
            var k = (Hub.Pet.PlayingUntil - now).TotalSeconds;
            var bx = (float)(t.MidX + Math.Sin(k * 2.3) * (t.Width / 2 - 20));
            var by = floor + 6 - (float)Math.Abs(Math.Sin(k * 6) * 18);
            using var ball = new SKPaint { IsAntialias = true, Color = new SKColor(0x4F, 0xA3, 0xFF) };
            ui.Canvas.DrawCircle(bx, by, px * 1.4f, ball);
        }
        PetArt.Draw(ui.Canvas, look, _body, x, floor + 8, px, _body.Time);
        ui.Hit(new SKRect(x - half, floor - PetArt.Height * px - 10, x + half, floor + 10), Hub.Pet.Pet);

        // Feed and Play.
        var chipY = t.Bottom - 15;
        var feed = ui.Chip(t.Left + 8, chipY, "Feed", Hub.Pet.Feed, size: 10.5f);
        ui.Chip(feed.Right + 5, chipY, "Play", Hub.Pet.Play, size: 10.5f);
    }

    private static void DrawHeart(SKCanvas c, float x, float y, SKColor colour)
    {
        using var paint = new SKPaint { Color = colour, IsAntialias = false };
        string[] heart = [".x.x.", "xxxxx", ".xxx.", "..x.."];
        for (var r = 0; r < heart.Length; r++)
            for (var q = 0; q < 5; q++)
                if (heart[r][q] == 'x') c.DrawRect(x + q * 1.8f, y + r * 1.8f, 1.82f, 1.82f, paint);
    }

    private static void DrawBowl(SKCanvas c, float cx, float bottom, float px)
    {
        using var paint = new SKPaint { IsAntialias = false, Color = new SKColor(0xE0, 0x5A, 0x4A) };
        c.DrawRect(cx - px * 2.5f, bottom - px * 1.5f, px * 5, px * 1.5f, paint);
        paint.Color = new SKColor(0x8B, 0x5A, 0x2B);
        c.DrawRect(cx - px * 2, bottom - px * 2.2f, px * 4, px * 0.8f, paint);
    }
}
