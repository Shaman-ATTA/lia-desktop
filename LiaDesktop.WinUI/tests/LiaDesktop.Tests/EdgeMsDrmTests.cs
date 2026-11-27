using LiaDesktop.Services;
using Xunit;

namespace LiaDesktop.Tests;

public class EdgeMsDrmTests
{
    // Goldens from edge-tts 7.3.1 generate_sec_ms_gec_v2
    [Theory]
    [InlineData(1732650000.0, "E2293CD8BF95C059-2437F76E9A01D5DFFF8267765BBA0A3ACEFE41E519345DACEB3C609B925026E8")]
    [InlineData(1700000000.0, "2CD23A2CA0B2595A-F3C846C370E05F9CC83ACC5F995C5F5D87A4D0F14CE8936A0F902E64099E38A2")]
    [InlineData(1600000000.0, "1F5E89103EC98A08-E69D008C1241797258F0EE06F56044C11FAEC4B60F95C6AB0DB3C5E79BE2F010")]
    public void SecMsGec_v2_matches_edge_tts_golden(double unix, string expected)
    {
        Assert.Equal(expected, EdgeMsDrm.GenerateSecMsGec(unix));
        Assert.Equal("2-143.0.3650.75", EdgeMsDrm.SecMsGecVersion);
    }

    [Fact]
    public void BuildWssUrl_includes_gec_params()
    {
        var url = EdgeMsDrm.BuildWssUrl("abc123");
        Assert.Contains("ConnectionId=abc123", url);
        Assert.Contains("Sec-MS-GEC=", url);
        Assert.Contains("Sec-MS-GEC-Version=2-143.0.3650.75", url);
    }
}
