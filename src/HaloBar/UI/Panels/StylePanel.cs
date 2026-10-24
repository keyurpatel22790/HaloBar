using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>Surfaces, accents and the music card's background, as swatches.</summary>
internal sealed class StylePanel(Hub hub) : Panel(hub)
{
    public override string Title => "Style";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Palette(c, cx, cy, size, color);
    public override double FrameInterval => 0.25;

    public override void Draw(Ui ui, SKRect body)
    {
        var x0 = body.Left + 20;

        string? hovered = null;
        var y = body.Top + 34;
        for (var i = 0; i < Looks.Surfaces.Length; i++)
        {
            var look = Looks.Surfaces[i];
            var cx = x0 + 12 + i * 34;
            if (ui.Hovering(Ui.Square(cx, y, 15))) hovered = look.Name;
            SurfaceSwatch(ui, look, cx, y, 12);
            if (look == Theme.Surface) ui.Ring(cx, y, 15.5f, Theme.Text);
            ui.Hit(Ui.Square(cx, y, 16), () => Theme.Choose(surface: look));
        }
        Heading(ui, "Surface", hovered ?? Theme.Surface.Name, x0, body.Top + 12);

        hovered = null;
        y = body.Top + 86;
        for (var i = 0; i < Looks.Accents.Length; i++)
        {
            var look = Looks.Accents[i];
            var cx = x0 + 11 + i * 30;
            if (ui.Hovering(Ui.Square(cx, y, 13))) hovered = look.Name;
            AccentSwatch(ui, look, cx, y, 10);
            if (look.Id == Theme.AccentChoice.Id) ui.Ring(cx, y, 13.5f, Theme.Text);
            ui.Hit(Ui.Square(cx, y, 14), () => Theme.Choose(accent: look));
        }
        Heading(ui, "Accent", hovered ?? Theme.AccentChoice.Name, x0, body.Top + 64);

        ui.Text("Music card", x0, body.Top + 125, 90, 12, Theme.Weight.SemiBold);
        var x = x0 + 92;
        x = ui.Chip(x, body.Top + 120, "Plain", () => Theme.Choose(albumCard: false), !Theme.AlbumCard).Right + 6;
        ui.Chip(x, body.Top + 120, "Album colours", () => Theme.Choose(albumCard: true), Theme.AlbumCard);
    }

    private void Heading(Ui ui, string title, string value, float x, float baseline)
    {
        ui.Text(title, x, baseline, 90, 12, Theme.Weight.SemiBold);
        ui.Text("·  " + value, x + ui.Measure(title, 12, Theme.Weight.SemiBold) + 8, baseline, 260, 12, Theme.Weight.Regular, Theme.Dim);
    }

    private void SurfaceSwatch(Ui ui, SurfaceLook look, float cx, float cy, float radius)
    {
        var box = Ui.Square(cx, cy, radius);
        using var paint = new SKPaint { IsAntialias = true };
        switch (look.Effect)
        {
            case SurfaceEffect.Aurora:
                using (var ring = Hues(cx, cy, 60, 70)) { paint.Shader = ring; ui.Canvas.DrawOval(box, paint); }
                break;
            case SurfaceEffect.Album:
                PaletteDisc(ui, box);
                break;
            default:
                using (var fill = SKShader.CreateLinearGradient(new SKPoint(cx, box.Top), new SKPoint(cx, box.Bottom), [look.Top, look.Bottom], SKShaderTileMode.Clamp))
                {
                    paint.Shader = fill;
                    ui.Canvas.DrawOval(box, paint);
                }
                ui.Circle(cx + radius * 0.48f, cy + radius * 0.48f, radius * 0.3f, look.Accent);
                break;
        }
        ui.Ring(cx, cy, radius, Theme.Overlay(70), 1);
    }

    private void AccentSwatch(Ui ui, AccentLook look, float cx, float cy, float radius)
    {
        var box = Ui.Square(cx, cy, radius);
        using var paint = new SKPaint { IsAntialias = true };
        switch (look.Source)
        {
            case AccentSource.Rainbow:
                using (var hues = Hues(cx, cy, 70, 100)) { paint.Shader = hues; ui.Canvas.DrawOval(box, paint); }
                break;
            case AccentSource.Album:
                PaletteDisc(ui, box);
                break;
            case AccentSource.Surface:
                ui.Circle(cx, cy, radius, Theme.Surface.Accent);
                ui.Text("A", cx, cy + radius * 0.38f, radius * 2, radius * 1.05f, Theme.Weight.Bold, SKColors.White, SKTextAlign.Center);
                break;
            case AccentSource.Windows:
                ui.Circle(cx, cy, radius, Theme.WindowsAccentColor);
                paint.Color = SKColors.White.WithAlpha(230);
                var q = radius * 0.28f;
                foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                    ui.Canvas.DrawRect(SKRect.Create(cx + (dx < 0 ? -q - 0.6f : 0.6f), cy + (dy < 0 ? -q - 0.6f : 0.6f), q, q), paint);
                break;
            default:
                ui.Circle(cx, cy, radius, look.Color);
                break;
        }
        ui.Ring(cx, cy, radius, Theme.Overlay(70), 1);
    }

    /// <summary>The playing cover's colours as slices of a disc, or a colour wheel when nothing plays.</summary>
    private void PaletteDisc(Ui ui, SKRect box)
    {
        using var paint = new SKPaint { IsAntialias = true };
        if (Hub.Media.Snapshot?.Palette is { } p)
        {
            for (var i = 0; i < p.Swatches.Length; i++)
            {
                paint.Color = p.Swatches[i];
                ui.Canvas.DrawArc(box, -90 + i * 120, 120, true, paint);
            }
            return;
        }
        using var hues = Hues(box.MidX, box.MidY, 45, 90);
        paint.Shader = hues;
        ui.Canvas.DrawOval(box, paint);
    }

    private static SKShader Hues(float cx, float cy, float saturation, float value)
    {
        var colors = Enumerable.Range(0, 7).Select(i => SKColor.FromHsv(i * 60 % 360, saturation, value)).ToArray();
        return SKShader.CreateSweepGradient(new SKPoint(cx, cy), colors);
    }
}
