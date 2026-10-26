namespace TypesafeSdk;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
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

    /// <summary>The SDK version sent in the User-Agent and X-TypeSafe-SDK headers.</summary>
    public static readonly string Version = ReadVersion();

    /// <summary>Bodies below this many bytes fit in a single TCP segment, so they are sent uncompressed.</summary>
    internal const int MinCompressedBodyBytes = 1024;

    // Relaxed escaping writes characters such as < and > as-is, so asset names and references match the JSON text.
    private static readonly JavaScriptEncoder JsonEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    // No naming policy: caller-supplied state and instructions must reach the API unchanged.
    // NaN and infinities are sent as strings, since JSON has no literal for them.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JsonEncoder,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly string[] SecretHeaderMarkers = ["authorization", "api-key", "apikey", "cookie", "token", "secret"];

    // Set by the SDK on every request; caller headers with these names are ignored.
    private static readonly HashSet<string> SdkHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Accept", "User-Agent", "X-TypeSafe-SDK", "X-TypeSafe-Runtime", "X-TypeSafe-Retry-Count",
        "Content-Type", "Content-Encoding", "Content-Length",
    };

    private static readonly string Runtime = DescribeRuntime();

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly Uri _systemOneEndpoint;
    private readonly Uri _modelsEndpoint;
    private readonly string _model;
    private readonly RetryPolicy _retry;
    private readonly TimeSpan _timeout;
    private readonly TypeSafeLogLevel _logLevel;
    private readonly Action<TypeSafeLogLevel, string> _logger;
    private readonly IReadOnlyDictionary<string, string> _defaultHeaders;
    private readonly TypeSafeCompression _compression;

    public TypeSafeClient(TypeSafeClientOptions? options = null)
    {
        options ??= new TypeSafeClientOptions();

        _apiKey = ValidateApiKey(options.ApiKey ?? ReadEnv(ApiKeyEnv));

        var baseUrl = options.BaseUrl ?? ReadEnv(BaseUrlEnv) ?? DefaultBaseUrl;
        _systemOneEndpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1/systemone");
        _modelsEndpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1/models");

        _model = options.Model ?? ReadEnv(DefaultModelEnv) ?? DefaultModel;
        _retry = options.Retry ?? new RetryPolicy();
        _timeout = options.Timeout ?? DefaultTimeout;

        _logLevel = options.LogLevel
            ?? (Enum.TryParse<TypeSafeLogLevel>(ReadEnv(LogLevelEnv), ignoreCase: true, out var envLevel)
                ? envLevel
                : TypeSafeLogLevel.Off);
        _logger = options.Logger ?? ((level, message) => Console.Error.WriteLine($"[typesafe] {level}: {message}"));
        _defaultHeaders = options.DefaultHeaders ?? new Dictionary<string, string>();
        _compression = options.Compression ?? TypeSafeCompression.Zstd;
        _ownsHttp = options.HttpClient is null;
        // Timeouts are enforced per attempt below, so the owned client must not impose its own.
        _http = options.HttpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Answers named questions about text, structured state, or images (POST /v1/systemone).</summary>
    /// <param name="state">Text, an <see cref="Image"/>, a <see cref="TextWithAssets"/>, or any JSON-serializable value.</param>
    /// <param name="questions">At least one question, keyed by the names that identify their answers.</param>
    /// <param name="model">Overrides the client's default model.</param>
    /// <param name="retry">Overrides the client's retry policy.</param>
    /// <param name="extraBody">Extra top-level body fields, overriding SDK-set fields with the same names.</param>
    /// <param name="assets">
    /// Images keyed by names of your choice, such as <c>&lt;photo&gt;</c>, for referencing them from plain strings in
    /// state, instructions or criteria. Every name must appear in the request. Images placed directly in the content
    /// or in <see cref="TextWithAssets"/> don't need to be listed.
    /// </param>
    /// <param name="headers">Extra headers for this call, merged over <see cref="TypeSafeClientOptions.DefaultHeaders"/>.</param>
    /// <param name="ct">Cancels the request and any pending retries.</param>
    /// <exception cref="TypeSafeException">
    /// The questions are invalid, a name in <paramref name="assets"/> isn't used, or a string holds the reference of an
    /// image that isn't attached. Thrown before anything is sent.
    /// </exception>
    public async Task<SystemOneResponse> SystemOneAsync(
        object state,
        IReadOnlyDictionary<string, Question> questions,
        string? model = null,
        RetryPolicy? retry = null,
        IReadOnlyDictionary<string, object?>? extraBody = null,
        IReadOnlyDictionary<string, Image>? assets = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);
        QuestionValidation.Validate(questions);

        // Serialized now, so changes to the arguments after this call don't change what is sent.
        var body = new JsonObject();
        var (payload, sent) = RequestAssets.Serialize(assets, JsonEncoder, () =>
        {
            body["state"] = JsonSerializer.SerializeToNode(state, JsonOptions);
            body["model"] = model ?? _model;
            body["questions"] = JsonSerializer.SerializeToNode(questions, JsonOptions);

            if (extraBody is not null)
            {
                foreach (var (key, value) in extraBody)
                {
                    body[key] = JsonSerializer.SerializeToNode(value, JsonOptions);
                }
            }

            return body.ToJsonString(JsonOptions);
        });

        RequestBody content;
        if (sent.Count == 0)
        {
            content = CreateBody(Encoding.UTF8.GetBytes(payload), "application/json; charset=utf-8", payload);
        }
        else
        {
            // Each image goes in its own part; an image under several names is sent once per name.
            var parts = sent.Select((entry, i) => (Part: $"asset-{i}", Image: entry.Value)).ToList();
            var entries = new JsonObject();
            for (var i = 0; i < sent.Count; i++)
            {
                entries[sent[i].Key] = new JsonObject { ["type"] = "image", ["part"] = parts[i].Part };
            }

            body["assets"] = entries;
            payload = body.ToJsonString(JsonOptions);
            var (bytes, contentType) = RequestAssets.EncodeMultipart(payload, parts);
            content = CreateBody(bytes, contentType, $"{payload}\n[{parts.Count} image part(s), {bytes.Length} bytes]");
        }

        return await ExecuteAsync(
            retry,
            headers,
            (attempt, sendHeaders, token) =>
                SendOnceAsync(HttpMethod.Post, _systemOneEndpoint, content, attempt, sendHeaders, ResponseParser.Parse, token),
            ct);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task<SystemOneResponse> SystemOneAsync(
        object state,
        IReadOnlyDictionary<string, Question> questions,
        string? model,
        RetryPolicy? retry,
        IReadOnlyDictionary<string, object?>? extraBody,
        CancellationToken ct) =>
        SystemOneAsync(state, questions, model, retry, extraBody, assets: null, headers: null, ct);

    /// <summary>Lists the models and aliases available to the account (GET /v1/models).</summary>
    /// <param name="retry">Overrides the client's retry policy.</param>
    /// <param name="headers">Extra headers for this call, merged over <see cref="TypeSafeClientOptions.DefaultHeaders"/>.</param>
    /// <param name="ct">Cancels the request and any pending retries.</param>
    public Task<ModelListResponse> ListModelsAsync(
        RetryPolicy? retry = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default) =>
        ExecuteAsync(
            retry,
            headers,
            (attempt, sendHeaders, token) => SendOnceAsync(
                HttpMethod.Get, _modelsEndpoint, null, attempt, sendHeaders, (body, id, _) => ResponseParser.ParseModels(body, id), token),
            ct);

    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task<ModelListResponse> ListModelsAsync(RetryPolicy? retry, CancellationToken ct) =>
        ListModelsAsync(retry, headers: null, ct);

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<T> ExecuteAsync<T>(
        RetryPolicy? retry,
        IReadOnlyDictionary<string, string>? headers,
        Func<int, IReadOnlyDictionary<string, string>, CancellationToken, Task<T>> send,
        CancellationToken ct)
    {
        var policy = retry ?? _retry;
        var merged = MergeHeaders(_defaultHeaders, headers);
        var started = Stopwatch.GetTimestamp();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await send(attempt, merged, ct);
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

    private async Task<T> SendOnceAsync<T>(
        HttpMethod method,
        Uri endpoint,
        RequestBody? body,
        int attempt,
        IReadOnlyDictionary<string, string> headers,
        Func<string, string?, Action<string>?, T> parse,
        CancellationToken ct)
    {
        var endpointLabel = $"{method} {endpoint.GetLeftPart(UriPartial.Path)}";

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(_timeout);

        using var request = new HttpRequestMessage(method, endpoint);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body.Content);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(body.ContentType);
            if (body.ContentEncoding is not null)
            {
                request.Content.Headers.ContentEncoding.Add(body.ContentEncoding);
            }
        }

        // Caller headers skip the SDK's own, so they can't clobber auth, the content type, or the encoding.
        foreach (var (name, value) in headers)
        {
            if (!SdkHeaders.Contains(name) && !request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        request.Headers.Authorization = new("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", $"typesafe-sdk-dotnet/{Version}");
        request.Headers.TryAddWithoutValidation("X-TypeSafe-SDK", $"typesafe-sdk-dotnet/{Version}");
        request.Headers.TryAddWithoutValidation("X-TypeSafe-Runtime", Runtime);
        if (attempt > 0)
        {
            request.Headers.TryAddWithoutValidation("X-TypeSafe-Retry-Count", attempt.ToString(CultureInfo.InvariantCulture));
        }

        if (IsEnabled(TypeSafeLogLevel.Debug))
        {
            var requestHeaders = request.Content is null
                ? FormatHeaders(request.Headers)
                : FormatHeaders(request.Headers, request.Content.Headers);
            Log(TypeSafeLogLevel.Debug, $"Request: {endpointLabel}\n{requestHeaders}\n{body?.LogText}");
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var response = await _http.SendAsync(request, attemptCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(attemptCts.Token);

            var requestId = response.Headers.TryGetValues("x-typesafe-request-id", out var ids)
                ? ids.FirstOrDefault()
                : null;

            var summary = $"{endpointLabel} -> {(int)response.StatusCode} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms"
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
                throw TypeSafeApiException.Create(response.StatusCode, responseBody, response.Headers, endpointLabel);
            }

            try
            {
                return parse(
                    responseBody,
                    requestId,
                    kind => Log(TypeSafeLogLevel.Warning, $"Skipping unknown answer kind at {kind}."));
            }
            catch (InvalidFieldException invalid)
            {
                Log(TypeSafeLogLevel.Error, $"Invalid response from {endpointLabel}: bad or missing '{invalid.FieldPath}'.");
                throw new TypeSafeResponseValidationException(
                    response.StatusCode, responseBody, response.Headers, endpointLabel, invalid.FieldPath);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log(TypeSafeLogLevel.Warning, $"{endpointLabel} timed out after {_timeout.TotalSeconds:0.#}s.");
            throw new TypeSafeTimeoutException(_timeout, ex);
        }
        catch (HttpRequestException ex)
        {
            Log(TypeSafeLogLevel.Warning, $"{endpointLabel} failed: {ex.Message}");
            throw new TypeSafeConnectionException($"Could not reach {endpointLabel}: {ex.Message}", ex);
        }
    }

    // Compressed once and sent as-is on every attempt; the logs show the uncompressed payload.
    private RequestBody CreateBody(byte[] content, string contentType, string logText)
    {
        if (_compression == TypeSafeCompression.None || content.Length < MinCompressedBodyBytes)
        {
            return new RequestBody(content, contentType, null, logText);
        }

        using var compressor = new ZstdSharp.Compressor();
        return new RequestBody(compressor.Wrap(content).ToArray(), contentType, "zstd", logText);
    }

    // Later sources win, matching names case-insensitively.
    private static Dictionary<string, string> MergeHeaders(
        IReadOnlyDictionary<string, string> defaults, IReadOnlyDictionary<string, string>? overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in defaults.Concat(overrides ?? new Dictionary<string, string>()))
        {
            merged[name] = value;
        }

        return merged;
    }

    // Blank values count as unset.
    private static string? ReadEnv(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value ? value : null;

    private static string ReadVersion()
    {
        var assembly = typeof(TypeSafeClient).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "unknown";

        // Drop the source revision appended to the informational version, such as 1.1.0+6a88392.
        return version.Split('+')[0];
    }

    private static string DescribeRuntime()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";
        return $"dotnet/{Environment.Version} ({os}; {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})";
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

internal sealed record RequestBody(byte[] Content, string ContentType, string? ContentEncoding, string LogText);
