namespace TypesafeSdk;

/// <summary>Unset values fall back to TYPESAFE_API_KEY, TYPESAFE_BASE_URL, TYPESAFE_DEFAULT_MODEL and TYPESAFE_LOG_LEVEL, then to defaults.</summary>
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
}
