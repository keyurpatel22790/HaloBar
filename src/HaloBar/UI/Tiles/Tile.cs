using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Tiles;

/// <summary>
/// One widget on the Home panel. A tile draws itself into the rectangle it is given (already
/// filled and clipped to a rounded card), takes clicks through <see cref="Ui"/>, and can ask for
/// a line of text with <see cref="Ask"/>. Tiles are per island, so typing state stays per monitor.
/// </summary>
internal abstract class Tile(Hub hub)
{
    protected Hub Hub { get; } = hub;
    protected Theme Theme => Hub.Theme;
    protected AppSettings Settings => Hub.Settings;

    public abstract string Id { get; }
    public abstract string Name { get; }
    /// <summary>Takes two slots when first added (tools that need room to type).</summary>
    public virtual bool WideByDefault => false;
    /// <summary>How soon the tile wants a redraw while shown, in seconds.</summary>
    public virtual double FrameInterval => 1;

    /// <summary>Set when something arrived (a web answer) that should show now, not at the next tick.</summary>
    public bool Fresh { get; set; }
    protected void Changed() => Fresh = true;

    public abstract void Draw(Ui ui, SKRect r);

    // ───────────────────────────── shared helpers ─────────────────────────────

    /// <summary>Starts typing into <paramref name="field"/>, pre-filled with <paramref name="text"/>.</summary>
    protected static void Ask(Ui ui, TextField field, string text = "")
    {
        field.Text = text;
        field.Caret = text.Length;
        ui.Focus(field);
    }

    /// <summary>The whole tile as a prompt: a title, the text box, and how to finish.</summary>
    protected void DrawAsk(Ui ui, SKRect t, TextField field, string title)
    {
        ui.Text(title, t.Left + 12, t.Top + 22, t.Width - 24, 11.5f, Theme.Weight.SemiBold, Theme.Dim);
        ui.Field(field, new SKRect(t.Left + 8, t.MidY - 14, t.Right - 8, t.MidY + 14), 12);
        ui.Text("Enter to save · Esc to cancel", t.MidX, t.Bottom - 12, t.Width - 12, 10, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
    }

    /// <summary>The coloured heading most tiles start with.</summary>
    protected void Heading(Ui ui, SKRect t, string text) =>
        ui.Text(text, t.Left + 12, t.Top + 22, t.Width - 24, 12, Theme.Weight.SemiBold, Theme.Accent);

    protected void Note(Ui ui, SKRect t, string headline, string hint)
    {
        ui.Text(headline, t.MidX, t.MidY + 2, t.Width - 16, 12, Theme.Weight.SemiBold, Theme.Dim, SKTextAlign.Center);
        if (hint.Length > 0) ui.Text(hint, t.MidX, t.MidY + 20, t.Width - 16, 10.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
    }

    protected static readonly SKColor Good = new(0x34, 0xC7, 0x59), Warn = new(0xFF, 0x9F, 0x0A), Bad = new(0xFF, 0x5A, 0x4F);
}

/// <summary>Every tile there is, in the order the editor lists them.</summary>
internal static class TileCatalogue
{
    public static List<Tile> Create(Hub hub) =>
    [
        new DateTile(hub), new WeatherTile(hub), new AirSunTile(hub), new StatsTile(hub), new TimerTile(hub),
        new ClockTile(hub), new TodoTile(hub), new BatteryTile(hub), new BatteryHealthTile(hub), new NextUpTile(hub), new MonthTile(hub),
        new ScreenTimeTile(hub), new DiskTile(hub), new NetworkTile(hub), new SystemTile(hub),
        new NoteTile(hub), new CountdownTile(hub), new CounterTile(hub), new StopwatchTile(hub),
        new CalculatorTile(hub), new ConverterTile(hub), new TranslateTile(hub), new DictionaryTile(hub),
        new CurrencyTile(hub), new PasswordTile(hub), new EmojiTile(hub), new TickerTile(hub), new GitTile(hub), new DockerTile(hub), new PhotosTile(hub),
    ];
}
