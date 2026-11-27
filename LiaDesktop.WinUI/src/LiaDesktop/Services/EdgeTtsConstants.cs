namespace LiaDesktop.Services;

/// <summary>
/// Constants shared with Microsoft Edge online TTS (edge-tts 7.3.1).
/// </summary>
public static class EdgeTtsConstants
{
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    public const string ChromiumFullVersion = "143.0.3650.75";
    public const string ChromiumMajorVersion = "143";
    public const string SecMsGecVersion = "2-143.0.3650.75";

    public const string WssUrlBase =
        "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1"
        + "?TrustedClientToken=" + TrustedClientToken;

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";

    public const string Origin = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";

    /// <summary>WSS request headers without Cookie (muid is added per connection).</summary>
    public static IReadOnlyDictionary<string, string> WssHeadersWithoutCookie { get; } =
        new Dictionary<string, string>
        {
            ["Pragma"] = "no-cache",
            ["Cache-Control"] = "no-cache",
            ["Origin"] = Origin,
            ["Sec-WebSocket-Version"] = "13",
            ["User-Agent"] = UserAgent,
            ["Accept-Encoding"] = "gzip, deflate, br, zstd",
            ["Accept-Language"] = "en-US,en;q=0.9",
        };
}
