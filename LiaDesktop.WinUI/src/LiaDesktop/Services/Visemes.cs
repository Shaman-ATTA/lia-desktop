namespace LiaDesktop.Services;

public enum VisemeShape { Rest, A, O, E, Closed }

public static class Visemes
{
    public static VisemeShape FromChar(char c)
    {
        c = char.ToLowerInvariant(c);
        return c switch
        {
            'а' or 'a' or 'я' => VisemeShape.A,
            'о' or 'o' or 'у' or 'u' or 'ю' or 'ё' => VisemeShape.O,
            'е' or 'e' or 'и' or 'i' or 'ы' or 'э' => VisemeShape.E,
            'м' or 'm' or 'п' or 'p' or 'б' or 'b' => VisemeShape.Closed,
            _ => VisemeShape.Rest,
        };
    }

    /// <summary>A capped at O openness (Android parity).</summary>
    public static (float Jaw, float Funnel, float Wide) Morph(VisemeShape shape, float amp = 1f)
    {
        amp = Math.Clamp(amp, 0, 1);
        var oOpen = 0.58f * Math.Max(amp, 0.35f);
        return shape switch
        {
            VisemeShape.A => (Math.Min(oOpen, 0.58f), 0.05f, 0.08f),
            VisemeShape.O => (Math.Min(oOpen, 0.58f), 0.75f + 0.25f * amp, 0.05f),
            VisemeShape.E => (Math.Min(0.48f * Math.Max(amp, 0.35f), 0.55f), 0f, 0.7f + 0.3f * amp),
            VisemeShape.Closed => (0.02f, 0f, 0f),
            _ => (0.04f, 0f, 0f),
        };
    }

    public static (float Jaw, float Funnel, float Wide) FromAmplitude(float amp) =>
        Morph(VisemeShape.A, Math.Clamp(amp, 0.2f, 1f));
}
