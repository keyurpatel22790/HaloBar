using System.Globalization;
using System.Text.RegularExpressions;

namespace HaloBar.Services;

/// <summary>
/// Works out typed sums: + − × ÷ ^ %, brackets, sqrt/sin/cos/tan/log/ln/abs/round, pi and e,
/// and percentages of a number ("18% of 2500", "2500 + 18%"). No variables, no eval.
/// </summary>
internal static class Calculator
{
    public static bool TryEvaluate(string text, out double result)
    {
        result = 0;
        text = Normalise(text);
        if (text.Length == 0) return false;
        try
        {
            var p = new Parser(text);
            result = p.Expression();
            if (!p.AtEnd) return false;
            return !double.IsNaN(result) && !double.IsInfinity(result);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A result for display: commas, at most <paramref name="digits"/> significant digits, no "-0".</summary>
    public static string Format(double value, int digits = 10)
    {
        if (Math.Abs(value) < 1e-12) return "0";
        if (Math.Abs(value) >= 1e15 || Math.Abs(value) < 1e-6) return value.ToString("0.#####e0", CultureInfo.InvariantCulture);
        var rounded = Math.Round(value, Math.Max(0, digits - (int)Math.Floor(Math.Log10(Math.Abs(value))) - 1));
        return rounded.ToString("#,0.##########", CultureInfo.InvariantCulture);
    }

    private static string Normalise(string text)
    {
        text = text.Trim().TrimEnd('=').Replace(",", "").Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace("**", "^");
        // "18% of 2500" → (18/100)*2500
        text = Regex.Replace(text, @"([\d.]+)\s*%\s*of\s*", "($1/100)*", RegexOptions.IgnoreCase);
        // "2500 + 18%" → 2500 + 2500*18/100 (and the same for minus), like a desk calculator.
        text = Regex.Replace(text, @"^([\d.]+)\s*([+-])\s*([\d.]+)\s*%$", "$1$2($1*$3/100)");
        return text.Replace(" ", "").ToLowerInvariant();
    }

    private sealed class Parser(string s)
    {
        private int _i;

        public bool AtEnd => _i >= s.Length;

        private char Peek => _i < s.Length ? s[_i] : '\0';

        // expression := term (('+' | '-') term)*
        public double Expression()
        {
            var value = Term();
            while (Peek is '+' or '-')
            {
                var op = s[_i++];
                var right = Term();
                value = op == '+' ? value + right : value - right;
            }
            return value;
        }

        // term := unary (('*' | '/' | implicit) unary)*
        private double Term()
        {
            var value = Unary();
            while (true)
            {
                if (Peek is '*' or '/')
                {
                    var op = s[_i++];
                    var right = Unary();
                    value = op == '*' ? value * right : value / right;
                }
                else if (Peek == '(' || char.IsLetter(Peek) || Peek == 'π') value *= Power(); // 2(3+4), 2pi
                else return value;
            }
        }

        // unary := ('-' | '+') unary | power, so -2^2 is -4 and 2^-1 is 0.5
        private double Unary()
        {
            if (Peek == '-') { _i++; return -Unary(); }
            if (Peek == '+') { _i++; return Unary(); }
            return Power();
        }

        // power := postfix ('^' unary)?   (right-associative)
        private double Power()
        {
            var value = Postfix(Atom());
            if (Peek != '^') return value;
            _i++;
            return Math.Pow(value, Unary());
        }

        private double Postfix(double value)
        {
            while (true)
            {
                if (Peek == '%') { _i++; value /= 100; }
                else if (Peek == '!') { _i++; value = Factorial(value); }
                else return value;
            }
        }

        private double Atom()
        {
            if (Peek == '(')
            {
                _i++;
                var inner = Expression();
                if (Peek != ')') throw new FormatException();
                _i++;
                return inner;
            }
            if (char.IsDigit(Peek) || Peek == '.')
            {
                var start = _i;
                while (char.IsDigit(Peek) || Peek == '.') _i++;
                if (Peek == 'e' && _i + 1 < s.Length && (char.IsDigit(s[_i + 1]) || s[_i + 1] is '-' or '+'))
                {
                    _i += 2;
                    while (char.IsDigit(Peek)) _i++;
                }
                return double.Parse(s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            if (char.IsLetter(Peek) || Peek == 'π')
            {
                var start = _i;
                while (char.IsLetter(Peek) || Peek == 'π') _i++;
                var name = s[start.._i];
                switch (name)
                {
                    case "pi" or "π": return Math.PI;
                    case "e": return Math.E;
                }
                var arg = Postfix(Atom());
                return name switch
                {
                    "sqrt" or "root" => Math.Sqrt(arg),
                    "sin" => Math.Sin(arg * Math.PI / 180),
                    "cos" => Math.Cos(arg * Math.PI / 180),
                    "tan" => Math.Tan(arg * Math.PI / 180),
                    "log" => Math.Log10(arg),
                    "ln" => Math.Log(arg),
                    "abs" => Math.Abs(arg),
                    "round" => Math.Round(arg),
                    _ => throw new FormatException(),
                };
            }
            throw new FormatException();
        }

        private static double Factorial(double n)
        {
            if (n < 0 || n > 170 || n != Math.Floor(n)) throw new FormatException();
            double r = 1;
            for (var k = 2; k <= n; k++) r *= k;
            return r;
        }
    }
}

/// <summary>
/// Converts "5 km to mi", "100 f in c", "2.5 gb to mb", "70 kg lb". Units of one kind convert
/// through a base unit; temperature has its own formulas.
/// </summary>
internal static class UnitConverter
{
    private sealed record Unit(string Kind, double Factor, string Symbol);

    private static readonly Dictionary<string, Unit> Units = Build();

    private static Dictionary<string, Unit> Build()
    {
        var d = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        void Add(string kind, double factor, string symbol, params string[] names)
        {
            var unit = new Unit(kind, factor, symbol);
            d[symbol] = unit;
            foreach (var n in names) d[n] = unit;
        }
        // Length, in metres.
        Add("length", 1e-3, "mm", "millimetre", "millimeter", "millimetres", "millimeters");
        Add("length", 0.01, "cm", "centimetre", "centimeter", "centimetres", "centimeters");
        Add("length", 1, "m", "metre", "meter", "metres", "meters");
        Add("length", 1000, "km", "kilometre", "kilometer", "kilometres", "kilometers");
        Add("length", 0.0254, "in", "inch", "inches", "\"");
        Add("length", 0.3048, "ft", "foot", "feet", "'");
        Add("length", 0.9144, "yd", "yard", "yards");
        Add("length", 1609.344, "mi", "mile", "miles");
        Add("length", 1852, "nmi", "nautical");
        // Mass, in kilograms.
        Add("mass", 1e-6, "mg", "milligram", "milligrams");
        Add("mass", 1e-3, "g", "gram", "grams");
        Add("mass", 1, "kg", "kilo", "kilos", "kilogram", "kilograms");
        Add("mass", 1000, "t", "tonne", "tonnes", "ton", "tons");
        Add("mass", 0.45359237, "lb", "lbs", "pound", "pounds");
        Add("mass", 0.028349523125, "oz", "ounce", "ounces");
        Add("mass", 6.35029318, "st", "stone");
        Add("mass", 0.01166380, "tola", "tolas");
        // Volume, in litres.
        Add("volume", 1e-3, "ml", "millilitre", "milliliter", "millilitres", "milliliters");
        Add("volume", 1, "l", "litre", "liter", "litres", "liters");
        Add("volume", 3.785411784, "gal", "gallon", "gallons");
        Add("volume", 0.2365882365, "cup", "cups");
        Add("volume", 0.0295735296, "floz");
        Add("volume", 0.014786765, "tbsp", "tablespoon", "tablespoons");
        Add("volume", 0.004928922, "tsp", "teaspoon", "teaspoons");
        // Area, in square metres.
        Add("area", 1, "m2", "sqm", "m²");
        Add("area", 0.09290304, "ft2", "sqft", "ft²");
        Add("area", 1e6, "km2", "sqkm", "km²");
        Add("area", 4046.8564224, "acre", "acres");
        Add("area", 10000, "ha", "hectare", "hectares");
        Add("area", 2589988.110336, "mi2", "sqmi", "mi²");
        // Speed, in metres per second.
        Add("speed", 1, "m/s", "mps");
        Add("speed", 1 / 3.6, "km/h", "kmh", "kph");
        Add("speed", 0.44704, "mph");
        Add("speed", 0.514444, "kn", "knot", "knots");
        // Data, in bytes (binary multiples, like Windows shows).
        Add("data", 1, "B", "byte", "bytes");
        Add("data", 1024, "KB", "kib", "kilobyte", "kilobytes");
        Add("data", 1024d * 1024, "MB", "mib", "megabyte", "megabytes");
        Add("data", 1024d * 1024 * 1024, "GB", "gib", "gigabyte", "gigabytes");
        Add("data", 1024d * 1024 * 1024 * 1024, "TB", "tib", "terabyte", "terabytes");
        // Time, in seconds.
        Add("time", 1, "s", "sec", "secs", "second", "seconds");
        Add("time", 60, "min", "mins", "minute", "minutes");
        Add("time", 3600, "h", "hr", "hrs", "hour", "hours");
        Add("time", 86400, "d", "day", "days");
        Add("time", 604800, "wk", "week", "weeks");
        Add("time", 31557600, "yr", "year", "years");
        // Temperature: the factor is unused.
        Add("temp", 0, "°C", "c", "celsius", "°c");
        Add("temp", 0, "°F", "f", "fahrenheit", "°f");
        Add("temp", 0, "K", "k", "kelvin");
        return d;
    }

    private static readonly Regex Query = new(@"^\s*(-?[\d.,]+)\s*([^\d\s][^\s]*?)\s+(?:to|in|into|as|=|->)?\s*([^\d\s][^\s]*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Returns "8.05 km" for "5 mi to km", or null when the text isn't a conversion.</summary>
    public static string? Convert(string text)
    {
        var m = Query.Match(text);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return null;
        if (!Units.TryGetValue(m.Groups[2].Value, out var from) || !Units.TryGetValue(m.Groups[3].Value, out var to)) return null;
        if (from.Kind != to.Kind) return null;
        var result = from.Kind == "temp" ? Temperature(amount, from.Symbol, to.Symbol) : amount * from.Factor / to.Factor;
        return $"{Calculator.Format(result, 6)} {to.Symbol}";
    }

    private static double Temperature(double v, string from, string to)
    {
        var celsius = from switch { "°F" => (v - 32) * 5 / 9, "K" => v - 273.15, _ => v };
        return to switch { "°F" => celsius * 9 / 5 + 32, "K" => celsius + 273.15, _ => celsius };
    }
}
