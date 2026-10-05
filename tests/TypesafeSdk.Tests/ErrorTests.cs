using System.Net;

namespace TypesafeSdk.Tests;

public class ErrorTests
{
    private const string Endpoint = "POST https://api.typesafe.ai/v1/systemone";

    [Theory]
    [InlineData(400, typeof(TypeSafeBadRequestException))]
    [InlineData(401, typeof(TypeSafeAuthenticationException))]
    [InlineData(403, typeof(TypeSafePermissionDeniedException))]
    [InlineData(404, typeof(TypeSafeNotFoundException))]
    [InlineData(422, typeof(TypeSafeUnprocessableEntityException))]
    [InlineData(429, typeof(TypeSafeRateLimitException))]
    [InlineData(500, typeof(TypeSafeInternalServerException))]
    [InlineData(503, typeof(TypeSafeInternalServerException))]
    [InlineData(529, typeof(TypeSafeInternalServerException))]
    [InlineData(408, typeof(TypeSafeApiException))]
    [InlineData(409, typeof(TypeSafeApiException))]
    [InlineData(302, typeof(TypeSafeApiException))]
    public async Task StatusCodes_MapToExceptionTypes(int status, Type expected)
    {
        const string body = """{"detail":{"message":"Server explanation"}}""";
        using var client = TestClient.Create(new StubHandler(_ => Reply.Error(
            status, body, ("x-typesafe-request-id", "req_123"), ("retry-after-ms", "125"))));

        var error = await Assert.ThrowsAnyAsync<TypeSafeApiException>(() => client.Call());

        Assert.Equal(expected, error.GetType());
        Assert.Equal(status, (int)error.Status);
        Assert.Equal(body, error.Body);
        Assert.Equal("req_123", error.RequestId);
        Assert.Equal(Endpoint, error.Endpoint);
        Assert.Equal(TimeSpan.FromMilliseconds(125), error.RetryAfter);
        Assert.Equal("125", error.Headers.GetValues("retry-after-ms").Single());
        Assert.Contains($"{status} ", error.Message);
        Assert.Contains(body, error.Message);
        Assert.Contains(Endpoint, error.Message);
    }

    [Fact]
    public void ExceptionHierarchy_IsCatchableAtEachLevel()
    {
        var headers = Headers.Response();
        TypeSafeException[] errors =
        [
            new TypeSafeBadRequestException(HttpStatusCode.BadRequest, null, headers, Endpoint),
            new TypeSafeAuthenticationException(HttpStatusCode.Unauthorized, null, headers, Endpoint),
            new TypeSafePermissionDeniedException(HttpStatusCode.Forbidden, null, headers, Endpoint),
            new TypeSafeNotFoundException(HttpStatusCode.NotFound, null, headers, Endpoint),
            new TypeSafeUnprocessableEntityException(HttpStatusCode.UnprocessableEntity, null, headers, Endpoint),
            new TypeSafeRateLimitException(HttpStatusCode.TooManyRequests, null, headers, Endpoint),
            new TypeSafeInternalServerException(HttpStatusCode.InternalServerError, null, headers, Endpoint),
            new TypeSafeResponseValidationException(HttpStatusCode.OK, null, headers, Endpoint, "model"),
        ];

        Assert.All(errors, error => Assert.IsAssignableFrom<TypeSafeApiException>(error));

        var timeout = new TypeSafeTimeoutException(TimeSpan.FromSeconds(1));
        Assert.IsAssignableFrom<TypeSafeConnectionException>(timeout);
        Assert.IsAssignableFrom<TypeSafeException>(timeout);
        Assert.IsNotAssignableFrom<TypeSafeApiException>(new TypeSafeConnectionException("x"));
    }

    [Theory]
    [InlineData("", "400 BadRequest from POST https://api.typesafe.ai/v1/systemone")]
    [InlineData("   ", "400 BadRequest from POST https://api.typesafe.ai/v1/systemone")]
    [InlineData("plain text", "400 BadRequest from POST https://api.typesafe.ai/v1/systemone: plain text")]
    [InlineData("""{"error":"bad"}""", """400 BadRequest from POST https://api.typesafe.ai/v1/systemone: {"error":"bad"}""")]
    public async Task Message_DescribesStatusEndpointAndBody(string body, string expected)
    {
        using var client = TestClient.Create(new StubHandler(_ => Reply.Error(400, body)));

        var error = await Assert.ThrowsAsync<TypeSafeBadRequestException>(() => client.Call());

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public async Task Message_TruncatesLongBodiesButBodyKeepsEverything()
    {
        var body = new string('x', 2000);
        using var client = TestClient.Create(new StubHandler(_ => Reply.Error(400, body)));

        var error = await Assert.ThrowsAsync<TypeSafeBadRequestException>(() => client.Call());

        Assert.Equal(body, error.Body);
        Assert.EndsWith($": {new string('x', 500)}...", error.Message);
        Assert.DoesNotContain(new string('x', 501), error.Message);
    }

    [Fact]
    public async Task EmptyBody_IsExposedAsNull()
    {
        using var client = TestClient.Create(new StubHandler(_ => Reply.Error(400, "")));

        var error = await Assert.ThrowsAsync<TypeSafeBadRequestException>(() => client.Call());

        Assert.Null(error.Body);
        Assert.Null(error.RequestId);
        Assert.Null(error.RetryAfter);
    }

    [Fact]
    public async Task Endpoint_IncludesBaseUrlPathPrefixAndNoTrailingSlashes()
    {
        using var client = TestClient.Create(
            new StubHandler(_ => Reply.Error(429)), baseUrl: "https://api.example.test/prefix///", apiKey: "private-api-key");

        var error = await Assert.ThrowsAsync<TypeSafeRateLimitException>(() => client.Call());

        Assert.Equal("POST https://api.example.test/prefix/v1/systemone", error.Endpoint);
        Assert.DoesNotContain("private-api-key", error.Message);
        Assert.DoesNotContain("private-api-key", error.ToString());
    }

    [Theory]
    [InlineData("retry-after-ms", "125", 125.0)]
    [InlineData("retry-after-ms", "1.5", 1.5)]
    [InlineData("retry-after-ms", "0", 0.0)]
    [InlineData("Retry-After", "2", 2000.0)]
    [InlineData("Retry-After", "60", 60000.0)]
    public void RetryAfter_ParsesSupportedHeaders(string header, string value, double expectedMs)
    {
        var error = new TypeSafeRateLimitException(HttpStatusCode.TooManyRequests, null, Headers.Response((header, value)), Endpoint);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), error.RetryAfter);
    }

    [Theory]
    [InlineData("retry-after-ms", "bad", "Retry-After", "2", 2000.0)]
    [InlineData("retry-after-ms", "-1", "Retry-After", "2", 2000.0)]
    [InlineData("retry-after-ms", "0", "Retry-After", "50", 0.0)]
    [InlineData("retry-after-ms", "125", "Retry-After", "50", 125.0)]
    public void RetryAfter_MillisecondHeaderTakesPrecedenceWhenValid(
        string header1, string value1, string header2, string value2, double expectedMs)
    {
        var headers = Headers.Response((header1, value1), (header2, value2));
        var error = new TypeSafeRateLimitException(HttpStatusCode.TooManyRequests, null, headers, Endpoint);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), error.RetryAfter);
    }

    [Theory]
    [InlineData("retry-after-ms", "bad")]
    [InlineData("retry-after-ms", "-5")]
    [InlineData("Retry-After", "bad")]
    public void RetryAfter_IgnoresUnparseableValues(string header, string value)
    {
        var error = new TypeSafeRateLimitException(HttpStatusCode.TooManyRequests, null, Headers.Response((header, value)), Endpoint);

        Assert.Null(error.RetryAfter);
    }

    [Fact]
    public void RetryAfter_HttpDateInTheFuture_IsTheRemainingWait()
    {
        var date = DateTimeOffset.UtcNow.AddSeconds(30).ToString("R");
        var error = new TypeSafeRateLimitException(
            HttpStatusCode.TooManyRequests, null, Headers.Response(("Retry-After", date)), Endpoint);

        Assert.InRange(error.RetryAfter!.Value, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void RetryAfter_HttpDateInThePast_IsZero()
    {
        var date = DateTimeOffset.UtcNow.AddSeconds(-30).ToString("R");
        var error = new TypeSafeRateLimitException(
            HttpStatusCode.TooManyRequests, null, Headers.Response(("Retry-After", date)), Endpoint);

        Assert.Equal(TimeSpan.Zero, error.RetryAfter);
    }

    [Fact]
    public void ApiException_ExplicitMessageOverridesDefault()
    {
        var error = new TypeSafeApiException(HttpStatusCode.Conflict, "{}", Headers.Response(), Endpoint, message: "A custom explanation");

        Assert.Equal("A custom explanation", error.Message);
        Assert.Equal(HttpStatusCode.Conflict, error.Status);
    }

    [Fact]
    public void ValidationException_ExposesFieldPathAndMessage()
    {
        var error = new TypeSafeResponseValidationException(
            HttpStatusCode.OK, "{}", Headers.Response(("x-typesafe-request-id", "req-1")), Endpoint, "answers.tone.confidence");

        Assert.Equal("answers.tone.confidence", error.FieldPath);
        Assert.Equal("req-1", error.RequestId);
        Assert.Equal($"Invalid response from {Endpoint}: bad or missing 'answers.tone.confidence'.", error.Message);
    }

    [Fact]
    public void TimeoutException_ReportsTimeoutAndInnerException()
    {
        var inner = new OperationCanceledException();
        var error = new TypeSafeTimeoutException(TimeSpan.FromSeconds(1.5), inner);

        Assert.Equal(TimeSpan.FromSeconds(1.5), error.Timeout);
        Assert.Same(inner, error.InnerException);
        Assert.Equal("Request timed out after 1.5s.", error.Message);
    }

    [Fact]
    public async Task HttpRequestException_BecomesConnectionExceptionWithInnerException()
    {
        var failure = new HttpRequestException("connection refused");
        using var client = TestClient.Create(new StubHandler(_ => throw failure));

        var error = await Assert.ThrowsAsync<TypeSafeConnectionException>(() => client.Call());

        Assert.Same(failure, error.InnerException);
        Assert.Equal($"Could not reach {Endpoint}: connection refused", error.Message);
    }

    [Fact]
    public async Task AttemptTimeout_BecomesTimeoutException()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply.Ok();
        });
        using var client = TestClient.Create(handler, timeout: TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<TypeSafeTimeoutException>(() => client.Call());

        Assert.Equal(TimeSpan.FromMilliseconds(50), error.Timeout);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }
}
