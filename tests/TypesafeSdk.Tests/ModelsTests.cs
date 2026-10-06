using System.Net;
using TypesafeSdk.Models;

namespace TypesafeSdk.Tests;

public class ModelsTests
{
    [Fact]
    public async Task List_SendsAuthenticatedGetWithoutBody()
    {
        var handler = new StubHandler(_ => Reply.Ok(Sample.ModelList));
        using var client = TestClient.Create(handler);

        await client.ListModelsAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.typesafe.ai/v1/models", request.Uri.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
        Assert.Null(request.ContentType);
        Assert.Equal("", request.Body);
    }

    [Fact]
    public async Task List_ParsesEveryModelInOrder()
    {
        var handler = new StubHandler(_ => Reply.Ok(Sample.ModelList, ("x-typesafe-request-id", "req-9")));
        using var client = TestClient.Create(handler);

        var result = await client.ListModelsAsync();

        Assert.Equal(
            [
                new ModelInfo("jev-latest", "The latest Jev release.", "2026-05-01"),
                new ModelInfo("jev-fast", "Lower latency.", "2026-03-12"),
            ],
            result.Models);
        Assert.Equal("req-9", result.RequestId);
        Assert.Equal(Sample.ModelList, result.RawBody);
    }

    [Fact]
    public async Task List_AllowsAnEmptyList()
    {
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok("""{"models":[]}""")));

        var result = await client.ListModelsAsync();

        Assert.Empty(result.Models);
        Assert.Null(result.RequestId);
    }

    [Fact]
    public async Task List_IgnoresUnknownFields()
    {
        var body = """{"extra":1,"models":[{"name":"a","description":"d","release_date":"r","extra":true}]}""";
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok(body)));

        var result = await client.ListModelsAsync();

        Assert.Equal(new ModelInfo("a", "d", "r"), Assert.Single(result.Models));
    }

    [Fact]
    public async Task List_UsesTheConfiguredBaseUrl()
    {
        var handler = new StubHandler(_ => Reply.Ok(Sample.ModelList));
        using var client = TestClient.Create(handler, baseUrl: "https://example.test/prefix/");

        await client.ListModelsAsync();

        Assert.Equal("https://example.test/prefix/v1/models", handler.Requests[0].Uri.ToString());
    }

    [Theory]
    [InlineData("""{}""", "models")]
    [InlineData("""{"models":{}}""", "models")]
    [InlineData("""{"models":"x"}""", "models")]
    [InlineData("""{"models":["x"]}""", "models[0]")]
    [InlineData("""{"models":[{"description":"d","release_date":"r"}]}""", "models[0].name")]
    [InlineData("""{"models":[{"name":1,"description":"d","release_date":"r"}]}""", "models[0].name")]
    [InlineData("""{"models":[{"name":"a","release_date":"r"}]}""", "models[0].description")]
    [InlineData("""{"models":[{"name":"a","description":"d"}]}""", "models[0].release_date")]
    [InlineData("""{"models":[{"name":"a","description":"d","release_date":"r"},{"name":"b","description":null,"release_date":"r"}]}""", "models[1].description")]
    [InlineData("[]", "$")]
    [InlineData("not json", "$")]
    [InlineData("", "$")]
    public void Parse_ReportsTheFieldPathOfInvalidData(string body, string path)
    {
        var error = Assert.Throws<InvalidFieldException>(() => ResponseParser.ParseModels(body, null));

        Assert.Equal(path, error.FieldPath);
    }

    [Fact]
    public async Task InvalidBody_BecomesResponseValidationException()
    {
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok("""{"models":[{"name":"a"}]}""")));

        var error = await Assert.ThrowsAsync<TypeSafeResponseValidationException>(() => client.ListModelsAsync());

        Assert.Equal("models[0].description", error.FieldPath);
        Assert.Equal("GET https://api.typesafe.ai/v1/models", error.Endpoint);
    }

    [Theory]
    [InlineData(401, typeof(TypeSafeAuthenticationException))]
    [InlineData(403, typeof(TypeSafePermissionDeniedException))]
    [InlineData(404, typeof(TypeSafeNotFoundException))]
    [InlineData(429, typeof(TypeSafeRateLimitException))]
    [InlineData(500, typeof(TypeSafeInternalServerException))]
    public async Task ErrorStatus_MapsToTypedExceptionNamingTheModelsEndpoint(int status, Type expected)
    {
        using var client = TestClient.Create(new StubHandler(_ => Reply.Error(status)));

        var error = await Assert.ThrowsAsync(expected, () => client.ListModelsAsync());

        var api = Assert.IsAssignableFrom<TypeSafeApiException>(error);
        Assert.Equal((HttpStatusCode)status, api.Status);
        Assert.Equal("GET https://api.typesafe.ai/v1/models", api.Endpoint);
    }

    [Fact]
    public async Task List_RetriesTransientFailuresUsingTheClientPolicy()
    {
        var calls = 0;
        var handler = new StubHandler(_ => ++calls == 1 ? Reply.Error(503) : Reply.Ok(Sample.ModelList));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry());

        var result = await client.ListModelsAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, result.Models.Count);
    }

    [Fact]
    public async Task List_PerCallRetryOverridesTheClientPolicy()
    {
        var handler = new StubHandler(_ => Reply.Error(503));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry(maxRetries: 0));

        await Assert.ThrowsAsync<TypeSafeInternalServerException>(
            () => client.ListModelsAsync(TestClient.FastRetry(maxRetries: 2)));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task List_DoesNotRetryClientErrors()
    {
        var handler = new StubHandler(_ => Reply.Error(401));
        using var client = TestClient.Create(handler, retry: TestClient.FastRetry());

        await Assert.ThrowsAsync<TypeSafeAuthenticationException>(() => client.ListModelsAsync());

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AttemptTimeout_BecomesTimeoutException()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply.Ok(Sample.ModelList);
        });
        using var client = TestClient.Create(handler, timeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TypeSafeTimeoutException>(() => client.ListModelsAsync());
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutWrapping()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply.Ok(Sample.ModelList);
        });
        using var client = TestClient.Create(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListModelsAsync(ct: cts.Token));
    }

    [Fact]
    public async Task Logging_SummarisesTheRequestAndRedactsCredentials()
    {
        var log = new LogCapture();
        var handler = new StubHandler(_ => Reply.Ok(Sample.ModelList));
        using var client = TestClient.Create(handler, logLevel: TypeSafeLogLevel.Debug, logger: log.Write);

        await client.ListModelsAsync();

        Assert.Contains("GET https://api.typesafe.ai/v1/models -> 200", log.Text);
        Assert.Contains("Authorization: [redacted]", log.Text);
        Assert.DoesNotContain("test-key", log.Text);
    }
}