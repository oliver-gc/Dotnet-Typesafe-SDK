namespace TypesafeSdk.Models;

using System.Text.Json;

public abstract record Answer;

/// <param name="Noul">Probability of yes, from 0 to 1.</param>
public sealed record NoulAnswer(double Noul) : Answer;

public sealed record ChoiceAnswer(
    string Choice,
    double Confidence,
    IReadOnlyDictionary<string, double> Probabilities) : Answer;

/// <param name="Score">Probability-weighted level; may fall between integer levels.</param>
/// <param name="Confidence">Certainty from 0 to 1.</param>
/// <param name="Legend">Rubric description per level.</param>
/// <param name="Probabilities">Probability of each level.</param>
public sealed record ScoreAnswer(
    double Score,
    double Confidence,
    IReadOnlyDictionary<int, JsonElement> Legend,
    IReadOnlyDictionary<int, double> Probabilities) : Answer;

/// <summary>A box's edges as fractions (0 to 1) of the image's height (top, bottom) and width (left, right).</summary>
public sealed record BoxCoordinates(double Top, double Bottom, double Left, double Right);

/// <param name="Box">Where the instructions' subject is in the image.</param>
/// <param name="Confidence">Certainty from 0 to 1, or null when the API didn't report one.</param>
public sealed record BoxAnswer(BoxCoordinates Box, double? Confidence) : Answer;
