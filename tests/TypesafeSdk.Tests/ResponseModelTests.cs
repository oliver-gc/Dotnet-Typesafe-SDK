using System.Text.Json;
using TypesafeSdk.Models;

namespace TypesafeSdk.Tests;

public class ResponseModelTests
{
    private static SystemOneResponse Response(params (string Id, Answer Answer)[] answers) => new()
    {
        Model = "test",
        Usage = new Usage(null, null),
        Answers = answers.ToDictionary(a => a.Id, a => a.Answer),
        RawBody = "{}",
    };

    private static readonly NoulAnswer Noul = new(0.98);
    private static readonly ChoiceAnswer Choice = new("billing", 0.9, new Dictionary<string, double> { ["billing"] = 0.9 });
    private static readonly ScoreAnswer Score = new(1.5, 0.8, new Dictionary<int, JsonElement>(), new Dictionary<int, double>());

    [Fact]
    public void AnswerGroups_FilterByKindAndKeepIdentity()
    {
        var response = Response(("n", Noul), ("c", Choice), ("s", Score));

        Assert.Equal(new[] { "n" }, response.Nouls.Keys);
        Assert.Equal(new[] { "c" }, response.Choices.Keys);
        Assert.Equal(new[] { "s" }, response.Scores.Keys);
        Assert.Same(Noul, response.Nouls["n"]);
        Assert.Same(Choice, response.Choices["c"]);
        Assert.Same(Score, response.Scores["s"]);
        Assert.Same(response.Answers["n"], response.Nouls["n"]);
    }

    [Fact]
    public void AnswerGroups_AreCached()
    {
        var response = Response(("n", Noul));

        Assert.Same(response.Nouls, response.Nouls);
        Assert.Same(response.Choices, response.Choices);
        Assert.Same(response.Scores, response.Scores);
    }

    [Fact]
    public void AnswerGroups_AreEmptyWhenNoAnswerMatches()
    {
        var response = Response();

        Assert.Empty(response.Nouls);
        Assert.Empty(response.Choices);
        Assert.Empty(response.Scores);
    }

    [Fact]
    public void Answers_HaveValueEquality()
    {
        Assert.Equal(new NoulAnswer(0.5), new NoulAnswer(0.5));
        Assert.NotEqual(new NoulAnswer(0.5), new NoulAnswer(0.6));
        Assert.Equal(new Usage(1, 2), new Usage(1, 2));
        Assert.NotEqual<Answer>(new NoulAnswer(0.5), new ScoreAnswer(0.5, 1, new Dictionary<int, JsonElement>(), new Dictionary<int, double>()));
    }

    [Fact]
    public void Questions_RequireInstructionsAndKindSpecificCriteria()
    {
        var noul = new NoulQuestion { Instructions = "?" };
        var choice = new ChoiceQuestion { Instructions = "?", Criteria = new Dictionary<string, object?> { ["a"] = null } };
        var score = new ScoreQuestion { Instructions = "?", Criteria = ["good"] };

        Assert.Null(noul.Criteria);
        Assert.Single(choice.Criteria);
        Assert.Single(score.Criteria);
        Assert.All<Question>([noul, choice, score], q => Assert.Equal("?", q.Instructions));
    }

    [Fact]
    public void LogLevels_AreOrderedByVerbosity()
    {
        Assert.True(TypeSafeLogLevel.Off < TypeSafeLogLevel.Error);
        Assert.True(TypeSafeLogLevel.Error < TypeSafeLogLevel.Warning);
        Assert.True(TypeSafeLogLevel.Warning < TypeSafeLogLevel.Info);
        Assert.True(TypeSafeLogLevel.Info < TypeSafeLogLevel.Debug);
    }
}
