using TypesafeSdk.Models;

namespace TypesafeSdk.Tests;

public class ResponseParserTests
{
    private static string WithAnswers(string answers) => $$"""{"model":"m","usage":{"input_tokens":1,"output_tokens":1},"answers":{{answers}}}""";

    [Theory]
    [InlineData("""{"usage":{},"answers":{}}""", "model")]
    [InlineData("""{"model":1,"usage":{},"answers":{}}""", "model")]
    [InlineData("""{"model":"m","answers":{}}""", "usage")]
    [InlineData("""{"model":"m","usage":[],"answers":{}}""", "usage")]
    [InlineData("""{"model":"m","usage":{},"answers":[]}""", "answers")]
    [InlineData("""{"model":"m","usage":{},"answers":"x"}""", "answers")]
    [InlineData("[]", "$")]
    [InlineData("42", "$")]
    [InlineData("not json", "$")]
    [InlineData("", "$")]
    public void InvalidEnvelope_ReportsTheFieldPath(string body, string path)
    {
        var error = Assert.Throws<InvalidFieldException>(() => ResponseParser.Parse(body, null));

        Assert.Equal(path, error.FieldPath);
    }

    [Theory]
    [InlineData("""{"n":{"type":"noul"}}""", "answers.n.noul")]
    [InlineData("""{"n":{"type":"noul","noul":"0.5"}}""", "answers.n.noul")]
    [InlineData("""{"n":{"noul":0.5}}""", "answers.n.type")]
    [InlineData("""{"n":{"type":7,"noul":0.5}}""", "answers.n.type")]
    [InlineData("""{"c":"not-a-mapping"}""", "answers.c")]
    [InlineData("""{"c":{"type":"choice","confidence":0.5,"probabilities":{}}}""", "answers.c.choice")]
    [InlineData("""{"c":{"type":"choice","choice":"a","probabilities":{}}}""", "answers.c.confidence")]
    [InlineData("""{"c":{"type":"choice","choice":"a","confidence":0.5}}""", "answers.c.probabilities")]
    [InlineData("""{"c":{"type":"choice","choice":"a","confidence":0.5,"probabilities":{"a":"high"}}}""", "answers.c.probabilities.a")]
    [InlineData("""{"s":{"type":"score","confidence":1,"legend":{},"probabilities":{}}}""", "answers.s.score")]
    [InlineData("""{"s":{"type":"score","score":1,"legend":{},"probabilities":{}}}""", "answers.s.confidence")]
    [InlineData("""{"s":{"type":"score","score":1,"confidence":1,"legend":[],"probabilities":{}}}""", "answers.s.legend")]
    [InlineData("""{"s":{"type":"score","score":1,"confidence":1,"legend":{"x":"bad"},"probabilities":{}}}""", "answers.s.legend.x")]
    [InlineData("""{"s":{"type":"score","score":1,"confidence":1,"legend":{},"probabilities":[]}}""", "answers.s.probabilities")]
    [InlineData("""{"s":{"type":"score","score":1,"confidence":1,"legend":{},"probabilities":{"x":1}}}""", "answers.s.probabilities.x")]
    [InlineData("""{"s":{"type":"score","score":1,"confidence":1,"legend":{},"probabilities":{"0":"1"}}}""", "answers.s.probabilities.0")]
    public void MalformedAnswer_ReportsTheFieldPath(string answers, string path)
    {
        var error = Assert.Throws<InvalidFieldException>(() => ResponseParser.Parse(WithAnswers(answers), null));

        Assert.Equal(path, error.FieldPath);
    }

    [Fact]
    public void MissingAnswers_YieldAnEmptyResponse()
    {
        var response = ResponseParser.Parse("""{"model":"m","usage":{}}""", "req-1");

        Assert.Empty(response.Answers);
        Assert.Equal("req-1", response.RequestId);
    }

    [Theory]
    [InlineData("""{}""", null, null)]
    [InlineData("""{"input_tokens":5}""", 5, null)]
    [InlineData("""{"output_tokens":7}""", null, 7)]
    [InlineData("""{"input_tokens":"5","output_tokens":null}""", null, null)]
    [InlineData("""{"input_tokens":12,"output_tokens":3,"reasoning_tokens":9}""", 12, 3)]
    public void Usage_FieldsAreOptional(string usage, int? input, int? output)
    {
        var response = ResponseParser.Parse($$"""{"model":"m","usage":{{usage}}}""", null);

        Assert.Equal(new Usage(input, output), response.Usage);
    }

    [Fact]
    public void UnknownAnswerKinds_AreSkippedAndReported()
    {
        var skipped = new List<string>();
        var body = WithAnswers("""
            {"a":{"type":"noul","noul":0.1},"b":{"type":"aurora","value":3},"c":{"type":"noul","noul":0.2}}
            """);

        var response = ResponseParser.Parse(body, null, skipped.Add);

        Assert.Equal(new[] { "a", "c" }, response.Answers.Keys.Order());
        Assert.Equal(["answers.b (type 'aurora')"], skipped);
    }

    [Fact]
    public void ScoreAnswer_PreservesLegendStructureAndIntegerKeys()
    {
        var body = WithAnswers("""
            {"q":{"type":"score","score":0,"confidence":1,
              "legend":{"0":{"examples":["a",{"note":null}]},"2":"great"},
              "probabilities":{"0":0.25,"2":0.75}}}
            """);

        var answer = Assert.IsType<ScoreAnswer>(ResponseParser.Parse(body, null).Answers["q"]);

        Assert.Equal(new[] { 0, 2 }, answer.Legend.Keys.Order());
        Assert.Equal("a", answer.Legend[0].GetProperty("examples")[0].GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, answer.Legend[0].GetProperty("examples")[1].GetProperty("note").ValueKind);
        Assert.Equal("great", answer.Legend[2].GetString());
        Assert.Equal(0.25, answer.Probabilities[0]);
        Assert.Equal(0.75, answer.Probabilities[2]);
    }

    [Fact]
    public void Legend_OutlivesTheParsedDocument()
    {
        var response = ResponseParser.Parse(
            WithAnswers("""{"q":{"type":"score","score":0,"confidence":1,"legend":{"0":{"k":"v"}},"probabilities":{"0":1}}}"""), null);

        GC.Collect();

        Assert.Equal("v", ((ScoreAnswer)response.Answers["q"]).Legend[0].GetProperty("k").GetString());
    }

    [Fact]
    public void IntegerValuesAreAcceptedWhereDoublesAreExpected()
    {
        var body = WithAnswers("""{"n":{"type":"noul","noul":1},"c":{"type":"choice","choice":"a","confidence":1,"probabilities":{"a":1}}}""");

        var response = ResponseParser.Parse(body, null);

        Assert.Equal(1.0, ((NoulAnswer)response.Answers["n"]).Noul);
        Assert.Equal(1.0, ((ChoiceAnswer)response.Answers["c"]).Probabilities["a"]);
    }

    [Fact]
    public void RawBody_IsKeptVerbatim()
    {
        const string body = """  { "model" : "m", "usage": {}, "extra": [1, 2] }  """;

        Assert.Equal(body, ResponseParser.Parse(body, null).RawBody);
    }
}
