namespace TypesafeSdk;

public sealed record RetryPolicy
{
    private static readonly IReadOnlySet<int> DefaultStatuses = new HashSet<int> { 429, 500, 502, 503, 504, 529 };

    /// <summary>Retries after the initial attempt; 0 disables retries.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>First backoff delay, doubled each attempt up to <see cref="BackoffMax"/>.</summary>
    public TimeSpan BackoffInitial { get; init; } = TimeSpan.FromSeconds(0.5);

    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Fraction (0 to 1) of each delay randomly subtracted.</summary>
    public double BackoffJitter { get; init; } = 0.25;

    public IReadOnlySet<int> HttpStatuses { get; init; } = DefaultStatuses;

    /// <summary>Honor Retry-After and retry-after-ms response headers.</summary>
    public bool RespectRetryAfter { get; init; } = true;

    public bool RetryOnConnectionError { get; init; } = true;
    public bool RetryOnTimeout { get; init; } = true;

    /// <summary>Extra rule; returning true triggers a retry in addition to the built-in rules.</summary>
    public Func<TypeSafeException, bool>? Predicate { get; init; }

    /// <summary>Total budget per call including delays; null disables the limit.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromSeconds(30);

    internal bool ShouldRetry(TypeSafeException error) =>
        error switch
        {
            TypeSafeTimeoutException => RetryOnTimeout,
            TypeSafeConnectionException => RetryOnConnectionError,
            TypeSafeApiException api => HttpStatuses.Contains((int)api.Status),
            _ => false,
        }
        || (Predicate?.Invoke(error) ?? false);

    internal TimeSpan DelayFor(int attempt, TypeSafeException error)
    {
        if (RespectRetryAfter && error is TypeSafeApiException { RetryAfter: { } retryAfter })
        {
            return retryAfter;
        }

        var seconds = Math.Min(BackoffInitial.TotalSeconds * Math.Pow(2, attempt), BackoffMax.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * (1 - BackoffJitter * Random.Shared.NextDouble()));
    }
}
