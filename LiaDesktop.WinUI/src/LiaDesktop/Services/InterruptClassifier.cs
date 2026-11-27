using System.Text.RegularExpressions;

namespace LiaDesktop.Services;

public enum InterruptKind
{
    /// <summary>Continue the interrupted sentence / remainder.</summary>
    Backchannel,
    /// <summary>Hard stop — no reply.</summary>
    Stop,
    /// <summary>Cancel remainder; treat as a new user request.</summary>
    NewRequest,
}

/// <summary>Classifies barge-in STT text (Russian backchannels / stop / new request).</summary>
public static class InterruptClassifier
{
    static readonly Regex StopRe = new(
        @"^(?:стоп|хватит|замолчи|тихо|прекрати|остановись)(?:\s|[!.…]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex BackchannelRe = new(
        @"^(?:угу|ага|ага+|угу+|да|даа+|ок|okay|ok|продолжай|дальше|так|ну|хорошо|ясно|понял[ао]?|слушаю)(?:\s|[!.…]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim().ToLowerInvariant();
        t = Regex.Replace(t, @"[«»""„]", "");
        t = Regex.Replace(t, @"\s+", " ");
        return t;
    }

    public static InterruptKind Classify(string? text)
    {
        var t = Normalize(text);
        if (t.Length == 0) return InterruptKind.Backchannel; // empty → treat as soft continue
        if (StopRe.IsMatch(t)) return InterruptKind.Stop;
        if (BackchannelRe.IsMatch(t)) return InterruptKind.Backchannel;
        // Short affirmations embedded in noise: first token
        var first = t.Split(' ', 2)[0];
        if (StopRe.IsMatch(first)) return InterruptKind.Stop;
        if (BackchannelRe.IsMatch(first) && t.Split(' ').Length <= 2)
            return InterruptKind.Backchannel;
        return InterruptKind.NewRequest;
    }
}
