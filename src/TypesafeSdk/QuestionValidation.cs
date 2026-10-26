namespace TypesafeSdk;

using TypesafeSdk.Models;

/// <summary>Checks questions before sending, so mistakes fail fast instead of as a 400 or 422.</summary>
internal static class QuestionValidation
{
    public static void Validate(IReadOnlyDictionary<string, Question> questions)
    {
        if (questions.Count == 0)
        {
            throw new TypeSafeException("At least one question is required.");
        }

        foreach (var (name, question) in questions)
        {
            switch (question)
            {
                case null:
                    throw new TypeSafeException($"Question \"{name}\" is null.");
                case NoulQuestion { Instructions: null } noul when noul.Criteria is not { True: not null } and not { False: not null }:
                    throw new TypeSafeException(
                        $"Noul question \"{name}\" has neither instructions nor criteria; at least one is required.");
                case ChoiceQuestion { Criteria: null or { Count: 0 } }:
                    throw new TypeSafeException($"Choice question \"{name}\" has no criteria; at least one choice is required.");
                case ScoreQuestion { Criteria: var criteria } when criteria is null || criteria.Count < 2:
                    throw new TypeSafeException(
                        $"Score question \"{name}\" has {criteria?.Count ?? 0} criteria; at least two scores are required.");
            }
        }
    }
}
