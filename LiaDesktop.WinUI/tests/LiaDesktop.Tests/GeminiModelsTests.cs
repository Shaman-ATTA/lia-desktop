using LiaDesktop.Services;
using Xunit;

namespace LiaDesktop.Tests;

public class GeminiModelsTests
{
    [Fact]
    public void ParseSuggestedModel_From404()
    {
        var msg =
            "This model models/gemini-2.0-flash is no longer available. Please update your code to use models/gemini-2.5-flash";
        Assert.Equal("gemini-2.5-flash", GeminiModels.ParseSuggestedModel(msg));
    }

    [Fact]
    public void PickBestFlash_PrefersFlashLiteLatest()
    {
        var models = new[]
        {
            new GeminiModelInfo("gemini-3.8-flash", "", true),
            new GeminiModelInfo("gemini-3.7-flash", "", true),
            new GeminiModelInfo("gemini-flash-lite-latest", "", true),
            new GeminiModelInfo("gemini-3.5-flash-lite", "", true),
            new GeminiModelInfo("gemini-2.5-flash", "", true),
        };
        Assert.Equal("gemini-flash-lite-latest", GeminiModels.PickBestFlash(models));
    }

    [Fact]
    public void BuildFallbackChain_UserThenPreferred()
    {
        var models = new[]
        {
            new GeminiModelInfo("gemini-3.8-flash", "", true),
            new GeminiModelInfo("gemini-flash-lite-latest", "", true),
            new GeminiModelInfo("gemini-3.1-flash-lite", "", true),
            new GeminiModelInfo("gemini-3.7-flash", "", true),
            new GeminiModelInfo("gemini-2.5-flash", "", true), // retired — skipped
        };
        var chain = GeminiModels.BuildFallbackChain(models, "gemini-3.8-flash").ToList();
        Assert.Equal("gemini-3.8-flash", chain[0]); // user choice first
        Assert.Contains("gemini-flash-lite-latest", chain);
        Assert.DoesNotContain(chain, id => id.StartsWith("gemini-2.5"));
        // preferred order after user
        var lite = chain.IndexOf("gemini-flash-lite-latest");
        var p31 = chain.IndexOf("gemini-3.1-flash-lite");
        Assert.True(lite >= 0 && p31 >= 0 && lite < p31);
    }

    [Fact]
    public void NextFlashFallbacks_SkipsCurrent()
    {
        var models = new[]
        {
            new GeminiModelInfo("gemini-flash-lite-latest", "", true),
            new GeminiModelInfo("gemini-3.8-flash", "", true),
            new GeminiModelInfo("gemini-3.7-flash", "", true),
        };
        var next = GeminiModels.NextFlashFallbacks(models, "gemini-flash-lite-latest");
        Assert.DoesNotContain("gemini-flash-lite-latest", next);
        Assert.Contains("gemini-3.8-flash", next);
    }

    [Fact]
    public async Task ListModels_FiltersGenerateContent()
    {
        var json =
            """
            {"models":[
              {"name":"models/gemini-flash-lite-latest","displayName":"Flash Lite","supportedGenerationMethods":["generateContent"]},
              {"name":"models/embedding-001","supportedGenerationMethods":["embedContent"]},
              {"name":"models/gemini-3.8-flash","supportedGenerationMethods":["generateContent"]}
            ]}
            """;
        var handler = new StubHandler(json, System.Net.HttpStatusCode.OK);
        var http = new HttpClient(handler);
        var list = await GeminiModels.ListAsync("key", http);
        Assert.Equal(new[] { "gemini-flash-lite-latest", "gemini-3.8-flash" }, list.Select(m => m.Id));
    }

    [Fact]
    public async Task Resolve_MigratesRetired25()
    {
        var json =
            """
            {"models":[
              {"name":"models/gemini-flash-lite-latest","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-3.8-flash","supportedGenerationMethods":["generateContent"]}
            ]}
            """;
        var http = new HttpClient(new StubHandler(json, System.Net.HttpStatusCode.OK));
        var r = await GeminiModels.ResolveAsync("k", "gemini-2.5-flash", http);
        Assert.Equal("gemini-flash-lite-latest", r.Model);
        Assert.True(r.Migrated);
    }

    [Fact]
    public async Task Resolve_MigratesPhantom()
    {
        var json =
            """
            {"models":[
              {"name":"models/gemini-flash-lite-latest","supportedGenerationMethods":["generateContent"]}
            ]}
            """;
        var http = new HttpClient(new StubHandler(json, System.Net.HttpStatusCode.OK));
        var r = await GeminiModels.ResolveAsync("k", "gemini-not-real", http);
        Assert.Equal("gemini-flash-lite-latest", r.Model);
        Assert.True(r.Migrated);
    }

    [Fact]
    public void Classify_AuthModelNetwork()
    {
        var auth = GeminiModels.Classify(new HttpRequestException("API key not valid", null, System.Net.HttpStatusCode.BadRequest));
        Assert.Equal(GeminiErrorKind.Auth, auth.Kind);
        var model = GeminiModels.Classify(
            new HttpRequestException(
                "This model models/gemini-2.0-flash is no longer available. use models/gemini-flash-lite-latest",
                null, System.Net.HttpStatusCode.NotFound));
        Assert.Equal(GeminiErrorKind.Model, model.Kind);
        Assert.Equal("gemini-flash-lite-latest", model.SuggestedModel);
        var busy = GeminiModels.Classify(
            new HttpRequestException("model is currently experiencing high demand", null, System.Net.HttpStatusCode.ServiceUnavailable));
        Assert.Contains("перегружена", busy.UserMessage);
    }

    [Fact]
    public void Classify_ShowsHttpStatus()
    {
        var err = new GeminiHttpException("Resource exhausted", 503, 2, """{"error":{"message":"Resource exhausted"}}""");
        var c = GeminiModels.Classify(err);
        Assert.Contains("HTTP 503", c.UserMessage);
        Assert.Contains("перегружена", c.UserMessage);
    }

    [Fact]
    public void Visemes_A_CappedAtO()
    {
        var a = Visemes.Morph(VisemeShape.A, 1);
        var o = Visemes.Morph(VisemeShape.O, 1);
        Assert.True(a.Jaw <= o.Jaw + 0.001f);
    }

    sealed class StubHandler : HttpMessageHandler
    {
        readonly string _json;
        readonly System.Net.HttpStatusCode _code;
        public StubHandler(string json, System.Net.HttpStatusCode code) { _json = json; _code = code; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
