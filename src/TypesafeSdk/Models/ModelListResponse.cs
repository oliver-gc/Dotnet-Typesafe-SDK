namespace TypesafeSdk.Models;

/// <summary>A model ID or alias accepted by the model field.</summary>
/// <param name="Name">The model ID or alias.</param>
/// <param name="Description">What the model is for.</param>
/// <param name="ReleaseDate">When the model or alias was released, as returned by the API.</param>
public sealed record ModelInfo(string Name, string Description, string ReleaseDate);

public sealed class ModelListResponse
{
    /// <summary>One entry per model or alias. Versioned IDs are accepted even when not listed.</summary>
    public required IReadOnlyList<ModelInfo> Models { get; init; }

    /// <summary>The x-typesafe-request-id response header.</summary>
    public string? RequestId { get; init; }

    /// <summary>The unmodified response body.</summary>
    public required string RawBody { get; init; }
}