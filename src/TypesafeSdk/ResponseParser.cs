namespace TypesafeSdk;

using System.Text.Json;
using TypesafeSdk.Models;

internal sealed class InvalidFieldException(string fieldPath) : Exception(fieldPath)
{
    public string FieldPath { get; } = fieldPath;
}

internal static class ResponseParser
{
    public static SystemOneResponse Parse(string body, string? requestId, Action<string>? onUnknownKind = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidFieldException("$");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidFieldException("$");
            }

            var usage = GetObject(root, "usage", "usage");
            var answers = new Dictionary<string, Answer>();

            if (root.TryGetProperty("answers", out var answersElement))
            {
                if (answersElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidFieldException("answers");
                }

                foreach (var entry in answersElement.EnumerateObject())
                {
                    var answer = ParseAnswer(entry.Value, $"answers.{entry.Name}", onUnknownKind);
                    if (answer is not null)
                    {
                        answers[entry.Name] = answer;
                    }
                }
            }

            return new SystemOneResponse
            {
                Model = GetString(root, "model", "model"),
                Usage = new Usage(GetOptionalInt(usage, "input_tokens"), GetOptionalInt(usage, "output_tokens")),
                Answers = answers,
                RequestId = requestId,
                RawBody = body,
            };
        }
    }

    // Returns null for answer kinds this SDK doesn't know, so new API features don't break old clients.
    private static Answer? ParseAnswer(JsonElement element, string path, Action<string>? onUnknownKind)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidFieldException(path);
        }

        var type = GetString(element, "type", $"{path}.type");
        Answer? answer = type switch
        {
            "noul" => new NoulAnswer(GetDouble(element, "noul", $"{path}.noul")),
            "choice" => new ChoiceAnswer(
                GetString(element, "choice", $"{path}.choice"),
                GetDouble(element, "confidence", $"{path}.confidence"),
                GetMap(element, "probabilities", path)),
            "score" => new ScoreAnswer(
                GetDouble(element, "score", $"{path}.score"),
                GetDouble(element, "confidence", $"{path}.confidence"),
                GetLegend(element, path),
                GetProbabilities(element, path)),
            _ => null,
        };

        if (answer is null)
        {
            onUnknownKind?.Invoke($"{path} (type '{type}')");
        }

        return answer;
    }

    private static Dictionary<string, double> GetMap(JsonElement parent, string name, string path)
    {
        var map = new Dictionary<string, double>();
        foreach (var entry in GetObject(parent, name, $"{path}.{name}").EnumerateObject())
        {
            map[entry.Name] = entry.Value.TryGetDouble(out var value)
                ? value
                : throw new InvalidFieldException($"{path}.{name}.{entry.Name}");
        }

        return map;
    }

    private static Dictionary<int, double> GetProbabilities(JsonElement parent, string path)
    {
        var map = new Dictionary<int, double>();
        foreach (var entry in GetObject(parent, "probabilities", $"{path}.probabilities").EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var level) || !entry.Value.TryGetDouble(out var value))
            {
                throw new InvalidFieldException($"{path}.probabilities.{entry.Name}");
            }

            map[level] = value;
        }

        return map;
    }

    private static Dictionary<int, JsonElement> GetLegend(JsonElement parent, string path)
    {
        var map = new Dictionary<int, JsonElement>();
        foreach (var entry in GetObject(parent, "legend", $"{path}.legend").EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var level))
            {
                throw new InvalidFieldException($"{path}.legend.{entry.Name}");
            }

            // Clone so the element outlives the parsed document.
            map[level] = entry.Value.Clone();
        }

        return map;
    }

    private static JsonElement GetObject(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidFieldException(path);

    private static string GetString(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidFieldException(path);

    private static double GetDouble(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number
            : throw new InvalidFieldException(path);

    private static int? GetOptionalInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}
