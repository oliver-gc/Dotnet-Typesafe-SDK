using System.Collections.Concurrent;
using System.Net;

namespace TypesafeSdk.Tests;

public class RetryTests
{
    private static readonly (string, string) NoDelay = ("retry-after-ms", "0");

    private static TypeSafeApiException ApiError(int status, params (string, string)[] headers) =>
        TypeSafeApiException.Create(
            (HttpStatusCode)status, "{}", Headers.Response(headers), "POST https://api.typesafe.ai/v1/systemone");

    [Theory]
    [InlineData(429, 3)]
    [InlineData(500, 3)]
    [InlineData(502, 3)]
    [InlineData(503, 3)]
    [InlineData(504, 3)]
    [InlineData(529, 3)]
    [InlineData(400, 1)]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(404, 1)]
    [InlineData(408, 1)]
    [InlineData(409, 1)]
    [InlineData(422, 1)]
    [InlineData(302, 1)]
    public async Task DefaultPolicy_RetriesOnlyTransientStatuses(int status, int attempts)
    {
        var handler = new StubHandler(_ => Reply.Error(status, "{}", NoDelay));
        using var client = TestClient.Create(handler, retry: new RetryPolicy());

        var error = await Assert.ThrowsAnyAsync<TypeSafeApiException>(() => client.Call());

        Assert.Equal(status, (int)error.Status);
        Assert.Equal(attempts, handler.Requests.Count);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(4, 5)]
    public async Task MaxRetries_LimitsTotalAttempts(int maxRetries, int attempts)
    {
        var handler = new StubHandler(_ => Reply.Error(429, "{}", NoDelay));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry(maxRetries));

        await Assert.ThrowsAsync<TypeSafeRateLimitException>(() => client.Call());

        Assert.Equal(attempts, handler.Requests.Count);
    }

    [Theory]
    [InlineData(409, 3)]
    [InlineData(500, 1)]
    public async Task CustomStatuses_ReplaceTheDefaults(int status, int attempts)
    {
        var handler = new StubHandler(_ => Reply.Error(status, "{}", NoDelay));
        var policy = TestClient.FastRetry() with { HttpStatuses = new HashSet<int> { 409 } };
        using var client = TestClient.Create(handler, retry: policy);

        await Assert.ThrowsAnyAsync<TypeSafeApiException>(() => client.Call());

        Assert.Equal(attempts, handler.Requests.Count);
    }

    [Fact]
    public async Task Predicate_AddsRetriesToTheBuiltInRules()
    {
        var handler = new StubHandler(_ => Reply.Error(404, "{}", NoDelay));
        var policy = TestClient.FastRetry(1) with { Predicate = e => e is TypeSafeApiException { Status: HttpStatusCode.NotFound } };
        using var client = TestClient.Create(handler, retry: policy);

        await Assert.ThrowsAsync<TypeSafeNotFoundException>(() => client.Call());

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PerCallPolicy_OverridesTheClientPolicy()
    {
        var handler = new StubHandler(_ => Reply.Error(429, "{}", NoDelay));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry(2));

        await Assert.ThrowsAsync<TypeSafeRateLimitException>(() => client.Call(retry: new RetryPolicy { MaxRetries = 0 }));
        Assert.Single(handler.Requests);

        await Assert.ThrowsAsync<TypeSafeRateLimitException>(() => client.Call());
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task PerCallPolicy_CanUseDifferentStatuses()
    {
        var handler = new StubHandler(_ => Reply.Error(409, "{}", NoDelay));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry(0));

        await Assert.ThrowsAsync<TypeSafeApiException>(
            () => client.Call(retry: TestClient.FastRetry(2) with { HttpStatuses = new HashSet<int> { 409 } }));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_ResendTheSameBodyAndCredentials()
    {
        var handler = new StubHandler(_ => Reply.Error(503, "{}", NoDelay));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry());

        await Assert.ThrowsAsync<TypeSafeInternalServerException>(() => client.Call(state: new { Id = 7 }, model: "m"));

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(handler.Requests[0].Body, r.Body));
        Assert.All(handler.Requests, r => Assert.Equal("test-key", r.Headers.Authorization?.Parameter));
    }

    [Fact]
    public async Task Retry_RecoversAfterTransientFailures()
    {
        var calls = 0;
        var handler = new StubHandler(_ => Interlocked.Increment(ref calls) < 3 ? Reply.Error(503, "{}", NoDelay) : Reply.Ok());
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry());

        var result = await client.Call();

        Assert.Equal("jev-latest", result.Model);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ExhaustedRetries_ThrowTheFinalHttpError()
    {
        var statuses = new[] { 429, 500, 503 };
        var attempt = 0;
        var handler = new StubHandler(_ =>
        {
            var n = Interlocked.Increment(ref attempt);
            return Reply.Error(statuses[n - 1], $$"""{"message":"attempt {{n}}"}""", ("x-typesafe-request-id", $"request-{n}"), NoDelay);
        });
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry());

        var error = await Assert.ThrowsAsync<TypeSafeInternalServerException>(() => client.Call());

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.Status);
        Assert.Equal("request-3", error.RequestId);
        Assert.Contains("attempt 3", error.Body);
    }

    [Fact]
    public async Task ConnectionErrors_AreRetriedAndRecover()
    {
        var attempts = 0;
        var handler = new StubHandler(_ => Interlocked.Increment(ref attempts) < 3
            ? throw new HttpRequestException("failed")
            : Reply.Ok());
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry());

        await client.Call();

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExhaustedConnectionRetries_ThrowTheLastError()
    {
        var attempts = 0;
        var handler = new StubHandler(_ => throw new HttpRequestException($"attempt {Interlocked.Increment(ref attempts)}"));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry());

        var error = await Assert.ThrowsAsync<TypeSafeConnectionException>(() => client.Call());

        Assert.Equal(3, attempts);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal("attempt 3", error.InnerException!.Message);
    }

    [Fact]
    public async Task ConnectionErrors_AreNotRetriedWhenDisabled()
    {
        var attempts = 0;
        var handler = new StubHandler(_ => throw new HttpRequestException($"attempt {Interlocked.Increment(ref attempts)}"));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry() with { RetryOnConnectionError = false });

        await Assert.ThrowsAsync<TypeSafeConnectionException>(() => client.Call());

        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 1)]
    public async Task Timeouts_AreRetriedOnlyWhenEnabled(bool retryOnTimeout, int attempts)
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply.Ok();
        });
        var policy = TestClient.FastRetry() with { RetryOnTimeout = retryOnTimeout };
        using var client = TestClient.Create(handler, retry: policy, timeout: TimeSpan.FromMilliseconds(30));

        var error = await Assert.ThrowsAsync<TypeSafeTimeoutException>(() => client.Call());

        Assert.Equal(attempts, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(30), error.Timeout);
    }

    [Fact]
    public async Task TimeoutBudget_StopsRetriesWhenTheNextDelayWouldExceedIt()
    {
        var handler = new StubHandler(_ => Reply.Error(429, "{}", ("Retry-After", "5")));
        var policy = TestClient.FastRetry() with { Timeout = TimeSpan.FromMilliseconds(500), RespectRetryAfter = true };
        using var client = TestClient.Create(handler, retry: policy);

        await Assert.ThrowsAsync<TypeSafeRateLimitException>(() => client.Call());

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NullTimeoutBudget_DisablesTheLimit()
    {
        var handler = new StubHandler(_ => Reply.Error(429, "{}", NoDelay));
        var policy = TestClient.FastRetry() with { Timeout = null };
        using var client = TestClient.Create(handler, retry: policy);

        await Assert.ThrowsAsync<TypeSafeRateLimitException>(() => client.Call());

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task TimeoutBudget_AppliesFreshlyToEachCall()
    {
        var handler = new StubHandler(_ => Reply.Error(429, "{}", ("retry-after-ms", "1")));
        var policy = TestClient.FastRetry() with { Timeout = TimeSpan.FromSeconds(30) };
        using var client = TestClient.Create(handler, retry: policy);

        for (var call = 1; call <= 2; call++)
        {
            await Assert.ThrowsAsync<TypeSafeRateLimitException>(() => client.Call());
            Assert.Equal(3 * call, handler.Requests.Count);
        }
    }

    [Fact]
    public async Task CancellingDuringBackoff_StopsRetryingWithoutWrapping()
    {
        var handler = new StubHandler(_ => Reply.Error(429, "{}", ("Retry-After", "60")));
        using var client = TestClient.Create(handler, retry: new RetryPolicy { Timeout = null });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Call(ct: cts.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConcurrentCalls_RetryIndependently()
    {
        var seen = new ConcurrentDictionary<string, int>();
        var handler = new StubHandler(async (request, _) =>
        {
            await Task.Yield();
            var key = (string)request.Json["state"]!;
            return seen.AddOrUpdate(key, 1, (_, n) => n + 1) == 1 ? Reply.Error(429, "{}", NoDelay) : Reply.Ok();
        });
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry());

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => client.Call(state: $"call-{i}")));

        Assert.Equal(4, seen.Count);
        Assert.All(seen.Values, count => Assert.Equal(2, count));
    }

    [Fact]
    public async Task ServerRequestedDelay_IsWaitedBeforeTheRetry()
    {
        var times = new List<DateTimeOffset>();
        var handler = new StubHandler(_ =>
        {
            times.Add(DateTimeOffset.UtcNow);
            return times.Count == 1 ? Reply.Error(429, "{}", ("retry-after-ms", "200")) : Reply.Ok();
        });
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry());

        await client.Call();

        Assert.Equal(2, times.Count);
        Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(180), $"Waited only {times[1] - times[0]}");
    }

    [Fact]
    public void Defaults_MatchDocumentedValues()
    {
        var policy = new RetryPolicy();

        Assert.Equal(2, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(0.5), policy.BackoffInitial);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.BackoffMax);
        Assert.Equal(0.25, policy.BackoffJitter);
        Assert.Equal(new[] { 429, 500, 502, 503, 504, 529 }, policy.HttpStatuses.Order());
        Assert.True(policy.RespectRetryAfter);
        Assert.True(policy.RetryOnConnectionError);
        Assert.True(policy.RetryOnTimeout);
        Assert.Null(policy.Predicate);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.Timeout);
    }

    [Fact]
    public void ShouldRetry_FollowsTheErrorKindAndPolicyFlags()
    {
        var policy = new RetryPolicy();
        var noConnection = new RetryPolicy { RetryOnConnectionError = false, RetryOnTimeout = false };
        var connection = new TypeSafeConnectionException("x");
        var timeout = new TypeSafeTimeoutException(TimeSpan.FromSeconds(1));

        Assert.True(policy.ShouldRetry(connection));
        Assert.True(policy.ShouldRetry(timeout));
        Assert.False(noConnection.ShouldRetry(connection));
        Assert.False(noConnection.ShouldRetry(timeout));
        Assert.False(policy.ShouldRetry(new TypeSafeException("plain")));
        Assert.False(policy.ShouldRetry(new TypeSafeResponseValidationException(
            HttpStatusCode.OK, "{}", Headers.Response(), "POST x", "model")));
        Assert.True(new RetryPolicy { Predicate = _ => true }.ShouldRetry(new TypeSafeException("plain")));
    }

    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(1, 1.0)]
    [InlineData(2, 2.0)]
    [InlineData(3, 4.0)]
    [InlineData(4, 5.0)]
    [InlineData(20, 5.0)]
    [InlineData(2000, 5.0)]
    public void Backoff_DoublesUpToTheMaximum(int attempt, double expectedSeconds)
    {
        var policy = new RetryPolicy { BackoffJitter = 0 };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.DelayFor(attempt, new TypeSafeConnectionException("x")));
    }

    [Fact]
    public void Backoff_JitterOnlyEverShortensTheDelay()
    {
        var policy = new RetryPolicy();
        var error = new TypeSafeConnectionException("x");

        for (var i = 0; i < 200; i++)
        {
            Assert.InRange(policy.DelayFor(0, error), TimeSpan.FromSeconds(0.375), TimeSpan.FromSeconds(0.5));
        }
    }

    [Theory]
    [InlineData(0.0, 5.0, 0.0)]
    [InlineData(0.5, 0.0, 0.0)]
    [InlineData(0.5, 0.0006, 0.0006)]
    public void Backoff_HandlesZeroAndTinyBounds(double initial, double max, double expectedSeconds)
    {
        var policy = new RetryPolicy
        {
            BackoffInitial = TimeSpan.FromSeconds(initial),
            BackoffMax = TimeSpan.FromSeconds(max),
            BackoffJitter = 0,
        };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.DelayFor(0, new TypeSafeConnectionException("x")));
    }

    [Theory]
    [InlineData("Retry-After", "2", 2000.0)]
    [InlineData("retry-after-ms", "125", 125.0)]
    [InlineData("Retry-After", "60", 60000.0)]
    [InlineData("retry-after-ms", "60001", 60001.0)]
    public void ServerDelay_IsHonoredHoweverLong(string header, string value, double expectedMs)
    {
        var delay = new RetryPolicy().DelayFor(0, ApiError(429, (header, value)));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), delay);
    }

    [Fact]
    public void ServerDelay_IsIgnoredWhenDisabledOrUnparseable()
    {
        var ignoring = new RetryPolicy { RespectRetryAfter = false, BackoffJitter = 0 };
        var respecting = new RetryPolicy { BackoffJitter = 0 };

        Assert.Equal(TimeSpan.FromSeconds(0.5), ignoring.DelayFor(0, ApiError(429, ("Retry-After", "5"))));
        Assert.Equal(TimeSpan.FromSeconds(0.5), respecting.DelayFor(0, ApiError(429, ("Retry-After", "bad"))));
        Assert.Equal(TimeSpan.FromSeconds(0.5), respecting.DelayFor(0, ApiError(429)));
    }

    [Fact]
    public void ServerDelay_AppliesToAnyApiException()
    {
        var policy = new RetryPolicy { BackoffJitter = 0 };

        Assert.Equal(TimeSpan.FromSeconds(7), policy.DelayFor(0, ApiError(503, ("Retry-After", "7"))));
    }
}
