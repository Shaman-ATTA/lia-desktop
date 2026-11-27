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

    public static bool ContainsWake(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = NormalizeAsrWake(text);
        if (text is "лиа" or "лия") return true;
        return WakeRe.IsMatch(text);
    }

    public static string StripWake(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = NormalizeAsrWake(text);
        return StripLeadingWake.Replace(s, "").Trim();
    }
}
