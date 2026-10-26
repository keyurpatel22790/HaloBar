using System.Globalization;
using System.Text.RegularExpressions;
using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Tiles;

/// <summary>
/// A tool you type into: the text box sits on the tile, Enter keeps it open for the next sum or
/// word, and the answer shows as you type.
/// </summary>
internal abstract class TypingTile : Tile
{
    protected readonly TextField Input;
    private bool _refocus;
    private string _seen = "";
    private DateTime _changedAt;

    protected TypingTile(Hub hub, string placeholder) : base(hub) =>
        Input = new TextField(placeholder, text =>
        {
            Entered(text);
            _refocus = true;
        });

    /// <summary>Enter was pressed with <paramref name="text"/>.</summary>
    protected virtual void Entered(string text) { }

    /// <summary>The text has been still for a moment (for look-ups that go to the web).</summary>
    protected virtual void Settled(string text) { }

    protected bool Typing(Ui ui) => ui.Focused == Input;

    /// <summary>Draws the text box and keeps typing going after Enter.</summary>
    protected void DrawInput(Ui ui, SKRect r, float size = 12)
    {
        if (_refocus)
        {
            _refocus = false;
            ui.Focus(Input);
        }
        ui.Field(Input, r, size);
        if (Input.Text != _seen)
        {
            _seen = Input.Text;
            _changedAt = DateTime.Now;
        }
        else if (_changedAt != default && DateTime.Now - _changedAt > TimeSpan.FromSeconds(0.7))
        {
            _changedAt = default;
            Settled(Input.Text.Trim());
        }
    }

    public override double FrameInterval => _changedAt != default ? 0.2 : 1;

    protected void Copy(string text)
    {
        if (Hub.Clipboard.CopyText(text)) Hub.ShowHud(new Hud(HudIcon.Copied, "Copied", Detail: text));
    }
}

internal sealed class CalculatorTile(Hub hub) : TypingTile(hub, "18% of 2500")
{
    private readonly List<(string Sum, string Answer)> _history = [];

    public override string Id => "calc";
    public override string Name => "Calculator";

    protected override void Entered(string text)
    {
        if (!Calculator.TryEvaluate(text, out var value)) return;
        var answer = Calculator.Format(value);
        _history.Insert(0, (text.Trim(), answer));
        if (_history.Count > 6) _history.RemoveAt(6);
        Input.Text = answer.Replace(",", "");
        Input.Caret = Input.Text.Length;
    }

    public override void Draw(Ui ui, SKRect t)
    {
        var wide = t.Width > 200;
        var left = wide ? new SKRect(t.Left, t.Top, t.MidX + 20, t.Bottom) : t;
        Heading(ui, left, "Calculator");
        DrawInput(ui, new SKRect(left.Left + 8, t.Top + 32, left.Right - 8, t.Top + 58));
        var ok = Calculator.TryEvaluate(Input.Text, out var value);
        if (ok && Input.Text.Trim() != Calculator.Format(value).Replace(",", ""))
        {
            var answer = Calculator.Format(value);
            ui.Text($"= {answer}", left.Right - 12, t.Top + 92, left.Width - 24, 20, Theme.Weight.Bold, Theme.Text, SKTextAlign.Right);
            ui.Hit(new SKRect(left.Left, t.Top + 66, left.Right, t.Top + 100), () => Copy(answer.Replace(",", "")));
        }
        else if (Input.Text.Length == 0 && !Typing(ui))
            ui.Text("+ − × ÷ ^ % √ sin", left.MidX, t.Top + 88, left.Width - 16, 10.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
        if (!wide)
        {
            if (_history.Count > 0)
                ui.Text($"{_history[0].Sum} = {_history[0].Answer}", t.Left + 12, t.Bottom - 10, t.Width - 24, 9.5f, Theme.Weight.Regular, Theme.Dim);
            return;
        }
        var x = left.Right + 6;
        var y = t.Top + 22;
        if (_history.Count == 0) ui.Text("Enter keeps the answer", x, y, t.Right - x - 10, 10, Theme.Weight.Regular, Theme.Faint);
        foreach (var (sum, ans) in _history)
        {
            if (y > t.Bottom - 6) break;
            ui.Text(sum, x, y, t.Right - x - 10, 9.5f, Theme.Weight.Regular, Theme.Dim);
            ui.Text($"= {ans}", x, y + 12, t.Right - x - 10, 11, Theme.Weight.SemiBold);
            var chosen = ans;
            ui.Hit(new SKRect(x - 4, y - 10, t.Right, y + 16), () => Copy(chosen.Replace(",", "")));
            y += 30;
        }
    }
}

/// <summary>Units, temperatures and currencies: "5 km to mi", "100 f to c", "250 usd to inr".</summary>
internal sealed class ConverterTile(Hub hub) : TypingTile(hub, "5 km to mi")
{
    private static readonly Regex Money = new(@"^\s*([\d.,]+)\s*([a-z]{3})\s+(?:to|in)?\s*([a-z]{3})\s*$", RegexOptions.IgnoreCase);
    private string _moneyFor = "", _money = "";

    public override string Id => "convert";
    public override string Name => "Unit converter";

    protected override void Settled(string text)
    {
        var m = Money.Match(text);
        if (!m.Success) return;
        var from = m.Groups[2].Value.ToUpperInvariant();
        var to = m.Groups[3].Value.ToUpperInvariant();
        if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var rates = await WebTools.RatesAsync(from);
                if (rates.TryGetValue(to, out var rate)) (_moneyFor, _money) = (text, $"{WebTools.Money(amount * rate)} {to}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                (_moneyFor, _money) = (text, "Rates unavailable");
            }
        });
    }

    public override void Draw(Ui ui, SKRect t)
    {
        Heading(ui, t, "Convert");
        DrawInput(ui, new SKRect(t.Left + 8, t.Top + 32, t.Right - 8, t.Top + 58));
        var text = Input.Text.Trim();
        var answer = UnitConverter.Convert(text) ?? (_moneyFor == text ? _money : null);
        if (answer != null)
        {
            ui.Text(answer, t.Right - 12, t.Top + 92, t.Width - 24, 18, Theme.Weight.Bold, Theme.Text, SKTextAlign.Right);
            var number = answer.Split(' ')[0];
            ui.Hit(new SKRect(t.Left, t.Top + 66, t.Right, t.Top + 100), () => Copy(number.Replace(",", "")));
        }
        var hint = t.Width > 200 ? "km mi ft · kg lb · °C °F · L gal · GB MB · USD INR" : "km · kg · °C · GB · USD";
        ui.Text(hint, t.MidX, t.Bottom - 10, t.Width - 16, 9.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
    }
}

/// <summary>Translate as you type, between the languages you pick.</summary>
internal sealed class TranslateTile(Hub hub) : TypingTile(hub, "Type to translate")
{
    private string? _picking;
    private string _result = "", _resultFor = "", _detected = "";
    private volatile bool _busy;

    public override string Id => "translate";
    public override string Name => "Translator";
    public override bool WideByDefault => true;

    protected override void Entered(string text) => Settled(text.Trim());

    protected override void Settled(string text)
    {
        if (text.Length == 0)
        {
            _result = _resultFor = "";
            return;
        }
        var key = $"{Settings.TranslateFrom}|{Settings.TranslateTo}|{text}";
        if (_busy || key == _resultFor) return;
        _busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var (translated, from) = await WebTools.TranslateAsync(text, Settings.TranslateFrom, Settings.TranslateTo);
                (_result, _detected, _resultFor) = (translated, from, key);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            {
                (_result, _resultFor) = ($"Couldn't translate: {ex.Message}", key);
            }
            finally { _busy = false; }
        });
    }

    public override double FrameInterval => _busy ? 0.2 : base.FrameInterval;

    public override void Draw(Ui ui, SKRect t)
    {
        var wide = t.Width > 200;
        if (_picking != null)
        {
            DrawPicker(ui, t, wide);
            return;
        }
        string Label(string code) => wide ? WebTools.LanguageName(code) : code == "auto" ? "Auto" : code.ToUpperInvariant()[..2];
        var from = Settings.TranslateFrom;
        var fromLabel = from == "auto" && _detected.Length > 0 && _resultFor.Length > 0 ? $"{Label("auto")} · {_detected.ToUpperInvariant()}" : Label(from);
        var size = wide ? 11 : 10.5f;
        var a = ui.Chip(t.Left + 8, t.Top + 17, fromLabel, () => _picking = "from", on: true, size: size);
        ui.IconButton(a.Right + 13, t.Top + 17, 9, Icons.Swap, Swap, enabled: from != "auto" || _detected.Length > 0);
        ui.Chip(a.Right + 26, t.Top + 17, Label(Settings.TranslateTo), () => _picking = "to", on: true, size: size);
        if (_result.Length > 0 && wide) ui.IconButton(t.Right - 16, t.Top + 17, 9, Icons.Copy, () => Copy(_result));

        DrawInput(ui, new SKRect(t.Left + 8, t.Top + 34, t.Right - 8, t.Top + 60));
        var current = $"{Settings.TranslateFrom}|{Settings.TranslateTo}|{Input.Text.Trim()}";
        if (_result.Length > 0 && Input.Text.Trim().Length > 0)
        {
            var color = current == _resultFor ? Theme.Text : Theme.Dim;
            ui.Wrapped(_result, t.Left + 12, t.Top + 64, t.Width - 24, 3, 13, color);
            ui.Hit(new SKRect(t.Left, t.Top + 62, t.Right, t.Bottom), () => Copy(_result));
        }
        else if (_busy) ui.Text("Translating…", t.Left + 12, t.Top + 80, t.Width - 24, 11, Theme.Weight.Regular, Theme.Faint);
        else if (!Typing(ui)) ui.Text("Click above and type", t.Left + 12, t.Top + 80, t.Width - 24, 11, Theme.Weight.Regular, Theme.Faint);
    }

    private void Swap()
    {
        var from = Settings.TranslateFrom == "auto" ? _detected : Settings.TranslateFrom;
        if (from.Length == 0) return;
        if (WebTools.Languages.All(l => l.Code != from)) from = "en";
        (Settings.TranslateFrom, Settings.TranslateTo) = (Settings.TranslateTo, from);
        Settings.Save();
        if (_result.Length > 0 && !_result.StartsWith("Couldn't", StringComparison.Ordinal))
        {
            Input.Text = _result;
            Input.Caret = _result.Length;
        }
        _resultFor = "";
        Settled(Input.Text.Trim());
    }

    private void DrawPicker(Ui ui, SKRect t, bool wide)
    {
        var choosingFrom = _picking == "from";
        ui.Text(choosingFrom ? "Translate from" : "Translate to", t.Left + 12, t.Top + 17, t.Width - 40, 11, Theme.Weight.SemiBold, Theme.Accent);
        ui.IconButton(t.Right - 16, t.Top + 13, 8, Icons.Close, () => _picking = null);
        var options = (choosingFrom ? new[] { ("auto", "Detect") } : []).Concat(WebTools.Languages).ToList();
        const int columns = 4;
        var cellW = (t.Width - 12) / columns;
        var current = choosingFrom ? Settings.TranslateFrom : Settings.TranslateTo;
        for (var i = 0; i < options.Count; i++)
        {
            var (code, name) = options[i];
            var x = t.Left + 6 + i % columns * cellW;
            var y = t.Top + 25 + i / columns * 17;
            var cell = new SKRect(x + 1, y, x + cellW - 1, y + 15);
            var selected = code == current;
            if (selected) ui.Fill(cell, 7.5f, Theme.Accent);
            else if (ui.Hovering(cell)) ui.Fill(cell, 7.5f, Theme.Overlay(26));
            var label = wide ? name : code == "auto" ? "Auto" : code.ToUpperInvariant()[..2];
            ui.Text(label, cell.MidX, cell.MidY + 3.4f, cellW - 4, 9.5f, Theme.Weight.Regular, selected ? Theme.OnAccent : Theme.Text, SKTextAlign.Center);
            var chosen = code;
            ui.Hit(cell, () =>
            {
                if (choosingFrom) Settings.TranslateFrom = chosen;
                else Settings.TranslateTo = chosen;
                Settings.Save();
                _picking = null;
                _resultFor = "";
                Settled(Input.Text.Trim());
            });
        }
    }
}

/// <summary>English word meanings.</summary>
internal sealed class DictionaryTile(Hub hub) : TypingTile(hub, "Look up a word")
{
    private IReadOnlyList<WebTools.Meaning> _meanings = [];
    private string _status = "", _for = "";
    private volatile bool _busy;

    public override string Id => "dictionary";
    public override string Name => "Dictionary";

    protected override void Entered(string text) => Settled(text.Trim());

    protected override void Settled(string text)
    {
        if (text.Length == 0 || text.Contains(' ') || text == _for || _busy) return;
        _busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var found = await WebTools.DefineAsync(text);
                (_meanings, _status, _for) = (found, found.Count == 0 ? $"No entry for “{text}”" : "", text);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            {
                (_meanings, _status, _for) = ([], "Dictionary unavailable", text);
            }
            finally { _busy = false; }
        });
    }

    public override void Draw(Ui ui, SKRect t)
    {
        var wide = t.Width > 200;
        var field = wide ? new SKRect(t.Left + 8, t.Top + 8, t.MidX - 4, t.Top + 34) : new SKRect(t.Left + 8, t.Top + 8, t.Right - 8, t.Top + 34);
        DrawInput(ui, field);
        if (_meanings.Count == 0)
        {
            var text = _busy ? "Looking up…" : _status.Length > 0 ? _status : "Meanings of English words";
            ui.Text(text, t.Left + 12, t.Top + 56, t.Width - 24, 10.5f, Theme.Weight.Regular, Theme.Faint);
            return;
        }
        var first = _meanings[0];
        if (wide)
        {
            ui.Text(first.Word, t.MidX + 6, t.Top + 26, t.Width / 2 - 18, 14, Theme.Weight.Bold);
        }
        var y = t.Top + 52;
        ui.Text(wide ? first.Phonetic : $"{first.Word} {first.Phonetic}", t.Left + 12, y - 4, t.Width - 24, 10, Theme.Weight.Regular, Theme.Dim);
        foreach (var m in _meanings.Take(wide ? 2 : 1))
        {
            ui.Text(m.PartOfSpeech, t.Left + 12, y + 12, 80, 10, Theme.Weight.SemiBold, Theme.Accent);
            ui.Wrapped(m.Definition, t.Left + 12 + (wide ? 70 : 0), wide ? y + 1 : y + 16, t.Width - 24 - (wide ? 70 : 0), wide ? 2 : 4, 10.5f);
            y += 34;
        }
    }
}

/// <summary>Exchange rates from one currency into a few others.</summary>
internal sealed class CurrencyTile : Tile
{
    private readonly TextField _amount, _targets;
    private double _value = 1;
    private Dictionary<string, double>? _rates;
    private string _ratesFor = "";
    private DateTime _fetchedAt;
    private volatile bool _busy;
    private string _status = "";

    public CurrencyTile(Hub hub) : base(hub)
    {
        _amount = new TextField("100 or 100 EUR", text =>
        {
            var m = Regex.Match(text.Trim(), @"^(.*?)\s*([A-Za-z]{3})?$");
            if (m.Groups[2].Success && WebTools.Currencies.Contains(m.Groups[2].Value.ToUpperInvariant()))
            {
                Settings.CurrencyBase = m.Groups[2].Value.ToUpperInvariant();
                Settings.Save();
            }
            if (Calculator.TryEvaluate(m.Groups[1].Value, out var v) && v > 0) _value = v;
        });
        _targets = new TextField("INR, EUR, GBP", text =>
        {
            Settings.CurrencyTargets = string.Join(", ", Targets(text));
            Settings.Save();
        });
    }

    public override string Id => "currency";
    public override string Name => "Currency";

    private static IEnumerable<string> Targets(string text) =>
        text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToUpperInvariant()).Distinct().Take(4);

    public override void Draw(Ui ui, SKRect t)
    {
        if (ui.Focused == _amount)
        {
            DrawAsk(ui, t, _amount, "Amount");
            return;
        }
        if (ui.Focused == _targets)
        {
            DrawAsk(ui, t, _targets, "Show these currencies");
            return;
        }
        Fetch();
        var @base = Settings.CurrencyBase;
        var amountText = Calculator.Format(_value);
        ui.Text(amountText, t.Left + 12, t.Top + 24, t.Width - 80, 14, Theme.Weight.Bold);
        ui.Hit(new SKRect(t.Left, t.Top, t.Left + 12 + ui.Measure(amountText, 14, Theme.Weight.Bold) + 4, t.Top + 32), () => Ask(ui, _amount, amountText.Replace(",", "")));
        ui.Chip(t.Left + 18 + ui.Measure(amountText, 14, Theme.Weight.Bold), t.Top + 19, @base, () =>
        {
            var i = Array.IndexOf(WebTools.Currencies, Settings.CurrencyBase);
            Settings.CurrencyBase = WebTools.Currencies[(i + 1) % WebTools.Currencies.Length];
            Settings.Save();
        }, on: true, size: 10.5f);
        ui.IconButton(t.Right - 16, t.Top + 18, 8, Icons.Plus, () => Ask(ui, _targets, Settings.CurrencyTargets));

        if (_rates == null || _ratesFor != @base)
        {
            Note(ui, t, _busy ? "Loading rates…" : _status, "");
            return;
        }
        var y = t.Top + 52;
        foreach (var code in Targets(Settings.CurrencyTargets).Where(c => c != @base).Take(t.Height > 120 ? 3 : 2))
        {
            ui.Text(code, t.Left + 12, y, 40, 11, Theme.Weight.SemiBold, Theme.Dim);
            var value = _rates.TryGetValue(code, out var rate) ? WebTools.Money(_value * rate) : "—";
            ui.Text(value, t.Right - 12, y, t.Width - 60, 13, Theme.Weight.SemiBold, Theme.Text, SKTextAlign.Right);
            var chosen = code;
            // Click a row to convert from that currency instead.
            ui.Hit(new SKRect(t.Left, y - 14, t.Right, y + 8), () =>
            {
                if (_rates.TryGetValue(chosen, out var r)) _value = Math.Round(_value * r, 2);
                Settings.CurrencyTargets = string.Join(", ", Targets(Settings.CurrencyTargets).Select(c => c == chosen ? Settings.CurrencyBase : c));
                Settings.CurrencyBase = chosen;
                Settings.Save();
            });
            y += 24;
        }
        if (t.Width > 200)
            ui.Text("Click a row to convert from it · rates from the ECB", t.Left + 12, t.Bottom - 8, t.Width - 24, 9, Theme.Weight.Regular, Theme.Faint);
    }

    private void Fetch()
    {
        var @base = Settings.CurrencyBase;
        if (_busy || (_ratesFor == @base && DateTime.Now - _fetchedAt < TimeSpan.FromHours(1))) return;
        _busy = true;
        _fetchedAt = DateTime.Now;
        _ = Task.Run(async () =>
        {
            try
            {
                _rates = await WebTools.RatesAsync(@base);
                _ratesFor = @base;
                _status = "";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                _status = "Rates unavailable";
                Log.Write($"Currency: {ex.Message}");
            }
            finally { _busy = false; }
        });
    }
}

/// <summary>Git repositories: branch, ahead/behind and uncommitted changes.</summary>
internal sealed class GitTile : Tile
{
    private sealed record Repo(string Folder, string Name, string Branch, int Ahead, int Behind, int Changed);

    private readonly TextField _folders;
    private IReadOnlyList<Repo> _repos = [];
    private DateTime _at;
    private volatile bool _busy;

    public GitTile(Hub hub) : base(hub) => _folders = new TextField(@"C:\code\project; D:\other", text =>
    {
        Settings.GitFolders = [.. text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(f => f.Trim('"'))];
        Settings.Save();
        _at = default;
    });

    public override string Id => "git";
    public override string Name => "Git";

    public override void Draw(Ui ui, SKRect t)
    {
        if (ui.Focused == _folders)
        {
            DrawAsk(ui, t, _folders, "Repo folders, separated by ;");
            return;
        }
        Icons.Branch(ui.Canvas, t.Left + 19, t.Top + 18, 14, Theme.Accent);
        ui.Text("Git", t.Left + 32, t.Top + 23, t.Width - 60, 12, Theme.Weight.SemiBold, Theme.Accent);
        ui.IconButton(t.Right - 16, t.Top + 18, 8, Icons.Plus, () => Ask(ui, _folders, string.Join("; ", Settings.GitFolders)));
        if (Settings.GitFolders.Count == 0)
        {
            Note(ui, t, "Add a repo", "Click + and paste its folder");
            return;
        }
        Refresh();
        if (_repos.Count == 0)
        {
            Note(ui, t, _busy ? "Reading…" : "Not a git repo", _busy ? "" : "Is git installed?");
            return;
        }
        var y = t.Top + 46;
        var wide = t.Width > 200;
        foreach (var repo in _repos.Take(3))
        {
            ui.Text(repo.Name, t.Left + 12, y, wide ? t.Width / 2 - 12 : t.Width - 24, 11, Theme.Weight.SemiBold);
            var state = new List<string>();
            if (repo.Changed > 0) state.Add($"{repo.Changed} changed");
            if (repo.Ahead > 0) state.Add($"↑{repo.Ahead}");
            if (repo.Behind > 0) state.Add($"↓{repo.Behind}");
            var detail = state.Count == 0 ? "clean" : string.Join(" ", state);
            if (wide)
            {
                ui.Text(repo.Branch, t.MidX, y, t.Width / 4, 10.5f, Theme.Weight.Regular, Theme.Dim);
                ui.Text(detail, t.Right - 12, y, t.Width / 4, 10.5f, Theme.Weight.SemiBold, repo.Changed > 0 ? Warn : Good, SKTextAlign.Right);
            }
            else
            {
                ui.Text($"{repo.Branch} · {detail}", t.Left + 12, y + 13, t.Width - 24, 9.5f, Theme.Weight.Regular, repo.Changed > 0 ? Warn : Theme.Dim);
            }
            var folder = repo.Folder;
            ui.Hit(new SKRect(t.Left, y - 13, t.Right, y + (wide ? 8 : 18)), () => Hub.Open(folder));
            y += wide ? 24 : 32;
        }
    }

    private void Refresh()
    {
        if (_busy || DateTime.Now - _at < TimeSpan.FromSeconds(30)) return;
        _busy = true;
        _at = DateTime.Now;
        var folders = Settings.GitFolders.ToList();
        _ = Task.Run(() =>
        {
            var repos = new List<Repo>();
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                var output = WebTools.Run("git", $"-C \"{folder}\" status --porcelain=v2 --branch");
                if (output == null) continue;
                string branch = "?";
                int ahead = 0, behind = 0, changed = 0;
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = line[14..].Trim();
                    else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
                    {
                        var parts = line[12..].Split(' ');
                        int.TryParse(parts[0].TrimStart('+'), out ahead);
                        int.TryParse(parts.ElementAtOrDefault(1)?.TrimStart('-'), out behind);
                    }
                    else if (!line.StartsWith('#')) changed++;
                }
                repos.Add(new Repo(folder, Path.GetFileName(folder.TrimEnd('\\', '/')), branch, ahead, behind, changed));
            }
            _repos = repos;
            _busy = false;
        });
    }
}

/// <summary>Running Docker containers.</summary>
internal sealed class DockerTile(Hub hub) : Tile(hub)
{
    private IReadOnlyList<(string Name, string Status)>? _containers;
    private DateTime _at;
    private volatile bool _busy;

    public override string Id => "docker";
    public override string Name => "Docker";

    public override void Draw(Ui ui, SKRect t)
    {
        Refresh();
        Heading(ui, t, _containers is { Count: > 0 } list ? $"Docker · {list.Count} running" : "Docker");
        if (_containers == null)
        {
            Note(ui, t, _busy ? "Checking…" : "Docker isn't running", "");
            return;
        }
        if (_containers.Count == 0)
        {
            Note(ui, t, "No containers running", "");
            return;
        }
        var y = t.Top + 44;
        foreach (var (name, status) in _containers.Take(4))
        {
            ui.Circle(t.Left + 15, y - 4, 3, status.StartsWith("Up", StringComparison.Ordinal) ? Good : Warn);
            ui.Text(name, t.Left + 24, y, t.Width > 200 ? t.Width / 2 - 24 : t.Width - 36, 10.5f, Theme.Weight.SemiBold);
            if (t.Width > 200) ui.Text(status, t.Right - 12, y, t.Width / 2 - 12, 10, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Right);
            y += 21;
        }
    }

    private void Refresh()
    {
        if (_busy || DateTime.Now - _at < TimeSpan.FromSeconds(15)) return;
        _busy = true;
        _at = DateTime.Now;
        _ = Task.Run(() =>
        {
            var output = WebTools.Run("docker", "ps --format \"{{.Names}}|{{.Status}}\"", 8000);
            _containers = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('|')).Select(p => (p[0], p.ElementAtOrDefault(1) ?? "")).ToList();
            _busy = false;
        });
    }
}
