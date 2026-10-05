namespace TypesafeSdk.Tests;

public class ConfigTests
{
    private static StubHandler Ok() => new(_ => Reply.Ok());

    [Fact]
    public void Defaults_AreDocumented()
    {
        Assert.Equal("https://api.typesafe.ai", TypeSafeClient.DefaultBaseUrl);
        Assert.Equal("jev-latest", TypeSafeClient.DefaultModel);
        Assert.Equal(TimeSpan.FromSeconds(10), TypeSafeClient.DefaultTimeout);
        Assert.Equal("TYPESAFE_API_KEY", TypeSafeClient.ApiKeyEnv);
        Assert.Equal("TYPESAFE_BASE_URL", TypeSafeClient.BaseUrlEnv);
        Assert.Equal("TYPESAFE_DEFAULT_MODEL", TypeSafeClient.DefaultModelEnv);
        Assert.Equal("TYPESAFE_LOG_LEVEL", TypeSafeClient.LogLevelEnv);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("env")]
    [InlineData("constructor")]
    public async Task Resolution_PrefersConstructorThenEnvironmentThenDefaults(string source)
    {
        using var env = new EnvScope();
        if (source != "default")
        {
            env.Set("TYPESAFE_API_KEY", "env-key")
                .Set("TYPESAFE_BASE_URL", "https://env.test///")
                .Set("TYPESAFE_DEFAULT_MODEL", "env-model");
        }

        var (key, url, model) = source switch
        {
            "constructor" => ("code-key", "https://code.test", "code-model"),
            "env" => ("env-key", "https://env.test", "env-model"),
            _ => ("test-key", "https://api.typesafe.ai", "jev-latest"),
        };

        var handler = Ok();
        using var client = source switch
        {
            "constructor" => TestClient.Create(handler, apiKey: "code-key", baseUrl: "https://code.test///", model: "code-model"),
            "env" => TestClient.Create(handler, apiKey: null),
            _ => TestClient.Create(handler),
        };

        await client.Call();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(key, request.Headers.Authorization?.Parameter);
        Assert.Equal($"{url}/v1/systemone", request.Uri.ToString());
        Assert.Equal(model, (string?)request.Json["model"]);
    }

    [Fact]
    public void MissingApiKey_ThrowsAndNamesTheEnvironmentVariable()
    {
        var error = Assert.Throws<TypeSafeException>(() => new TypeSafeClient());

        Assert.Contains("TYPESAFE_API_KEY", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void BlankApiKey_Throws(string key)
    {
        Assert.Throws<TypeSafeException>(() => new TypeSafeClient(new TypeSafeClientOptions { ApiKey = key }));

        using var env = new EnvScope().Set("TYPESAFE_API_KEY", key);
        Assert.Throws<TypeSafeException>(() => new TypeSafeClient());
    }

    [Theory]
    [InlineData("", "env")]
    [InlineData("\n", "env")]
    [InlineData("\r\n", "env")]
    [InlineData(" \t\r\n ", "env")]
    [InlineData("", "constructor")]
    [InlineData("\n", "constructor")]
    [InlineData("\r\n", "constructor")]
    [InlineData(" \t\r\n ", "constructor")]
    public async Task ApiKey_SurroundingWhitespaceIsTrimmed(string padding, string source)
    {
        using var env = new EnvScope().Set("TYPESAFE_API_KEY", source == "env" ? $"{padding}test-key{padding}" : "env-key");
        var handler = Ok();
        using var client = TestClient.Create(handler, apiKey: source == "env" ? null : $"{padding}test-key{padding}");

        await client.Call();

        Assert.Equal("test-key", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    [InlineData("\0private")]
    [InlineData("private\0")]
    public void InvalidExplicitKey_DoesNotFallBackToTheEnvironment(string key)
    {
        using var env = new EnvScope().Set("TYPESAFE_API_KEY", "env-key");

        Assert.Throws<TypeSafeException>(() => TestClient.Create(Ok(), apiKey: key));
    }

    [Theory]
    [InlineData("env", '\n')]
    [InlineData("env", '\r')]
    [InlineData("env", '\t')]
    [InlineData("env", '\u001f')]
    [InlineData("env", '\u007f')]
    [InlineData("env", ' ')]
    [InlineData("env", '\u00e9')]
    [InlineData("env", '\u200b')]
    [InlineData("constructor", '\n')]
    [InlineData("constructor", '\r')]
    [InlineData("constructor", '\t')]
    [InlineData("constructor", '\u001f')]
    [InlineData("constructor", '\u007f')]
    [InlineData("constructor", ' ')]
    [InlineData("constructor", '\u00e9')]
    [InlineData("constructor", '\u200b')]
    public void InvalidApiKey_IsRejectedWithoutLeakingTheCredential(string source, char character)
    {
        const string credential = "ts_live_private";
        var key = $"{credential}{character}suffix";
        using var env = new EnvScope().Set("TYPESAFE_API_KEY", source == "env" ? key : "env-key");

        var error = Assert.Throws<TypeSafeException>(() => TestClient.Create(Ok(), apiKey: source == "env" ? null : key));

        Assert.Contains("API key", error.Message);
        Assert.DoesNotContain(credential, error.Message);
        Assert.DoesNotContain(credential, error.ToString());
    }

    [Fact]
    public async Task BaseUrl_KeepsPathPrefixAndTrimsTrailingSlashes()
    {
        var handler = Ok();
        using var client = TestClient.Create(handler, baseUrl: "https://example.test/prefix///");

        await client.Call();

        Assert.Equal("https://example.test/prefix/v1/systemone", handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task ClientTimeout_AppliesToEachAttempt()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply.Ok();
        });
        using var client = TestClient.Create(handler, timeout: TimeSpan.FromMilliseconds(40));

        var error = await Assert.ThrowsAsync<TypeSafeTimeoutException>(() => client.Call());

        Assert.Equal(TimeSpan.FromMilliseconds(40), error.Timeout);
    }

    [Fact]
    public async Task OwnedHttpClient_DoesNotImposeItsOwnTimeout()
    {
        using var client = new TypeSafeClient(new TypeSafeClientOptions
        {
            ApiKey = "k",
            BaseUrl = "http://127.0.0.1:1",
            Retry = new RetryPolicy { MaxRetries = 0 },
        });

        // Nothing listens on port 1, so this must fail as a connection error rather than a TaskCanceledException.
        await Assert.ThrowsAsync<TypeSafeConnectionException>(() => client.Call());
    }
}
