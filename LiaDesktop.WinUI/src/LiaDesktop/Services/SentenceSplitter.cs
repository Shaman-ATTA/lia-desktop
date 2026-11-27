using System.Text;
using System.Text.RegularExpressions;

namespace LiaDesktop.Services;

/// <summary>
/// Split spoken replies into TTS-sized sentences (RU punctuation + soft comma cuts).
/// </summary>
public static class SentenceSplitter
{
    public const int SoftMaxChars = 200;

    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var parts = new List<string>();
        var sb = new StringBuilder();

        void Flush()
        {
            var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            sb.Clear();
            if (s.Length == 0) return;
            foreach (var chunk in SoftCut(s))
                parts.Add(chunk);
        }

        var i = 0;
        while (i < raw.Length)
        {
            var ch = raw[i];
            if (ch == '\n')
            {
                Flush();
                i++;
                continue;
            }

            sb.Append(ch);
            if (ch is '.' or '!' or '?' or '…')
            {
                // Consume repeated terminal punct
                while (i + 1 < raw.Length && raw[i + 1] is '.' or '!' or '?' or '…')
                {
                    i++;
                    sb.Append(raw[i]);
                }
                Flush();
            }
            i++;
        }
        Flush();
        return parts;
    }

    static IEnumerable<string> SoftCut(string sentence)
    {
        if (sentence.Length <= SoftMaxChars)
        {
            yield return sentence;
            yield break;
        }

        var rest = sentence;
        while (rest.Length > SoftMaxChars)
        {
            var window = rest.AsSpan(0, SoftMaxChars);
            var cut = -1;
            for (var i = window.Length - 1; i >= SoftMaxChars / 3; i--)
            {
                if (window[i] is ',' or ';' or ':' or '—' or '–' or ' ')
                {
                    cut = i + 1;
                    break;
                }
            }
            if (cut <= 0) cut = SoftMaxChars;
            var piece = rest[..cut].Trim();
            if (piece.Length > 0) yield return piece;
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0) yield return rest;
    }
}
