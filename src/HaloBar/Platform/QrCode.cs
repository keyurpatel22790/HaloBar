using QRCoder;
using SkiaSharp;

namespace HaloBar.Platform;

/// <summary>Draws a QR code for a bit of text straight onto the canvas (e.g. to send a link to a phone).</summary>
internal static class QrCode
{
    public static void Draw(SKCanvas canvas, string text, SKRect box)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        var n = rows.Count;
        if (n == 0) return;
        var side = Math.Min(box.Width, box.Height);
        var cell = side / (n + 2); // a one-module quiet border all round
        var ox = box.MidX - side / 2 + cell;
        var oy = box.MidY - side / 2 + cell;
        using var bg = new SKPaint { Color = SKColors.White, IsAntialias = false };
        canvas.DrawRoundRect(box.MidX - side / 2, box.MidY - side / 2, side, side, 6, 6, bg);
        using var fg = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                if (rows[y][x])
                    canvas.DrawRect(ox + x * cell, oy + y * cell, cell + 0.7f, cell + 0.7f, fg);
    }
}
