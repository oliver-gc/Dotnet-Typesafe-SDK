using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypesafeSdk.Models;

namespace TypesafeSdk.Tests;

public class SystemOneTests
{
    private static Dictionary<K, V> Dict<K, V>(IReadOnlyDictionary<K, V> source) where K : notnull =>
        source.ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public async Task RoundTrip_SendsExpectedRequestAndParsesResponse()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        var result = await client.Call(
            state: new Dictionary<string, object?> { ["document"] = "Hello ðŸŒ" },
            questions: new Dictionary<string, Question>
            {
                ["spam"] = new NoulQuestion { Instructions = "Spam?" },
                ["tone"] = new ChoiceQuestion
                {
                    Instructions = "Tone?",
                    Criteria = new Dictionary<string, object?> { ["friendly"] = null, ["hostile"] = null },
                },
                ["quality"] = new ScoreQuestion { Instructions = "Quality?", Criteria = ["bad", "ok", "great"] },
            });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", request.Uri.ToString());
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
        JsonAssert.Equal(
            """
            {
              "state": { "document": "Hello ðŸŒ" },
              "model": "jev-latest",
              "questions": {
                "spam": { "type": "noul", "instructions": "Spam?" },
                "tone": { "type": "choice", "instructions": "Tone?", "criteria": { "friendly": null, "hostile": null } },
                "quality": { "type": "score", "instructions": "Quality?", "criteria": ["bad", "ok", "great"] }
              }
            }
            """,
            request.Json);

        Assert.Equal("jev-latest", result.Model);
        Assert.Equal(new Usage(12, 3), result.Usage);
        Assert.Equal(new[] { "quality", "spam", "tone" }, result.Answers.Keys.Order());
        Assert.Equal(new[] { "spam" }, result.Nouls.Keys);
        Assert.Equal(new[] { "tone" }, result.Choices.Keys);
        Assert.Equal(new[] { "quality" }, result.Scores.Keys);

        Assert.Equal(0.98, result.Nouls["spam"].Noul);

        var tone = result.Choices["tone"];
        Assert.Equal("friendly", tone.Choice);
        Assert.Equal(0.9, tone.Confidence);
        Assert.Equal(new Dictionary<string, double> { ["friendly"] = 0.9, ["hostile"] = 0.1 }, Dict(tone.Probabilities));

        var quality = result.Scores["quality"];
        Assert.Equal(1.7, quality.Score);
        Assert.Equal(0.8, quality.Confidence);
        Assert.Equal(new[] { "bad", "ok", "great" }, quality.Legend.OrderBy(kv => kv.Key).Select(kv => kv.Value.GetString()));
        Assert.Equal(new Dictionary<int, double> { [0] = 0.1, [1] = 0.1, [2] = 0.8 }, Dict(quality.Probabilities));
    }

    [Fact]
    public async Task Response_CarriesRequestIdAndRawBody()
    {
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok(Sample.Result, ("x-typesafe-request-id", "req-42"))));

        var result = await client.Call();

        Assert.Equal("req-42", result.RequestId);
        Assert.Equal(Sample.Result, result.RawBody);
    }

    [Fact]
    public async Task Response_WithoutRequestIdHeader_HasNullRequestId()
    {
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok()));

        Assert.Null((await client.Call()).RequestId);
    }

    [Fact]
    public async Task ExtraBody_OverridesFieldsShallowly()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        await client.Call(
            state: "hi",
            model: "call-model",
            extraBody: new Dictionary<string, object?>
            {
                ["model"] = "override-model",
                ["beam_width"] = 4,
                ["nullable"] = null,
            });

        JsonAssert.Equal(
            """
            {
              "state": "hi",
              "model": "override-model",
              "questions": { "q": { "type": "noul", "instructions": "?" } },
              "beam_width": 4,
              "nullable": null
            }
            """,
            handler.Requests[0].Json);
    }

    [Fact]
    public async Task State_PropertyNamesAreNotRenamed()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        await client.Call(state: new { DocumentText = "x", Nested = new { InnerValue = 1 } });

        JsonAssert.Equal("""{"DocumentText":"x","Nested":{"InnerValue":1}}""", handler.Requests[0].Json["state"]);
    }

    [Fact]
    public async Task State_NullPropertiesAreOmittedButNullDictionaryValuesAreKept()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        await client.Call(state: new Dictionary<string, object?> { ["missing"] = null, ["items"] = new object?[] { null, "a" } });

        JsonAssert.Equal("""{"missing":null,"items":[null,"a"]}""", handler.Requests[0].Json["state"]);
    }

    [Fact]
    public async Task NullArguments_ThrowBeforeAnyRequest()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SystemOneAsync(null!, Sample.Questions));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SystemOneAsync("x", null!));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null, "client-model")]
    [InlineData("request-model", "request-model")]
    public async Task Model_PerCallOverridesClientDefault(string? callModel, string expected)
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler, model: "client-model");

        await client.Call(model: callModel ?? null);
        var sent = (string?)handler.Requests[0].Json["model"];

        Assert.Equal(callModel ?? "client-model", sent);
        _ = expected;
    }

    [Fact]
    public async Task RichDescriptions_ArePassedThroughAndLegendKeepsStructure()
    {
        var handler = new StubHandler(_ => Reply.Ok("""
            {
              "model": "custom",
              "usage": { "input_tokens": 1, "output_tokens": 1 },
              "answers": {
                "risk": {
                  "type": "score", "score": 0, "confidence": 1,
                  "legend": { "0": { "summary": "duplicated", "examples": ["charged twice"] } },
                  "probabilities": { "0": 1 }
                }
              }
            }
            """));
        using var client = TestClient.Create(handler);
        var rich = new Dictionary<string, object> { ["summary"] = "duplicated", ["examples"] = new[] { "charged twice" } };

        var result = await client.Call(
            state: "a ticket",
            model: "custom",
            questions: new Dictionary<string, Question>
            {
                ["duplicate"] = new NoulQuestion
                {
                    Instructions = new Dictionary<string, object> { ["question"] = "Duplicate?" },
                    Criteria = new NoulCriteria { True = rich },
                },
                ["team"] = new ChoiceQuestion
                {
                    Instructions = "Team?",
                    Criteria = new Dictionary<string, object?> { ["billing"] = rich, ["other"] = null },
                },
                ["risk"] = new ScoreQuestion { Instructions = "Risk?", Criteria = [rich] },
            });

        var summary = """{"summary":"duplicated","examples":["charged twice"]}""";
        JsonAssert.Equal(
            $$"""
            {
              "duplicate": { "type": "noul", "instructions": { "question": "Duplicate?" }, "criteria": { "true": {{summary}} } },
              "team": { "type": "choice", "instructions": "Team?", "criteria": { "billing": {{summary}}, "other": null } },
              "risk": { "type": "score", "instructions": "Risk?", "criteria": [ {{summary}} ] }
            }
            """,
            handler.Requests[0].Json["questions"]);

        var legend = result.Scores["risk"].Legend[0];
        Assert.Equal("duplicated", legend.GetProperty("summary").GetString());
        Assert.Equal("charged twice", legend.GetProperty("examples")[0].GetString());
    }

    [Theory]
    [MemberData(nameof(NoulCriteriaCases))]
    public async Task NoulCriteria_AreOptionalAndOnlySetSidesAreSent(NoulCriteria? criteria, string expectedQuestion)
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        await client.Call(questions: new Dictionary<string, Question>
        {
            ["q"] = new NoulQuestion { Instructions = "Spam?", Criteria = criteria },
        });

        JsonAssert.Equal(expectedQuestion, handler.Requests[0].Json["questions"]!["q"]);
    }

    public static TheoryData<NoulCriteria?, string> NoulCriteriaCases => new()
    {
        { null, """{"type":"noul","instructions":"Spam?"}""" },
        { new NoulCriteria(), """{"type":"noul","instructions":"Spam?","criteria":{}}""" },
        { new NoulCriteria { True = "Yes" }, """{"type":"noul","instructions":"Spam?","criteria":{"true":"Yes"}}""" },
        { new NoulCriteria { False = "No" }, """{"type":"noul","instructions":"Spam?","criteria":{"false":"No"}}""" },
        {
            new NoulCriteria { True = "Yes", False = "No" },
            """{"type":"noul","instructions":"Spam?","criteria":{"true":"Yes","false":"No"}}"""
        },
    };

    [Fact]
    public async Task ArrayInputs_AreEncodedForStateInstructionsAndCriteria()
    {
        var handler = new StubHandler(_ => Reply.Ok(Sample.Empty));
        using var client = TestClient.Create(handler);
        var state = new object?[] { new Dictionary<string, object?> { ["message"] = "Classify" }, null };
        var instructions = new object?[] { "Read the message", new Dictionary<string, object?> { ["context"] = null } };
        var description = new object?[] { "Example", null };

        await client.Call(
            state: state,
            questions: new Dictionary<string, Question>
            {
                ["yes"] = new NoulQuestion { Instructions = instructions, Criteria = new NoulCriteria { True = description } },
                ["label"] = new ChoiceQuestion
                {
                    Instructions = instructions,
                    Criteria = new Dictionary<string, object?> { ["a"] = description, ["b"] = null },
                },
                ["rating"] = new ScoreQuestion { Instructions = instructions, Criteria = [description] },
            });

        var body = handler.Requests[0].Json;
        JsonAssert.Equal("""[{"message":"Classify"},null]""", body["state"]);
        JsonAssert.Equal(
            """
            {
              "yes": { "type": "noul", "instructions": ["Read the message", {"context": null}], "criteria": { "true": ["Example", null] } },
              "label": { "type": "choice", "instructions": ["Read the message", {"context": null}], "criteria": { "a": ["Example", null], "b": null } },
              "rating": { "type": "score", "instructions": ["Read the message", {"context": null}], "criteria": [ ["Example", null] ] }
            }
            """,
            body["questions"]);
    }

    [Fact]
    public async Task ReadOnlyCollections_AreAccepted()
    {
        var handler = new StubHandler(_ => Reply.Ok(Sample.Empty));
        using var client = TestClient.Create(handler);
        IReadOnlyDictionary<string, object?> criteria = new Dictionary<string, object?> { ["a"] = null, ["b"] = "x" }.AsReadOnly();

        await client.Call(
            state: new Dictionary<string, object?> { ["items"] = (IReadOnlyList<object?>)new List<object?> { "a", null }.AsReadOnly() },
            questions: new Dictionary<string, Question>
            {
                ["label"] = new ChoiceQuestion { Instructions = "read", Criteria = criteria },
                ["rating"] = new ScoreQuestion { Instructions = "rate", Criteria = new[] { "low", "high" } },
            });

        var body = handler.Requests[0].Json;
        JsonAssert.Equal("""{"items":["a",null]}""", body["state"]);
        JsonAssert.Equal("""["low","high"]""", body["questions"]!["rating"]!["criteria"]);
        JsonAssert.Equal("""{"a":null,"b":"x"}""", body["questions"]!["label"]!["criteria"]);
    }

    [Fact]
    public async Task ResponseWithUnknownExtraFields_IsTolerated()
    {
        const string body = """
            {
              "model": "test",
              "usage": { "input_tokens": 1, "output_tokens": 1, "reasoning_tokens": 9, "billing_units": 1 },
              "answers": { "spam": { "type": "noul", "noul": 0.9, "explanation": "spammy" } }
            }
            """;
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok(body)));

        var result = await client.Call();

        Assert.Equal(0.9, result.Nouls["spam"].Noul);
        Assert.Equal(new Usage(1, 1), result.Usage);
        Assert.Equal(body, result.RawBody);
    }

    [Fact]
    public async Task UnknownAnswerKind_IsSkippedButKeptInRawBody()
    {
        const string body = """
            {
              "model": "test",
              "usage": { "input_tokens": 1, "output_tokens": 1 },
              "answers": { "spam": { "type": "noul", "noul": 0.9 }, "mystery": { "type": "aurora", "value": 3 } }
            }
            """;
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok(body, ("x-typesafe-request-id", "req-9"))));

        var result = await client.Call();

        Assert.Equal(new[] { "spam" }, result.Answers.Keys);
        Assert.Equal(0.9, result.Nouls["spam"].Noul);
        Assert.Equal("aurora", JsonNode.Parse(result.RawBody)!["answers"]!["mystery"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"usage":{},"answers":{}}""", "model")]
    [InlineData("""{"model":"m","answers":{}}""", "usage")]
    [InlineData("""{"model":"m","usage":{},"answers":[]}""", "answers")]
    [InlineData("""{"model":"m","usage":{},"answers":{"n":{"type":"noul"}}}""", "answers.n.noul")]
    [InlineData("not json", "$")]
    [InlineData("[]", "$")]
    public async Task MalformedResponse_ThrowsValidationExceptionWithFieldPath(string body, string fieldPath)
    {
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok(body, ("x-typesafe-request-id", "req-123"))));

        var error = await Assert.ThrowsAsync<TypeSafeResponseValidationException>(() => client.Call());

        Assert.Equal(fieldPath, error.FieldPath);
        Assert.Equal(HttpStatusCode.OK, error.Status);
        Assert.Equal("req-123", error.RequestId);
        Assert.Equal(body, error.Body);
        Assert.Equal($"Invalid response from POST https://api.typesafe.ai/v1/systemone: bad or missing '{fieldPath}'.", error.Message);
    }

    [Fact]
    public async Task CancelledToken_PropagatesAsOperationCanceled()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply.Ok();
        });
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Call(ct: cts.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SuppliedHttpClient_IsNotDisposedWithTheSdkClient()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var http = new HttpClient(handler);
        var client = new TypeSafeClient(new TypeSafeClientOptions { ApiKey = "k", HttpClient = http });

        await client.Call();
        client.Dispose();

        using var response = await http.GetAsync("https://example.test/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentCalls_DoNotShareState()
    {
        var handler = new StubHandler(async (request, _) =>
        {
            await Task.Yield();
            return Reply.Ok();
        });
        using var client = TestClient.Create(handler);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => client.Call(state: $"state-{i}", model: $"model-{i}")));

        Assert.Equal(8, results.Length);
        var sent = handler.Requests.Select(r => ((string)r.Json["state"]!, (string)r.Json["model"]!)).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, 8).Select(i => ($"state-{i}", $"model-{i}")).Order(), sent);
    }
}

