using TypesafeSdk.Models;

namespace TypesafeSdk.Tests;

/// <summary>Live API tests; skipped unless TYPESAFE_API_KEY is set.</summary>
[Trait("Category", "Integration")]
public class IntegrationTests
{
    [Fact]
    public async Task LiveQuestions_ReturnWellFormedAnswers()
    {
        var key = Environment.GetEnvironmentVariable(TypeSafeClient.ApiKeyEnv)?.Trim();
        Assert.SkipWhen(string.IsNullOrEmpty(key), $"{TypeSafeClient.ApiKeyEnv} is not set");

        using var client = new TypeSafeClient(new TypeSafeClientOptions { ApiKey = key, Timeout = TimeSpan.FromSeconds(120) });

        var result = await client.SystemOneAsync(
            new
            {
                subject = "Charged twice this month",
                body = "I see two charges of $49. I only have one account. Please fix this ASAP.",
            },
            new Dictionary<string, Question>
            {
                ["billing"] = new NoulQuestion
                {
                    Instructions = "Is this ticket about billing?",
                    Criteria = new NoulCriteria
                    {
                        True = new Dictionary<string, object> { ["meaning"] = "Payments or invoices", ["examples"] = new[] { "charged twice" } },
                    },
                },
                ["tone"] = new ChoiceQuestion
                {
                    Instructions = "What is the customer's tone?",
                    Criteria = new Dictionary<string, object?> { ["calm"] = null, ["frustrated"] = null, ["angry"] = null },
                },
                ["urgency"] = new ScoreQuestion
                {
                    Instructions = "How urgent is this ticket?",
                    Criteria = ["can wait", "this week", "today"],
                },
            });

        Assert.False(string.IsNullOrEmpty(result.Model));
        Assert.True(result.Usage.InputTokens is null or > 0);
        Assert.True(result.Usage.OutputTokens is null or >= 0);
        Assert.False(string.IsNullOrEmpty(result.RequestId));

        Assert.InRange(result.Nouls["billing"].Noul, 0, 1);

        var tone = result.Choices["tone"];
        Assert.Contains(tone.Choice, new[] { "calm", "frustrated", "angry" });
        Assert.InRange(tone.Probabilities.Values.Sum(), 0.9, 1.1);

        var urgency = result.Scores["urgency"];
        Assert.InRange(urgency.Score, 0, 2);
        Assert.Equal(new[] { "can wait", "this week", "today" }, urgency.Legend.OrderBy(kv => kv.Key).Select(kv => kv.Value.GetString()));
        Assert.Equal(new[] { 0, 1, 2 }, urgency.Probabilities.Keys.Order());
        Assert.InRange(urgency.Probabilities.Values.Sum(), 0.9, 1.1);
    }
}
