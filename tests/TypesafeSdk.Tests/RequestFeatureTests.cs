using System.Text;
using System.Text.Json.Nodes;
using TypesafeSdk.Models;

namespace TypesafeSdk.Tests;

public class RequestFeatureTests
{
    private const string BoxResult = """
        {
          "model": "jev-preview",
          "usage": { "input_tokens": null, "output_tokens": null },
          "answers": {
            "dog": { "type": "box", "box": { "top": 0.1, "bottom": 0.6, "left": 0.2, "right": 0.9 }, "confidence": 0.75 },
            "cat": { "type": "box", "box": { "top": 0, "bottom": 1, "left": 0, "right": 1 }, "confidence": null },
            "bird": { "type": "box", "box": { "top": 0, "bottom": 1, "left": 0, "right": 1 } }
          }
        }
        """;

    [Fact]
    public async Task BoxQuestions_AreSentAndAnswersParsed()
    {
        var handler = new StubHandler(_ => Reply.Ok(BoxResult));
        using var client = TestClient.Create(handler);

        var result = await client.SystemOneAsync(
            "a photo",
            new Dictionary<string, Question>
            {
                ["dog"] = new BoxQuestion { Instructions = "The dog" },
                ["cat"] = new BoxQuestion(),
            },
            model: "jev-preview");

        JsonAssert.Equal(
            """{ "dog": { "type": "box", "instructions": "The dog" }, "cat": { "type": "box" } }""",
            handler.Requests[0].Json["questions"]);
        Assert.Equal(new BoxAnswer(new BoxCoordinates(0.1, 0.6, 0.2, 0.9), 0.75), result.Boxes["dog"]);
        Assert.Null(result.Boxes["cat"].Confidence);
        Assert.Null(result.Boxes["bird"].Confidence);
        Assert.Equal(new Usage(null, null), result.Usage);
        Assert.Empty(result.Nouls);
    }

    [Theory]
    [InlineData("""{"type":"box","confidence":1}""", "answers.b.box")]
    [InlineData("""{"type":"box","box":{"top":0,"bottom":1,"left":0}}""", "answers.b.box.right")]
    [InlineData("""{"type":"box","box":{"top":0,"bottom":1,"left":0,"right":1},"confidence":"high"}""", "answers.b.confidence")]
    public async Task MalformedBoxAnswer_ThrowsValidationException(string answer, string fieldPath)
    {
        var body = $$$"""{"model":"m","usage":{},"answers":{"b":{{{answer}}}}}""";
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok(body)));

        var error = await Assert.ThrowsAsync<TypeSafeResponseValidationException>(() => client.Call());

        Assert.Equal(fieldPath, error.FieldPath);
    }

    public static TheoryData<Dictionary<string, Question>, string> InvalidQuestions => new()
    {
        { new Dictionary<string, Question>(), "At least one question is required." },
        { new() { ["n"] = new NoulQuestion() }, "Noul question \"n\" has neither instructions nor criteria; at least one is required." },
        { new() { ["n"] = new NoulQuestion { Criteria = new NoulCriteria() } }, "Noul question \"n\" has neither instructions nor criteria; at least one is required." },
        { new() { ["c"] = new ChoiceQuestion { Criteria = new Dictionary<string, object?>() } }, "Choice question \"c\" has no criteria; at least one choice is required." },
        { new() { ["s"] = new ScoreQuestion { Criteria = ["only"] } }, "Score question \"s\" has 1 criteria; at least two scores are required." },
        { new() { ["x"] = null! }, "Question \"x\" is null." },
    };

    [Theory]
    [MemberData(nameof(InvalidQuestions))]
    public async Task InvalidQuestions_ThrowBeforeSending(Dictionary<string, Question> questions, string message)
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        var error = await Assert.ThrowsAsync<TypeSafeException>(() => client.SystemOneAsync("state", questions));

        Assert.Equal(message, error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Instructions_AreOptionalWhenCriteriaDescribeTheQuestion()
    {
        var handler = new StubHandler(_ => Reply.Ok(Sample.Empty));
        using var client = TestClient.Create(handler);

        await client.SystemOneAsync("state", new Dictionary<string, Question>
        {
            ["n"] = new NoulQuestion { Criteria = new NoulCriteria { False = "Not spam" } },
            ["c"] = new ChoiceQuestion { Criteria = new Dictionary<string, object?> { ["a"] = null } },
            ["s"] = new ScoreQuestion { Criteria = [null, "high"] },
        });

        JsonAssert.Equal(
            """
            {
              "n": { "type": "noul", "criteria": { "false": "Not spam" } },
              "c": { "type": "choice", "criteria": { "a": null } },
              "s": { "type": "score", "criteria": [null, "high"] }
            }
            """,
            handler.Requests[0].Json["questions"]);
    }

    [Fact]
    public async Task NonFiniteNumbers_AreSentAsStrings()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        await client.Call(state: new { a = double.NaN, b = double.PositiveInfinity, c = 1.5 });

        JsonAssert.Equal("""{"a":"NaN","b":"Infinity","c":1.5}""", handler.Requests[0].Json["state"]);
    }

    [Fact]
    public async Task SdkHeaders_IdentifyTheClient()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        await client.Call();

        var headers = handler.Requests[0].Headers;
        Assert.Equal("application/json", Assert.Single(headers.Accept).MediaType);
        Assert.Equal($"typesafe-sdk-dotnet/{TypeSafeClient.Version}", string.Join(' ', headers.GetValues("User-Agent")));
        Assert.Equal($"typesafe-sdk-dotnet/{TypeSafeClient.Version}", Assert.Single(headers.GetValues("X-TypeSafe-SDK")));
        Assert.StartsWith($"dotnet/{Environment.Version} (", Assert.Single(headers.GetValues("X-TypeSafe-Runtime")));
        Assert.False(headers.Contains("X-TypeSafe-Retry-Count"));
        Assert.Matches(@"^\d+\.\d+\.\d+", TypeSafeClient.Version);
    }

    [Fact]
    public async Task Retries_SendTheRetryCount()
    {
        var handler = new StubHandler(_ => Reply.Error(503));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry(2));

        await Assert.ThrowsAsync<TypeSafeInternalServerException>(() => client.Call());

        Assert.Equal(
            new[] { null, "1", "2" },
            handler.Requests.Select(r => r.Headers.TryGetValues("X-TypeSafe-Retry-Count", out var v) ? v.Single() : null));
    }

    [Fact]
    public async Task Headers_PerCallOverrideDefaultsButNotSdkHeaders()
    {
        var handler = new StubHandler(request => Reply.Ok(request.Method == HttpMethod.Get ? Sample.ModelList : Sample.Result));
        using var client = TestClient.Create(
            handler,
            defaultHeaders: new Dictionary<string, string>
            {
                ["TypeSafe-Danger-Zone-Images"] = "true",
                ["X-Team"] = "default",
                ["Authorization"] = "Bearer stolen",
            });

        await client.SystemOneAsync(
            "state",
            Sample.Questions,
            headers: new Dictionary<string, string>
            {
                ["x-team"] = "per-call",
                ["Content-Type"] = "text/plain",
                ["Content-Language"] = "en",
            });
        await client.ListModelsAsync(headers: new Dictionary<string, string> { ["X-Trace"] = "abc" });

        var call = handler.Requests[0];
        Assert.Equal("true", Assert.Single(call.Headers.GetValues("TypeSafe-Danger-Zone-Images")));
        Assert.Equal("per-call", Assert.Single(call.Headers.GetValues("X-Team")));
        Assert.Equal("test-key", call.Headers.Authorization?.Parameter);
        Assert.Equal("application/json", call.ContentType);
        Assert.Equal("en", Assert.Single(call.ContentHeaders!.ContentLanguage));

        var models = handler.Requests[1];
        Assert.Equal("default", Assert.Single(models.Headers.GetValues("X-Team")));
        Assert.Equal("abc", Assert.Single(models.Headers.GetValues("X-Trace")));
    }

    [Fact]
    public async Task LargeBodies_AreCompressedWithZstdOnEveryAttempt()
    {
        var handler = new StubHandler(_ => Reply.Error(503));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry(1));
        var state = new string('x', 2000);

        await Assert.ThrowsAsync<TypeSafeInternalServerException>(() => client.Call(state: state));

        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.Equal("zstd", Assert.Single(request.ContentHeaders!.ContentEncoding));
            Assert.True(request.Bytes.Length < 1000);
            using var decompressor = new ZstdSharp.Decompressor();
            var json = JsonNode.Parse(Encoding.UTF8.GetString(decompressor.Unwrap(request.Bytes)))!;
            Assert.Equal(state, (string?)json["state"]);
        }
    }

    [Theory]
    [InlineData(null, 100, false)]
    [InlineData(TypeSafeCompression.Zstd, 100, false)]
    [InlineData(TypeSafeCompression.None, 5000, false)]
    [InlineData(TypeSafeCompression.Zstd, 5000, true)]
    public async Task Compression_AppliesOnlyToLargeBodiesWhenEnabled(TypeSafeCompression? compression, int stateLength, bool compressed)
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler, compression: compression);

        await client.Call(state: new string('x', stateLength));

        Assert.Equal(compressed, handler.Requests[0].ContentHeaders!.ContentEncoding.Contains("zstd"));
    }

    [Fact]
    public async Task BlankEnvironmentValues_AreIgnored()
    {
        using var env = new EnvScope().Set(TypeSafeClient.DefaultModelEnv, "  ").Set(TypeSafeClient.BaseUrlEnv, "");
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        await client.Call();

        Assert.Equal("https://api.typesafe.ai/v1/systemone", handler.Requests[0].Uri.ToString());
        Assert.Equal("jev-latest", (string?)handler.Requests[0].Json["model"]);
    }

    [Fact]
    public void MaxRetryAfter_FallsBackToBackoffForLongerServerDelays()
    {
        var policy = new RetryPolicy { BackoffJitter = 0, MaxRetryAfter = TimeSpan.FromSeconds(10) };
        TypeSafeApiException Error(string seconds) => TypeSafeApiException.Create(
            System.Net.HttpStatusCode.TooManyRequests, "{}", Headers.Response(("Retry-After", seconds)), "POST x");

        Assert.Null(new RetryPolicy().MaxRetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayFor(0, Error("10")));
        Assert.Equal(TimeSpan.FromSeconds(0.5), policy.DelayFor(0, Error("11")));
    }
}
