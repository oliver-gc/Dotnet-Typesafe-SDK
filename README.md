# Dotnet Typesafe SDK

.NET client for the [TypeSafe](https://typesafe.ai) API.

[![CI](https://img.shields.io/github/actions/workflow/status/oliver-gc/Dotnet-Typesafe-SDK/ci.yml?branch=main&logo=github&label=CI)](https://github.com/oliver-gc/Dotnet-Typesafe-SDK/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/TypesafeSdk?label=nuget)](https://www.nuget.org/packages/TypesafeSdk)
[![Downloads](https://img.shields.io/nuget/dt/TypesafeSdk?label=downloads)](https://www.nuget.org/packages/TypesafeSdk)
[![License](https://img.shields.io/github/license/oliver-gc/Dotnet-Typesafe-SDK?label=license)](https://github.com/oliver-gc/Dotnet-Typesafe-SDK/blob/main/LICENSE)

Please report all issues and feature requests on the [GitHub issue tracker](https://github.com/oliver-gc/Dotnet-Typesafe-SDK/issues).

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

## Listing models

`ListModelsAsync` calls `GET /v1/models` and returns the names your account can send in the `model` field, each with a description and release date. It currently lists aliases; versioned IDs such as `jev-1.13.0` are accepted whether or not they appear in the list.

```csharp
var models = await client.ListModelsAsync();

foreach (var model in models.Models)
{
    Console.WriteLine($"{model.Name} ({model.ReleaseDate}): {model.Description}");
}
```
## Configuration

Pass `TypeSafeClientOptions` to override defaults: `ApiKey`, `BaseUrl`, `Model`, `Retry`, `Timeout`, `HttpClient`, `LogLevel` and `Logger`. Unset values fall back to `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, `TYPESAFE_DEFAULT_MODEL` and `TYPESAFE_LOG_LEVEL`.

## License

MIT
