namespace TypesafeSdk;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

public class TypeSafeException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>An unsuccessful HTTP response, or a successful one with an invalid body.</summary>
public class TypeSafeApiException : TypeSafeException
{
    private const int MaxBodyInMessage = 500;

    public TypeSafeApiException(
        HttpStatusCode status, string? body, HttpResponseHeaders headers, string endpoint, string? message = null)
        : base(message ?? Describe(status, body, endpoint))
    {
        Status = status;
        Body = string.IsNullOrEmpty(body) ? null : body;
        Headers = headers;
        Endpoint = endpoint;
        RequestId = headers.TryGetValues("x-typesafe-request-id", out var ids) ? ids.FirstOrDefault() : null;
        RetryAfter = ParseRetryAfter(headers);
    }

    public HttpStatusCode Status { get; }

    /// <summary>JSON error body or plain text; null when empty.</summary>
    public string? Body { get; }

    public HttpResponseHeaders Headers { get; }

    /// <summary>Method and URL without credentials, query or fragment.</summary>
    public string Endpoint { get; }

    public string? RequestId { get; }

    /// <summary>Server-requested wait from Retry-After or retry-after-ms, when present.</summary>
    public TimeSpan? RetryAfter { get; }

    internal static TypeSafeApiException Create(
        HttpStatusCode status, string? body, HttpResponseHeaders headers, string endpoint) =>
        (int)status switch
        {
            400 => new TypeSafeBadRequestException(status, body, headers, endpoint),
            401 => new TypeSafeAuthenticationException(status, body, headers, endpoint),
            403 => new TypeSafePermissionDeniedException(status, body, headers, endpoint),
            404 => new TypeSafeNotFoundException(status, body, headers, endpoint),
            422 => new TypeSafeUnprocessableEntityException(status, body, headers, endpoint),
            429 => new TypeSafeRateLimitException(status, body, headers, endpoint),
            >= 500 => new TypeSafeInternalServerException(status, body, headers, endpoint),
            _ => new TypeSafeApiException(status, body, headers, endpoint),
        };

    private static string Describe(HttpStatusCode status, string? body, string endpoint)
    {
        var text = $"{(int)status} {status} from {endpoint}";
        if (string.IsNullOrWhiteSpace(body))
        {
            return text;
        }

        return body.Length > MaxBodyInMessage ? $"{text}: {body[..MaxBodyInMessage]}..." : $"{text}: {body}";
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("retry-after-ms", out var values)
            && double.TryParse(values.First(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            && ms >= 0)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        if (headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return delta;
            }

            if (retryAfter.Date is { } date)
            {
                var wait = date - DateTimeOffset.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }

        return null;
    }
}

public sealed class TypeSafeBadRequestException(HttpStatusCode status, string? body, HttpResponseHeaders headers, string endpoint)
    : TypeSafeApiException(status, body, headers, endpoint);

public sealed class TypeSafeAuthenticationException(HttpStatusCode status, string? body, HttpResponseHeaders headers, string endpoint)
    : TypeSafeApiException(status, body, headers, endpoint);

public sealed class TypeSafePermissionDeniedException(HttpStatusCode status, string? body, HttpResponseHeaders headers, string endpoint)
    : TypeSafeApiException(status, body, headers, endpoint);

public sealed class TypeSafeNotFoundException(HttpStatusCode status, string? body, HttpResponseHeaders headers, string endpoint)
    : TypeSafeApiException(status, body, headers, endpoint);

public sealed class TypeSafeUnprocessableEntityException(HttpStatusCode status, string? body, HttpResponseHeaders headers, string endpoint)
    : TypeSafeApiException(status, body, headers, endpoint);

public sealed class TypeSafeRateLimitException(HttpStatusCode status, string? body, HttpResponseHeaders headers, string endpoint)
    : TypeSafeApiException(status, body, headers, endpoint);

/// <summary>Any 5xx response, including 529 Overloaded.</summary>
public sealed class TypeSafeInternalServerException(HttpStatusCode status, string? body, HttpResponseHeaders headers, string endpoint)
    : TypeSafeApiException(status, body, headers, endpoint);

/// <summary>A successful response whose body was missing or structurally invalid required data.</summary>
public sealed class TypeSafeResponseValidationException(
    HttpStatusCode status, string? body, HttpResponseHeaders headers, string endpoint, string fieldPath)
    : TypeSafeApiException(status, body, headers, endpoint, $"Invalid response from {endpoint}: bad or missing '{fieldPath}'.")
{
    /// <summary>Dotted path to the offending field, such as answers.tone.confidence.</summary>
    public string FieldPath { get; } = fieldPath;
}

/// <summary>A request failed without an HTTP response.</summary>
public class TypeSafeConnectionException(string message, Exception? innerException = null)
    : TypeSafeException(message, innerException);

public sealed class TypeSafeTimeoutException(TimeSpan timeout, Exception? innerException = null)
    : TypeSafeConnectionException($"Request timed out after {timeout.TotalSeconds:0.#}s.", innerException)
{
    public TimeSpan Timeout { get; } = timeout;
}
