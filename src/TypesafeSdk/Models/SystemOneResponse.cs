namespace TypesafeSdk.Models;

public sealed record Usage(int? InputTokens, int? OutputTokens);

public sealed class SystemOneResponse
{
    public required string Model { get; init; }
    public required Usage Usage { get; init; }

    /// <summary>All recognized answers keyed by question id; unknown answer kinds are skipped.</summary>
    public required IReadOnlyDictionary<string, Answer> Answers { get; init; }

    /// <summary>The x-typesafe-request-id response header.</summary>
    public string? RequestId { get; init; }

    /// <summary>The unmodified response body, including answer kinds this SDK doesn't recognize.</summary>
    public required string RawBody { get; init; }

    public IReadOnlyDictionary<string, NoulAnswer> Nouls => field ??= Filter<NoulAnswer>();
    public IReadOnlyDictionary<string, ChoiceAnswer> Choices => field ??= Filter<ChoiceAnswer>();
    public IReadOnlyDictionary<string, ScoreAnswer> Scores => field ??= Filter<ScoreAnswer>();

    private Dictionary<string, T> Filter<T>() where T : Answer =>
        Answers.Where(kv => kv.Value is T).ToDictionary(kv => kv.Key, kv => (T)kv.Value);
}
