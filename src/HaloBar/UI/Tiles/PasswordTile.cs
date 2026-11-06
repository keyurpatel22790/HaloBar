using System.Security.Cryptography;
using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Tiles;

/// <summary>
/// Makes a strong random password on the spot, fully offline. Pick the length and which kinds of
/// characters to include; click the password (or "Copy") to put it on the clipboard.
/// </summary>
internal sealed class PasswordTile : Tile
{
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";
    private const string Symbols = "!@#$%^&*()-_=+?";
    private const string Ambiguous = "Il1O0o";

    private string _password = "";

    public PasswordTile(Hub hub) : base(hub) => Regenerate();

    public override string Id => "password";
    public override string Name => "Password";
    public override bool WideByDefault => true;
    public override double FrameInterval => 1;

    private string Pool(string chars) => Settings.PwAvoidAmbiguous ? new string(chars.Where(c => !Ambiguous.Contains(c)).ToArray()) : chars;

    private void Regenerate()
    {
        var pools = new List<string> { Pool(Lower) };
        if (Settings.PwUpper) pools.Add(Pool(Upper));
        if (Settings.PwDigits) pools.Add(Pool(Digits));
        if (Settings.PwSymbols) pools.Add(Pool(Symbols));
        var all = string.Concat(pools);
        var length = Math.Clamp(Settings.PwLength, 4, 64);
        var chars = new char[length];
        // One character from each chosen kind, so every password satisfies the rules, then fill the rest.
        for (var i = 0; i < length; i++)
        {
            var from = i < pools.Count ? pools[i] : all;
            chars[i] = from[RandomNumberGenerator.GetInt32(from.Length)];
        }
        // Shuffle so the guaranteed characters aren't always at the front (Fisher–Yates).
        for (var i = length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        _password = new string(chars);
    }

    private int PoolSize()
    {
        var n = Pool(Lower).Length;
        if (Settings.PwUpper) n += Pool(Upper).Length;
        if (Settings.PwDigits) n += Pool(Digits).Length;
        if (Settings.PwSymbols) n += Pool(Symbols).Length;
        return n;
    }

    /// <summary>Rough entropy in bits: length × log2(how many characters each slot could be).</summary>
    private double Bits => Settings.PwLength * Math.Log2(Math.Max(2, PoolSize()));

    private (string Label, SKColor Colour) Strength()
    {
        var bits = Bits;
        if (bits < 40) return ("Weak", Bad);
        if (bits < 64) return ("Fair", Warn);
        if (bits < 90) return ("Good", Good);
        return ("Strong", Good);
    }

    public override void Draw(Ui ui, SKRect t)
    {
        var wide = t.Width > 200;
        Heading(ui, t, "Password");
        ui.Chip(t.Right - 50, t.Top + 15, "New", Regenerate, size: 11);

        // The password, in a box; click it (or anywhere on it) to copy.
        var box = new SKRect(t.Left + 10, t.Top + 32, t.Right - 10, t.Top + 58);
        ui.Fill(box, 8, Theme.Overlay(ui.Hovering(box) ? (byte)30 : (byte)16));
        ui.Text(_password, box.Left + 10, box.MidY + 4.5f, box.Width - 50, wide ? 13.5f : 11.5f, Theme.Weight.SemiBold, Theme.Text);
        Icons.Copy(ui.Canvas, box.Right - 15, box.MidY, 9, Theme.Dim);
        ui.Hit(box, Copy);

        // Strength bar and label.
        var (label, colour) = Strength();
        var bar = new SKRect(t.Left + 10, t.Top + 66, t.Right - 70, t.Top + 70);
        ui.Bar(bar, (float)Math.Clamp(Bits / 100.0, 0.08, 1), colour);
        ui.Text(label, t.Right - 10, t.Top + 72, 60, 10.5f, Theme.Weight.SemiBold, colour, SKTextAlign.Right);

        // Options: character kinds, then the length control. A little chip-row layout that wraps.
        float cx = t.Left + 10, cy = t.Top + 86;
        void Toggle(string lbl, bool on, Action act)
        {
            var r = ui.Chip(cx, cy, lbl, act, on, 10.5f);
            cx = r.Right + 5;
            if (cx > t.Right - 60) { cx = t.Left + 10; cy += 24; }
        }
        void Save(Action change) { change(); Regenerate(); Settings.Save(); }
        Toggle("A-Z", Settings.PwUpper, () => Save(() => Settings.PwUpper = !Settings.PwUpper));
        Toggle("0-9", Settings.PwDigits, () => Save(() => Settings.PwDigits = !Settings.PwDigits));
        Toggle("#!%", Settings.PwSymbols, () => Save(() => Settings.PwSymbols = !Settings.PwSymbols));
        if (wide) Toggle("No look-alikes", Settings.PwAvoidAmbiguous, () => Save(() => Settings.PwAvoidAmbiguous = !Settings.PwAvoidAmbiguous));

        // Length: − 16 +
        var lenY = cy;
        ui.IconButton(t.Right - 54, lenY + 6, 8, Minus, () => Save(() => Settings.PwLength = Math.Max(4, Settings.PwLength - 1)));
        ui.Text(Settings.PwLength.ToString(), t.Right - 28, lenY + 10, 24, 12, Theme.Weight.SemiBold, Theme.Text, SKTextAlign.Center);
        ui.IconButton(t.Right - 10, lenY + 6, 8, Icons.Plus, () => Save(() => Settings.PwLength = Math.Min(64, Settings.PwLength + 1)));
    }

    private static void Minus(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Round };
        c.DrawLine(cx - size / 2, cy, cx + size / 2, cy, p);
    }

    private void Copy()
    {
        if (Hub.Clipboard.CopyText(_password)) Hub.ShowHud(new Hud(HudIcon.Copied, "Password copied"));
    }
}
