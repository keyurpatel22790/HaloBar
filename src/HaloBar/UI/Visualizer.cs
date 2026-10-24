using SkiaSharp;

namespace HaloBar.UI;

/// <summary>
/// The little equaliser beside the artwork. Its bars follow the real output level, each with its
/// own wobble so they don't move in lockstep. Drawn as rounded bars or as columns of square pixels.
/// </summary>
internal sealed class Visualizer
{
    private readonly float[] _bars = new float[4];
    private double _clock;

    public void Update(double dt, bool playing, float peak)
    {
        _clock += dt;
        var level = playing ? Math.Clamp(peak * 1.6f, 0f, 1f) : 0f;
        for (var i = 0; i < _bars.Length; i++)
        {
            var wobble = 0.55f + 0.45f * (float)Math.Abs(Math.Sin(_clock * (5.1 + i * 1.7) + i * 1.3));
            var target = playing ? Math.Max(0.18f, level * wobble) : 0.12f;
            var k = target > _bars[i] ? 0.55f : 0.18f;
            _bars[i] += (target - _bars[i]) * Math.Min(1f, k * (float)(dt * 60));
        }
    }

    /// <summary>Draws the bars from <paramref name="left"/>, centred on <paramref name="midY"/>.</summary>
    public void Draw(SKCanvas c, float left, float midY, float maxHeight, SKColor color, bool pixels)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        for (var i = 0; i < _bars.Length; i++)
        {
            var x = left + i * 5.5f;
            if (pixels)
            {
                // Columns of 3×3 pixels growing from the middle.
                var cells = Math.Max(1, (int)Math.Round(_bars[i] * maxHeight / 4));
                var top = midY - cells * 4 / 2f;
                for (var k = 0; k < cells; k++)
                    c.DrawRect(x, top + k * 4, 3, 3, paint);
                continue;
            }
            var h = Math.Max(3, _bars[i] * maxHeight);
            c.DrawRoundRect(new SKRect(x, midY - h / 2, x + 3, midY + h / 2), 1.5f, 1.5f, paint);
        }
    }
}
