using System.Text.RegularExpressions;

namespace HaloBar.Services;

/// <summary>
/// Finds a one-time code (OTP, verification or login code) in a notification: a 4–8 digit number,
/// or one like "G-482913", that sits close to a word saying it's a code. Amounts, phone numbers,
/// dates, times and order numbers are left alone.
/// </summary>
internal static partial class OneTimeCode
{
    // Words that mark a code, English and Hindi.
    [GeneratedRegex(@"\b(otp|one[\s-]?time|verification|verify|passcode|pass\s?code|security\s?code|login\s?code|sign[\s-]?in\s?code|auth(entication)?\s?code|confirmation\s?code|2fa|mfa|pin|code)\b|कोड|ओटीपी|सत्यापन",
        RegexOptions.IgnoreCase)]
    private static partial Regex Keyword();

    // A code: 4–8 digits on their own (not part of a longer, decimal or space-split number, nor glued to letters like a
    // masked account "XX1234"), optionally after a short prefix ("G-").
    [GeneratedRegex(@"(?<![\d.,:/\-₹$€£\p{L}])(?:[A-Z]{1,3}-)?(\d{4,8})(?![\d,:/%]|\.\d|\s\d|\s?(?:rs|inr|usd|%))", RegexOptions.IgnoreCase)]
    private static partial Regex Candidate();

    // Money just before the number ("Rs. 4500", "₹ 1299", "INR 2500").
    [GeneratedRegex(@"(rs\.?|inr|usd|eur|₹|\$|€|£)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MoneyBefore();

    // Order, account and reference numbers are not codes.
    [GeneratedRegex(@"(order|a/c|acct|account|ref|reference|invoice|ticket|txn|transaction|id|no\.?|number|#)\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelBefore();

    /// <summary>The code in <paramref name="title"/> or <paramref name="body"/>, or null.</summary>
    public static string? Find(string title, string body)
    {
        var text = $"{title} \n {body}";
        var keywords = Keyword().Matches(text).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        if (keywords.Count == 0) return null;
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (Match m in Candidate().Matches(text))
        {
            var digits = m.Groups[1].Value;
            var before = text[Math.Max(0, m.Index - 12)..m.Index];
            if (MoneyBefore().IsMatch(before) || LabelBefore().IsMatch(before)) continue;
            // A year on its own (2026) is a date, unless a code word is right beside it.
            var distance = keywords.Min(k => m.Index >= k.End ? m.Index - k.End : Math.Max(0, k.Index - (m.Index + m.Length)));
            if (digits.Length == 4 && digits[0] is '1' or '2' && digits[1] is '9' or '0' && distance > 3) continue;
            if (distance > 40 || distance >= bestDistance) continue;
            (best, bestDistance) = (digits, distance);
        }
        return best;
    }
}
