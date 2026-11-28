using System.Text.RegularExpressions;

namespace LiaDesktop.Services;

/// <summary>
/// Pure wake-word text helpers (no Vosk). Unit-tested: «ли я» collapses only at utterance start.
/// </summary>
public static class WakeText
{
    // Whole-word лиа/лия only — never mid-sentence «ли я» (могу ли я / правильно ли я …).
    static readonly Regex WakeRe = new(
        @"(^|[\s,.:;!?])(лиа|лия)($|[\s,.:;!?])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ASR-split wake: ONLY at start, optionally followed by pause/comma or end.
    static readonly Regex LeadingAsrSplitWake = new(
        @"^\s*ли\s+я\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex StripLeadingWake = new(
        @"^\s*(лиа|лия)\s*[,.:;!]?\s*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Collapse vosk freeform «ли я» → «лиа» only when it opens the utterance.
    /// Mid-sentence «могу ли я» / «правильно ли я» must stay untouched.
    /// </summary>
    public static string NormalizeAsrWake(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim().ToLowerInvariant();
        return LeadingAsrSplitWake.Replace(s, "лиа");
    }

    // «эй ли я» / «ей ли я» — interjection + ASR-split wake (not «могу ли я»)
    static readonly Regex InterjectionAsrWake = new(
        @"^(эй|ей|ну|а)\s+ли\s+я\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool ContainsWake(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = NormalizeAsrWake(text);
        if (text is "лиа" or "лия") return true;
        if (InterjectionAsrWake.IsMatch(text)) return true;
        return WakeRe.IsMatch(text);
    }

    public static string StripWake(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = NormalizeAsrWake(text);
        if (InterjectionAsrWake.IsMatch(s)) return "";
        return StripLeadingWake.Replace(s, "").Trim();
    }

    /// <summary>
    /// True when the post-wake remnant looks like a real inline command
    /// («открой блокнот»), not ASR crumbs («лет», «ли»).
    /// </summary>
    public static bool IsLikelyInlineCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var c = command.Trim().ToLowerInvariant();
        if (c.Length < 5) return false;
        // Single short token without a verb-like length → crumb
        if (!c.Contains(' ') && c.Length < 8) return false;
        return true;
    }

    /// <summary>
    /// Freeform ASR that is clearly a non-wake utterance (e.g. «могу ли я…»).
    /// Sparse crumbs («лет», «ли») and «эй ли я» are NOT substantial — grammar/wake may apply.
    /// </summary>
    public static bool IsSubstantialNonWake(string? freeform)
    {
        if (string.IsNullOrWhiteSpace(freeform)) return false;
        var t = NormalizeAsrWake(freeform);
        if (ContainsWake(t)) return false;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Mid «ли я»: question particle only when trailing content or a long word before «ли»
        // («могу ли я…», «должен ли я») — NOT «эй ли я».
        if (Regex.IsMatch(t, @"\bли\s+я\b", RegexOptions.CultureInvariant))
        {
            if (Regex.IsMatch(t, @"\bли\s+я\b.+\S", RegexOptions.CultureInvariant)) return true;
            if (words.Length >= 3 && words[0].Length >= 4) return true;
            return false;
        }
        if (words.Length >= 4) return true;
        if (t.Length >= 20) return true;
        return false;
    }
}
