using LiaDesktop.Services;
using Xunit;

namespace LiaDesktop.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void TryGetSha256_parses_gnu_format()
    {
        var sums = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899  LiaDesktop-0.7.6-setup.exe\n";
        Assert.True(UpdateLogic.TryGetSha256(sums, "LiaDesktop-0.7.6-setup.exe", out var h));
        Assert.Equal("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899", h);
    }

    [Fact]
    public void TryGetSha256_rejects_missing()
    {
        var sums = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899  other.exe\n";
        Assert.False(UpdateLogic.TryGetSha256(sums, "LiaDesktop-0.7.6-setup.exe", out _));
    }

    [Fact]
    public void IsUpdateCommand_ru_phrases()
    {
        Assert.True(UpdateLogic.IsUpdateCommand("обнови себя"));
        Assert.True(UpdateLogic.IsUpdateCommand("Лиа, проверь обновления"));
        Assert.True(UpdateLogic.IsUpdateCommand("проверь обновление"));
        Assert.True(UpdateLogic.IsInstallIntent("обновись"));
        Assert.False(UpdateLogic.IsUpdateCommand("открой блокнот"));
    }

    [Fact]
    public void AppVersion_tag_parse_and_newer()
    {
        Assert.True(AppVersion.TryParseTag("v0.7.7", out var v));
        Assert.Equal(new Version(0, 7, 7), v);
        Assert.True(AppVersion.IsNewer(new Version(0, 7, 7), new Version(0, 7, 6)));
        Assert.False(AppVersion.IsNewer(new Version(0, 7, 6), new Version(0, 7, 6)));
    }

    [Fact]
    public void BuildSilentUpgradeArgs_includes_launch_and_dir()
    {
        var a = UpdateLogic.BuildSilentUpgradeArgs(@"C:\Users\x\AppData\Local\LiaDesktop");
        Assert.Contains("/VERYSILENT", a);
        Assert.Contains("/FORCECLOSEAPPLICATIONS", a);
        Assert.Contains("/LAUNCHAFTER=1", a);
        Assert.Contains("/DIR=", a);
    }
}
