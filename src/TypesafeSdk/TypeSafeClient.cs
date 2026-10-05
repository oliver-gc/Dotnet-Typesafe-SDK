namespace TypesafeSdk;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TypesafeSdk.Models;

public sealed class TypeSafeClient : IDisposable
{
    public const string ApiKeyEnv = "TYPESAFE_API_KEY";
    public const string BaseUrlEnv = "TYPESAFE_BASE_URL";
    public const string DefaultModelEnv = "TYPESAFE_DEFAULT_MODEL";
    public const string LogLevelEnv = "TYPESAFE_LOG_LEVEL";
    public const string DefaultBaseUrl = "https://api.typesafe.ai";
    public const string DefaultModel = "jev-latest";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    // No naming policy: caller-supplied state and instructions must reach the API unchanged.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] SecretHeaderMarkers = ["authorization", "api-key", "apikey", "cookie", "token", "secret"];

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly Uri _endpoint;
    private readonly string _endpointLabel;
    private readonly string _model;
    private readonly RetryPolicy _retry;
    private readonly TimeSpan _timeout;
    private readonly TypeSafeLogLevel _logLevel;
    private readonly Action<TypeSafeLogLevel, string> _logger;
    public TypeSafeClient(TypeSafeClientOptions? options = null)
    {
        options ??= new TypeSafeClientOptions();

        _apiKey = ValidateApiKey(options.ApiKey ?? Environment.GetEnvironmentVariable(ApiKeyEnv));

        var baseUrl = options.BaseUrl ?? Environment.GetEnvironmentVariable(BaseUrlEnv) ?? DefaultBaseUrl;
        _endpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1/systemone");
        _endpointLabel = $"POST {_endpoint.GetLeftPart(UriPartial.Path)}";

        _model = options.Model ?? Environment.GetEnvironmentVariable(DefaultModelEnv) ?? DefaultModel;
        _retry = options.Retry ?? new RetryPolicy();
        _timeout = options.Timeout ?? DefaultTimeout;

        _logLevel = options.LogLevel
            ?? (Enum.TryParse<TypeSafeLogLevel>(Environment.GetEnvironmentVariable(LogLevelEnv), ignoreCase: true, out var envLevel)
                ? envLevel
                : TypeSafeLogLevel.Off);
        _logger = options.Logger ?? ((level, message) => Console.Error.WriteLine($"[typesafe] {level}: {message}"));
        _ownsHttp = options.HttpClient is null;
        // Timeouts are enforced per attempt below, so the owned client must not impose its own.
        _http = options.HttpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<SystemOneResponse> SystemOneAsync(
        object state,
        IReadOnlyDictionary<string, Question> questions,
        string? model = null,
        RetryPolicy? retry = null,
        IReadOnlyDictionary<string, object?>? extraBody = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);

        var body = new JsonObject
        {
            ["state"] = JsonSerializer.SerializeToNode(state, JsonOptions),
            ["model"] = model ?? _model,
            ["questions"] = JsonSerializer.SerializeToNode(questions, JsonOptions),
        };

        if (extraBody is not null)
        {
            foreach (var (key, value) in extraBody)
            {
                body[key] = JsonSerializer.SerializeToNode(value, JsonOptions);
            }
        }

        var payload = body.ToJsonString(JsonOptions);
        var policy = retry ?? _retry;
        var started = Stopwatch.GetTimestamp();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(payload, ct);
            }
            catch (TypeSafeException error) when (attempt < policy.MaxRetries && policy.ShouldRetry(error))
            {
                var delay = policy.DelayFor(attempt, error);
                if (policy.Timeout is { } budget && Stopwatch.GetElapsedTime(started) + delay >= budget)
                {
                    throw;
                }

                Log(
                    TypeSafeLogLevel.Warning,
                    $"Attempt {attempt + 1} of {policy.MaxRetries + 1} failed ({error.GetType().Name}); retrying in {delay.TotalSeconds:0.##}s.");
                await Task.Delay(delay, ct);
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<SystemOneResponse> SendOnceAsync(string payload, CancellationToken ct)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(_timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", _apiKey);

        if (IsEnabled(TypeSafeLogLevel.Debug))
        {
            Log(
                TypeSafeLogLevel.Debug,
                $"Request: {_endpointLabel}\n{FormatHeaders(request.Headers, request.Content.Headers)}\n{payload}");
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var response = await _http.SendAsync(request, attemptCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(attemptCts.Token);

            var requestId = response.Headers.TryGetValues("x-typesafe-request-id", out var ids)
                ? ids.FirstOrDefault()
                : null;

            var summary = $"{_endpointLabel} -> {(int)response.StatusCode} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms"
                + (requestId is null ? "" : $" (request_id={requestId})");

            if (IsEnabled(TypeSafeLogLevel.Debug))
            {
                Log(
                    TypeSafeLogLevel.Debug,
                    $"Response: {summary}\n{FormatHeaders(response.Headers, response.Content.Headers)}\n{responseBody}");
            }

            Log(response.IsSuccessStatusCode ? TypeSafeLogLevel.Info : TypeSafeLogLevel.Warning, summary);

            if (!response.IsSuccessStatusCode)
            {
                throw TypeSafeApiException.Create(response.StatusCode, responseBody, response.Headers, _endpointLabel);
            }

            try
            {
                return ResponseParser.Parse(
                    responseBody,
                    requestId,
                    kind => Log(TypeSafeLogLevel.Warning, $"Skipping unknown answer kind at {kind}."));
            }
            catch (InvalidFieldException invalid)
            {
                Log(TypeSafeLogLevel.Error, $"Invalid response from {_endpointLabel}: bad or missing '{invalid.FieldPath}'.");
                throw new TypeSafeResponseValidationException(
                    response.StatusCode, responseBody, response.Headers, _endpointLabel, invalid.FieldPath);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log(TypeSafeLogLevel.Warning, $"{_endpointLabel} timed out after {_timeout.TotalSeconds:0.#}s.");
            throw new TypeSafeTimeoutException(_timeout, ex);
        }
        catch (HttpRequestException ex)
        {
            Log(TypeSafeLogLevel.Warning, $"{_endpointLabel} failed: {ex.Message}");
            throw new TypeSafeConnectionException($"Could not reach {_endpointLabel}: {ex.Message}", ex);
        }
    }

    private bool IsEnabled(TypeSafeLogLevel level) => _logLevel != TypeSafeLogLevel.Off && level <= _logLevel;

    private void Log(TypeSafeLogLevel level, string message)
    {
        if (IsEnabled(level))
        {
            _logger(level, message);
        }
    }

    private static string FormatHeaders(params HttpHeaders[] sets) =>
        string.Join('\n', sets.SelectMany(set => set).Select(header =>
            $"{header.Key}: {(IsSecretHeader(header.Key) ? "[redacted]" : string.Join(", ", header.Value))}"));

    private static bool IsSecretHeader(string name) =>
        SecretHeaderMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string ValidateApiKey(string? key)
    {
        if (key is null)
        {
            throw new TypeSafeException($"No API key provided. Set {ApiKeyEnv} or pass TypeSafeClientOptions.ApiKey.");
        }

        var trimmed = key.Trim();
        if (trimmed.Length == 0 || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c > 127))
        {
            throw new TypeSafeException("The API key is empty or contains whitespace, control or non-ASCII characters.");
        }

        return trimmed;
    }
}
