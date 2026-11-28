using LiaDesktop.Services;
using Xunit;

namespace LiaDesktop.Tests;

public class WakeTextTests
{
    [Theory]
    [InlineData("могу ли я это сделать")]
    [InlineData("правильно ли я понял")]
    [InlineData("не знаю, смогу ли я прийти")]
    [InlineData("должен ли я звонить")]
    [InlineData("знаю ли я ответ")]
    [InlineData("смогу ли я")]
    public void NormalizeAsrWake_mid_sentence_li_ya_must_not_become_lia(string text)
    {
        var n = WakeText.NormalizeAsrWake(text);
        Assert.DoesNotContain("лиа", n);
        Assert.False(WakeText.ContainsWake(text));
        Assert.False(WakeText.ContainsWake(n));
    }

    [Theory]
    [InlineData("ли я", "лиа")]
    [InlineData("ли я,", "лиа,")]
    [InlineData("  ли я  ", "лиа")]
    [InlineData("ли я, открой блокнот", "лиа, открой блокнот")]
    [InlineData("лиа, привет", "лиа, привет")]
    public void NormalizeAsrWake_leading_li_ya_collapses(string input, string expect)
    {
        Assert.Equal(expect, WakeText.NormalizeAsrWake(input));
    }

    [Fact]
    public void Leading_li_ya_inline_command_wakes_and_strips()
    {
        const string raw = "ли я, открой блокнот";
        Assert.True(WakeText.ContainsWake(raw));
        var cmd = WakeText.StripWake(raw);
        Assert.Contains("открой", cmd);
        Assert.Contains("блокнот", cmd);
        Assert.DoesNotContain("лиа", cmd);
        Assert.DoesNotContain("ли я", cmd);
    }

    [Theory]
    [InlineData("лиа")]
    [InlineData("лия")]
    [InlineData("эй, лиа")]
    [InlineData("лиа, привет")]
    public void ContainsWake_positives(string text) =>
        Assert.True(WakeText.ContainsWake(text));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("лет", false)]
    [InlineData("ли", false)]
    [InlineData("привет", false)]
    [InlineData("открой блокнот", true)]
    [InlineData("какая сейчас погода", true)]
    public void IsLikelyInlineCommand_cases(string? cmd, bool expect) =>
        Assert.Equal(expect, WakeText.IsLikelyInlineCommand(cmd));

    [Theory]
    [InlineData("лет", false)]
    [InlineData("ли", false)]
    [InlineData("ли привет", false)]
    [InlineData("эй ли а", false)]
    [InlineData("могу ли я это сделать", true)]
    [InlineData("правильно ли я понял", true)]
    [InlineData("не знаю, смогу ли я прийти", true)]
    [InlineData("должен ли я звонить", true)]
    public void IsSubstantialNonWake_cases(string text, bool expect) =>
        Assert.Equal(expect, WakeText.IsSubstantialNonWake(text));
}
