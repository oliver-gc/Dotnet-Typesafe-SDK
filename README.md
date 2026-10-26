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

## Images

Image support is experimental. Opt in by sending the `TypeSafe-Danger-Zone-Images: true` header, for every request or for a single call through its `headers` argument:

```csharp
using var client = new TypeSafeClient(new TypeSafeClientOptions
{
    DefaultHeaders = new Dictionary<string, string> { ["TypeSafe-Danger-Zone-Images"] = "true" },
});
```

Create an `Image` from bytes, `Image.FromFile`, `Image.FromFileAsync`, `Image.FromStreamAsync` or `Image.FromBase64`. Images must be PNG or JPEG; the API rejects other formats with a `TypeSafeBadRequestException`. Requests with images are sent as `multipart/form-data`, and each image is sent once however many times it is used. There are three ways to use one, and they can be combined.

Place the image anywhere in the state, question instructions or criteria:

```csharp
var photo = await Image.FromFileAsync("returns/RMA-1042.jpg");

var response = await client.SystemOneAsync(
    state: new { claim = "The mug arrived with a chipped rim.", photo },
    questions: new Dictionary<string, Question>
    {
        ["damaged"] = new NoulQuestion { Instructions = "Is the product in the photo damaged?" },
    });
```

Write it into text with `TextWithAssets.Create`, or join a list with `TextWithAssets.Join`:

```csharp
var state = TextWithAssets.Create($"A seller uploaded {upload} for a listing whose catalog photo is {catalog}.");
var uploads = TextWithAssets.Join(", ", photos);
```

A plain interpolated string such as `$"Compare {upload}"` holds only the image's reference, so the call throws a `TypeSafeException` before sending unless that image is attached another way.

Or pass images in `assets` under names of your choice, and use the names in plain strings, such as prompts stored in configuration:

```csharp
var response = await client.SystemOneAsync(
    state: "A customer returned <delivered>. The catalog photo is <catalog>.",
    questions: new Dictionary<string, Question>
    {
        ["matches"] = new NoulQuestion { Instructions = "Is <delivered> the product shown in <catalog>?" },
    },
    assets: new Dictionary<string, Image>
    {
        ["<delivered>"] = await Image.FromFileAsync("returns/RMA-1042.jpg"),
        ["<catalog>"] = await Image.FromFileAsync("catalog/mug.png"),
    });
```

Use distinct delimiters such as `<delivered>` so names don't match ordinary text. Every name in `assets` must appear in the request, or the call throws before sending.

### Locating things with box questions

A `BoxQuestion` locates what its instructions describe. The answer's `Box` holds the edges as fractions of the image's height (`Top`, `Bottom`) and width (`Left`, `Right`), and `Confidence` is null when the API doesn't report one. Use the `jev-preview` model:

```csharp
var response = await client.SystemOneAsync(
    state: await Image.FromFileAsync("returns/RMA-1042.jpg"),
    questions: new Dictionary<string, Question> { ["damage"] = new BoxQuestion { Instructions = "The damaged area" } },
    model: "jev-preview");

var (top, bottom, left, right) = response.Boxes["damage"].Box;
```

## Validation

`SystemOneAsync` throws a `TypeSafeException` before sending when there are no questions, a choice question has no criteria, a score question has fewer than two criteria, or a noul question has neither instructions nor a described outcome. Instructions are otherwise optional.

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

Pass `TypeSafeClientOptions` to override defaults: `ApiKey`, `BaseUrl`, `Model`, `Retry`, `Timeout`, `HttpClient`, `LogLevel`, `Logger`, `DefaultHeaders` and `Compression`. Unset or blank values fall back to `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, `TYPESAFE_DEFAULT_MODEL` and `TYPESAFE_LOG_LEVEL`.

`DefaultHeaders` are sent with every request, and the `headers` argument of `SystemOneAsync` and `ListModelsAsync` adds or overrides headers for one call. Headers the SDK sets, such as `Authorization` and `Content-Type`, can't be overridden.

## Request compression

Request bodies of 1 KiB or more are compressed with zstd and sent with `Content-Encoding: zstd`. Smaller bodies fit in a single TCP segment, so they are sent uncompressed. Set `Compression = TypeSafeCompression.None` to send all bodies uncompressed, for example to an API that doesn't accept compressed requests.

## License

MIT
