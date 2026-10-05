namespace TypesafeSdk.Models;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
public abstract class Question
{
    /// <summary>A string, object, or array describing what to evaluate.</summary>
    [JsonPropertyName("instructions")]
    public required object Instructions { get; init; }
}

/// <summary>Yes/no question; the answer is a probability of yes.</summary>
public sealed class NoulQuestion : Question
{
    [JsonPropertyName("criteria")]
    public NoulCriteria? Criteria { get; init; }
}

public sealed class NoulCriteria
{
    [JsonPropertyName("true")]
    public object? True { get; init; }

    [JsonPropertyName("false")]
    public object? False { get; init; }
}

/// <summary>Picks one option; criteria maps option to its description (null for none). Max 255 options.</summary>
public sealed class ChoiceQuestion : Question
{
    [JsonPropertyName("criteria")]
    public required IReadOnlyDictionary<string, object?> Criteria { get; init; }
}

/// <summary>Rates along an ordered rubric of 2 to 10 level descriptions.</summary>
public sealed class ScoreQuestion : Question
{
    [JsonPropertyName("criteria")]
    public required IReadOnlyList<object> Criteria { get; init; }
}
