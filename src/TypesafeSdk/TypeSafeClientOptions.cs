namespace TypesafeSdk;

/// <summary>Unset or blank values fall back to TYPESAFE_API_KEY, TYPESAFE_BASE_URL, TYPESAFE_DEFAULT_MODEL and TYPESAFE_LOG_LEVEL, then to defaults.</summary>
public sealed class TypeSafeClientOptions
{
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public RetryPolicy? Retry { get; init; }

    /// <summary>Timeout for each HTTP attempt. Defaults to 10 seconds.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Caller-owned client; the SDK won't dispose it.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// Info logs one line per request; Debug also logs headers and bodies. Secret headers are redacted, bodies are not.
    /// Defaults to TYPESAFE_LOG_LEVEL (off, error, warning, info, debug), else Off.
    /// </summary>
    public TypeSafeLogLevel? LogLevel { get; init; }

    /// <summary>Log sink; defaults to standard error.</summary>
    public Action<TypeSafeLogLevel, string>? Logger { get; init; }

    /// <summary>
    /// Headers sent with every request; per-call headers take precedence. Headers the SDK sets, such as
    /// Authorization and Content-Type, can't be overridden.
    /// </summary>
    public IReadOnlyDictionary<string, string>? DefaultHeaders { get; init; }

    /// <summary>Compression for request bodies of 1 KiB or more. Defaults to <see cref="TypeSafeCompression.Zstd"/>.</summary>
    public TypeSafeCompression? Compression { get; init; }
}

public enum TypeSafeCompression
{
    /// <summary>Send request bodies uncompressed, for example to an API that doesn't accept compressed requests.</summary>
    None = 0,

    /// <summary>Compress request bodies of 1 KiB or more with zstd, sent as Content-Encoding: zstd.</summary>
    Zstd = 1,
}
