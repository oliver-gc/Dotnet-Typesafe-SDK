namespace TypesafeSdk.Models;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
[JsonDerivedType(typeof(BoxQuestion), "box")]
public abstract class Question
{
    /// <summary>
    /// A string, <see cref="Image"/>, <see cref="TextWithAssets"/>, object, or array describing what to evaluate.
    /// Optional; a <see cref="NoulQuestion"/> without instructions needs a described outcome in its criteria.
    /// </summary>
    [JsonPropertyName("instructions")]
    public object? Instructions { get; init; }
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

/// <summary>Rates along an ordered rubric of 2 to 10 level descriptions; null leaves a level undescribed.</summary>
public sealed class ScoreQuestion : Question
{
    [JsonPropertyName("criteria")]
    public required IReadOnlyList<object?> Criteria { get; init; }
}

/// <summary>Locates what its instructions describe in the request's image. Use the jev-preview model.</summary>
public sealed class BoxQuestion : Question;
