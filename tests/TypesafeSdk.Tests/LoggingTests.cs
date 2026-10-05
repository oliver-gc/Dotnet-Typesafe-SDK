namespace TypesafeSdk.Tests;

public class LoggingTests
{
    [Theory]
    [InlineData(TypeSafeLogLevel.Debug, new[] { TypeSafeLogLevel.Info, TypeSafeLogLevel.Debug })]
    [InlineData(TypeSafeLogLevel.Info, new[] { TypeSafeLogLevel.Info })]
    [InlineData(TypeSafeLogLevel.Warning, new TypeSafeLogLevel[0])]
    [InlineData(TypeSafeLogLevel.Error, new TypeSafeLogLevel[0])]
    [InlineData(TypeSafeLogLevel.Off, new TypeSafeLogLevel[0])]
    public async Task LogLevel_ControlsWhichMessagesAreEmitted(TypeSafeLogLevel level, TypeSafeLogLevel[] expected)
    {
        var logs = new LogCapture();
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok()), logLevel: level, logger: logs.Write);

        await client.Call();

        Assert.Equal(expected.Order(), logs.Entries.Select(e => e.Level).Distinct().Order());
        if (expected.Contains(TypeSafeLogLevel.Info))
        {
            var summary = Assert.Single(logs.Entries, e => e.Level == TypeSafeLogLevel.Info);
            Assert.Contains("POST https://api.typesafe.ai/v1/systemone -> 200", summary.Message);
        }

        if (!expected.Contains(TypeSafeLogLevel.Debug))
        {
            Assert.DoesNotContain("Authorization", logs.Text);
        }
    }

    [Fact]
    public async Task LoggingIsOffByDefault()
    {
        var logs = new LogCapture();
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok()), logger: logs.Write);

        await client.Call();

        Assert.Empty(logs.Entries);
    }

    [Theory]
    [InlineData("debug", true)]
    [InlineData("DEBUG", true)]
    [InlineData("info", true)]
    [InlineData("off", false)]
    [InlineData("bogus", false)]
    public async Task EnvironmentLogLevel_IsParsedCaseInsensitivelyAndBogusValuesDisableLogging(string value, bool logs)
    {
        using var env = new EnvScope().Set("TYPESAFE_LOG_LEVEL", value);
        var capture = new LogCapture();
        using var client = TestClient.Create(new StubHandler(_ => Reply.Ok()), logger: capture.Write);

        await client.Call();

        Assert.Equal(logs, capture.Entries.Count > 0);
    }

    [Fact]
    public async Task OptionLogLevel_BeatsTheEnvironment()
    {
        using var env = new EnvScope().Set("TYPESAFE_LOG_LEVEL", "debug");
        var capture = new LogCapture();
        using var client = TestClient.Create(
            new StubHandler(_ => Reply.Ok()), logLevel: TypeSafeLogLevel.Off, logger: capture.Write);

        await client.Call();

        Assert.Empty(capture.Entries);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task SecretHeaders_AreRedactedInDebugLogs(int status)
    {
        string[] secretNames =
        [
            "Authorization", "Proxy-Authorization", "X-API-Key", "API-Key", "X-ApiKey", "Cookie", "Set-Cookie",
            "X-Access-Token", "X-Client-Secret", "x-MiXeD-ToKeN",
        ];
        var logs = new LogCapture();
        var handler = new StubHandler(_ =>
        {
            var headers = secretNames.Select(name => (name, "response-credential")).Append(("x-visible", "response-visible")).ToArray();
            return status == 200 ? Reply.Ok(Sample.Result, headers) : Reply.Error(status, """{"message":"failure"}""", headers);
        });
        using var client = TestClient.Create(handler, apiKey: "auth-credential", logLevel: TypeSafeLogLevel.Debug, logger: logs.Write);

        try
        {
            await client.Call();
        }
        catch (TypeSafeApiException)
        {
        }

        Assert.Contains("response-visible", logs.Text);
        Assert.Contains("[redacted]", logs.Text);
        Assert.DoesNotContain("auth-credential", logs.Text);
        Assert.DoesNotContain("response-credential", logs.Text);
        Assert.Contains("Authorization: [redacted]", logs.Text);
    }

    [Fact]
    public async Task Debug_LogsRequestBodyAndRequestId()
    {
        var logs = new LogCapture();
        var handler = new StubHandler(_ => Reply.Ok(Sample.Result, ("x-typesafe-request-id", "req_log")));
        using var client = TestClient.Create(handler, logLevel: TypeSafeLogLevel.Debug, logger: logs.Write);

        await client.Call(state: "hello");

        Assert.Contains("hello", logs.Text);
        Assert.Contains("req_log", logs.Text);
        Assert.Contains("\"model\":\"jev-latest\"", logs.Text);
    }

    [Fact]
    public async Task ErrorStatus_IsLoggedAsAWarningWithTheRequestId()
    {
        var logs = new LogCapture();
        var handler = new StubHandler(_ => Reply.Error(400, "{}", ("x-typesafe-request-id", "req_bad")));
        using var client = TestClient.Create(handler, logLevel: TypeSafeLogLevel.Warning, logger: logs.Write);

        await Assert.ThrowsAsync<TypeSafeBadRequestException>(() => client.Call());

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(TypeSafeLogLevel.Warning, entry.Level);
        Assert.Contains("-> 400", entry.Message);
        Assert.Contains("request_id=req_bad", entry.Message);
    }

    [Fact]
    public async Task Retries_AreLoggedAsWarnings()
    {
        var logs = new LogCapture();
        var handler = new StubHandler(_ => Reply.Error(429, "{}", ("retry-after-ms", "0")));
        using var client = TestClient.Create(
            handler, retry: TestClient.FastRetry(), logLevel: TypeSafeLogLevel.Warning, logger: logs.Write);

        await Assert.ThrowsAsync<TypeSafeRateLimitException>(() => client.Call());

        Assert.Contains("Attempt 1 of 3 failed (TypeSafeRateLimitException)", logs.Text);
        Assert.Contains("Attempt 2 of 3 failed (TypeSafeRateLimitException)", logs.Text);
        Assert.DoesNotContain("Attempt 3 of 3 failed", logs.Text);
    }

    [Fact]
    public async Task Timeout_IsLoggedAsAWarning()
    {
        var logs = new LogCapture();
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply.Ok();
        });
        using var client = TestClient.Create(
            handler, timeout: TimeSpan.FromMilliseconds(30), logLevel: TypeSafeLogLevel.Warning, logger: logs.Write);

        await Assert.ThrowsAsync<TypeSafeTimeoutException>(() => client.Call());

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(TypeSafeLogLevel.Warning, entry.Level);
        Assert.Contains("timed out", entry.Message);
    }

    [Fact]
    public async Task ConnectionFailure_IsLoggedWithoutCredentials()
    {
        var logs = new LogCapture();
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = TestClient.Create(handler, apiKey: "ts_live_private", logLevel: TypeSafeLogLevel.Debug, logger: logs.Write);

        await Assert.ThrowsAsync<TypeSafeConnectionException>(() => client.Call());

        Assert.Contains("connection refused", logs.Text);
        Assert.DoesNotContain("ts_live_private", logs.Text);
    }

    [Fact]
    public async Task InvalidResponse_IsLoggedAsAnError()
    {
        var logs = new LogCapture();
        var handler = new StubHandler(_ => Reply.Ok("""{"usage":{},"answers":{}}"""));
        using var client = TestClient.Create(handler, logLevel: TypeSafeLogLevel.Error, logger: logs.Write);

        await Assert.ThrowsAsync<TypeSafeResponseValidationException>(() => client.Call());

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(TypeSafeLogLevel.Error, entry.Level);
        Assert.Contains("'model'", entry.Message);
    }

    [Fact]
    public async Task UnknownAnswerKind_IsLoggedAsAWarning()
    {
        var logs = new LogCapture();
        var handler = new StubHandler(_ => Reply.Ok("""
            {"model":"m","usage":{},"answers":{"mystery":{"type":"aurora","value":3}}}
            """));
        using var client = TestClient.Create(handler, logLevel: TypeSafeLogLevel.Warning, logger: logs.Write);

        var result = await client.Call();

        Assert.Empty(result.Answers);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(TypeSafeLogLevel.Warning, entry.Level);
        Assert.Contains("answers.mystery (type 'aurora')", entry.Message);
    }

    [Fact]
    public async Task DefaultLogger_WritesToStandardError()
    {
        var original = Console.Error;
        using var writer = new StringWriter();
        Console.SetError(writer);
        try
        {
            using var client = TestClient.Create(new StubHandler(_ => Reply.Ok()), logLevel: TypeSafeLogLevel.Info);

            await client.Call();
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("[typesafe] Info: POST https://api.typesafe.ai/v1/systemone -> 200", writer.ToString());
    }
}
