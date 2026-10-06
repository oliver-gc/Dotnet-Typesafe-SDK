using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TypesafeSdk.Models;

namespace TypesafeSdk.Tests;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, HttpRequestHeaders Headers, string? ContentType, string Body)
{
    public JsonNode Json => JsonNode.Parse(Body)!;
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly List<RecordedRequest> _requests = [];

    public StubHandler(Func<RecordedRequest, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public StubHandler(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method, request.RequestUri!, request.Headers, request.Content?.Headers.ContentType?.MediaType, body);

        lock (_requests)
        {
            _requests.Add(recorded);
        }

        return await _respond(recorded, cancellationToken);
    }
}

internal static class Reply
{
    public static HttpResponseMessage Json(HttpStatusCode status, string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public static HttpResponseMessage Ok(string json = Sample.Result, params (string Name, string Value)[] headers) =>
        Json(HttpStatusCode.OK, json, headers);

    public static HttpResponseMessage Error(int status, string body = """{"message":"failed"}""", params (string Name, string Value)[] headers) =>
        Json((HttpStatusCode)status, body, headers);
}

internal static class Sample
{
    public const string Result = """
        {
          "model": "jev-latest",
          "usage": { "input_tokens": 12, "output_tokens": 3 },
          "answers": {
            "spam": { "type": "noul", "noul": 0.98 },
            "tone": { "type": "choice", "choice": "friendly", "confidence": 0.9, "probabilities": { "friendly": 0.9, "hostile": 0.1 } },
            "quality": {
              "type": "score",
              "score": 1.7,
              "confidence": 0.8,
              "legend": { "0": "bad", "1": "ok", "2": "great" },
              "probabilities": { "0": 0.1, "1": 0.1, "2": 0.8 }
            }
          }
        }
        """;

    public const string ModelList = """
        {
          "models": [
            { "name": "jev-latest", "description": "The latest Jev release.", "release_date": "2026-05-01" },
            { "name": "jev-fast", "description": "Lower latency.", "release_date": "2026-03-12" }
          ]
        }
        """;
    public const string Empty = """{"model":"jev-latest","usage":{},"answers":{}}""";

    public static IReadOnlyDictionary<string, Question> Questions => new Dictionary<string, Question>
    {
        ["q"] = new NoulQuestion { Instructions = "?" },
    };
}

internal static class TestClient
{
    public static RetryPolicy FastRetry(int maxRetries = 2) => new()
    {
        MaxRetries = maxRetries,
        BackoffInitial = TimeSpan.FromMilliseconds(1),
        BackoffMax = TimeSpan.FromMilliseconds(1),
        BackoffJitter = 0,
    };

    /// <summary>A client over the stub; retries are off unless a policy is given.</summary>
    public static TypeSafeClient Create(
        StubHandler handler,
        string? apiKey = "test-key",
        string? baseUrl = null,
        string? model = null,
        RetryPolicy? retry = null,
        TimeSpan? timeout = null,
        TypeSafeLogLevel? logLevel = null,
        Action<TypeSafeLogLevel, string>? logger = null) =>
        new(new TypeSafeClientOptions
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            Model = model,
            Retry = retry ?? new RetryPolicy { MaxRetries = 0 },
            Timeout = timeout,
            LogLevel = logLevel,
            Logger = logger,
            HttpClient = new HttpClient(handler),
        });

    public static Task<SystemOneResponse> Call(
        this TypeSafeClient client,
        object? state = null,
        IReadOnlyDictionary<string, Question>? questions = null,
        string? model = null,
        RetryPolicy? retry = null,
        IReadOnlyDictionary<string, object?>? extraBody = null,
        CancellationToken ct = default) =>
        client.SystemOneAsync(state ?? "hello", questions ?? Sample.Questions, model, retry, extraBody, ct);
}

internal sealed class LogCapture
{
    private readonly List<(TypeSafeLogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(TypeSafeLogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public string Text => string.Join('\n', Entries.Select(e => e.Message));

    public void Write(TypeSafeLogLevel level, string message)
    {
        lock (_entries)
        {
            _entries.Add((level, message));
        }
    }
}

internal sealed class EnvScope : IDisposable
{
    private readonly Dictionary<string, string?> _original = [];

    public EnvScope Set(string name, string? value)
    {
        _original.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var (name, value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}

internal static class TestEnvironment
{
    [ModuleInitializer]
    public static void ClearSdkVariables()
    {
        foreach (var name in new[]
        {
            TypeSafeClient.ApiKeyEnv, TypeSafeClient.BaseUrlEnv, TypeSafeClient.DefaultModelEnv, TypeSafeClient.LogLevelEnv,
        })
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}

internal static class Headers
{
    public static HttpResponseHeaders Response(params (string Name, string Value)[] values)
    {
        var headers = new HttpResponseMessage().Headers;
        foreach (var (name, value) in values)
        {
            headers.TryAddWithoutValidation(name, value);
        }

        return headers;
    }
}

internal static class JsonAssert
{
    public static void Equal(string expected, JsonNode? actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"Expected: {JsonNode.Parse(expected)?.ToJsonString()}\nActual:   {actual?.ToJsonString()}");
}
