# Dotnet Typesafe SDK

Unofficial .NET client for the [TypeSafe](https://typesafe.ai) API.

View on Nuget here: [Nuget](https://www.nuget.org/packages/TypesafeSdk)

## Install

```sh
dotnet add package TypesafeSdk
```

## Quickstart

Set `TYPESAFE_API_KEY` in your environment, then:

```csharp
using TypesafeSdk;
using TypesafeSdk.Models;

using var client = new TypeSafeClient();

var response = await client.SystemOneAsync(
    state: "I was charged twice. Please help ASAP.",
    questions: new Dictionary<string, Question>
    {
        ["billing"] = new NoulQuestion { Instructions = "Is this about billing?" },
        ["tone"] = new ChoiceQuestion
        {
            Instructions = "What is the tone?",
            Criteria = new Dictionary<string, object?> { ["calm"] = null, ["angry"] = null },
        },
        ["urgency"] = new ScoreQuestion
        {
            Instructions = "How urgent is this?",
            Criteria = ["low", "medium", "high"],
        },
    });

Console.WriteLine(response.Nouls["billing"].Noul);
Console.WriteLine(response.Choices["tone"].Choice);
Console.WriteLine(response.Scores["urgency"].Score);
```

## Configuration

Pass `TypeSafeClientOptions` to override defaults: `ApiKey`, `BaseUrl`, `Model`, `Retry`, `Timeout`, `HttpClient`, `LogLevel` and `Logger`. Unset values fall back to `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, `TYPESAFE_DEFAULT_MODEL` and `TYPESAFE_LOG_LEVEL`.

## License

MIT
