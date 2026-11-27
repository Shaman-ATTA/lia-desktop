using LiaDesktop.Services;
using Xunit;

namespace LiaDesktop.Tests;

public class InterruptClassifierTests
{
    [Theory]
    [InlineData("угу", InterruptKind.Backchannel)]
    [InlineData("Ага!", InterruptKind.Backchannel)]
    [InlineData("да", InterruptKind.Backchannel)]
    [InlineData("продолжай", InterruptKind.Backchannel)]
    [InlineData("дальше", InterruptKind.Backchannel)]
    [InlineData("так", InterruptKind.Backchannel)]
    [InlineData("стоп", InterruptKind.Stop)]
    [InlineData("Хватит.", InterruptKind.Stop)]
    [InlineData("замолчи", InterruptKind.Stop)]
    [InlineData("тихо", InterruptKind.Stop)]
    [InlineData("открой браузер", InterruptKind.NewRequest)]
    [InlineData("какая погода завтра", InterruptKind.NewRequest)]
    public void Classify_cases(string text, InterruptKind expect) =>
        Assert.Equal(expect, InterruptClassifier.Classify(text));
}

public class EchoCancelVadTests
{
    static float[] Tone(int samples, float freq, float amp)
    {
        var a = new float[samples];
        for (var i = 0; i < samples; i++)
            a[i] = amp * MathF.Sin(2 * MathF.PI * freq * i / EchoCancelVad.SampleRate);
        return a;
    }

    [Fact]
    public void Loopback_tts_alone_does_not_trigger()
    {
        var vad = new EchoCancelVad();
        var tts = Tone(EchoCancelVad.SampleRate * 2, 220, 0.4f); // 2s
        const int frame = 320;
        for (var off = 0; off + frame <= tts.Length; off += frame)
        {
            var slice = tts.AsSpan(off, frame);
            vad.PushPlayback(slice);
            var mic = new float[frame];
            for (var i = 0; i < frame; i++) mic[i] = slice[i] * 0.75f; // speaker→mic loopback
            Assert.False(vad.PushMic(mic), $"false barge at {off}");
        }
        Assert.False(vad.Triggered);
    }

    [Fact]
    public void User_speech_over_tts_triggers_after_300ms()
    {
        var vad = new EchoCancelVad();
        var tts = Tone(EchoCancelVad.SampleRate * 2, 220, 0.35f);
        var user = Tone(EchoCancelVad.SampleRate, 900, 0.5f); // 1s of user speech
        const int frame = 320;
        var userStart = EchoCancelVad.SampleRate / 5; // 200ms
        var triggeredAt = -1;
        for (var off = 0; off + frame <= tts.Length; off += frame)
        {
            var slice = tts.AsSpan(off, frame).ToArray();
            vad.PushPlayback(slice);
            var mic = new float[frame];
            for (var i = 0; i < frame; i++)
            {
                var idx = off + i;
                float u = 0;
                if (idx >= userStart)
                {
                    var ui = idx - userStart;
                    if (ui < user.Length) u = user[ui];
                }
                mic[i] = slice[i] * 0.45f + u;
            }
            if (vad.PushMic(mic) && triggeredAt < 0)
                triggeredAt = off;
        }
        Assert.True(vad.Triggered);
        Assert.True(triggeredAt >= userStart);
        // ≥300ms user speech before trip
        Assert.True(triggeredAt >= userStart + (int)(0.25 * EchoCancelVad.SampleRate));
    }

    [Fact]
    public void SplitReply_snaps_near_sentence()
    {
        var full = "Привет, сэр. Как дела сегодня? Всё хорошо.";
        var (spoken, rem) = EchoCancelVad.SplitReply(full, 0.35);
        Assert.Contains("Привет", spoken);
        Assert.False(string.IsNullOrWhiteSpace(rem));
    }
}
