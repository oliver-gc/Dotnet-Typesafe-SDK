# 1.1.0

## Added

- Images, as an experimental feature enabled with the `TypeSafe-Danger-Zone-Images: true` header. Create them with `Image` (from bytes, a file, a stream or base64), then place them in the state, question instructions or criteria, write them into text with `TextWithAssets.Create` and `TextWithAssets.Join`, or reference them by names you choose with the new `assets` argument of `SystemOneAsync`. Requests with images are sent as `multipart/form-data`.
- `BoxQuestion`, which locates what its instructions describe in an image, with `BoxAnswer`, `BoxCoordinates` and the `SystemOneResponse.Boxes` accessor. Use it with the `jev-preview` model.
- zstd compression of request bodies of 1 KiB or more, controlled by `TypeSafeClientOptions.Compression`.
- `TypeSafeClientOptions.DefaultHeaders`, and a `headers` argument on `SystemOneAsync` and `ListModelsAsync` for per-call headers.
- Every request sends `Accept`, `User-Agent`, `X-TypeSafe-SDK` and `X-TypeSafe-Runtime` headers, and retries send `X-TypeSafe-Retry-Count`. `TypeSafeClient.Version` exposes the SDK version.
- `RetryPolicy.MaxRetryAfter` caps how long a server-requested wait is honored; longer waits use backoff. It is unset by default, so any wait is honored as before.
- `SystemOneAsync` validates questions before sending: at least one question, nonempty choice criteria, at least two score criteria, and instructions or a described outcome for noul questions.

## Changed

- `Question.Instructions` is optional and nullable, and score criteria entries may be null.
- `NaN` and infinities in request content are sent as the strings `"NaN"`, `"Infinity"` and `"-Infinity"` instead of failing to serialize.
- Characters such as `<`, `>` and `&` are sent unescaped in request JSON.
- Blank `TYPESAFE_*` environment variables are treated as unset.
- The package depends on `ZstdSharp.Port`.



# 1.0.2

## Fixed

- The README now uses plain Markdown instead of HTML.



# 1.0.1

Stable Release.

## Added

- `TypeSafeClient.ListModelsAsync` calls `GET /v1/models` and returns the models and aliases available to the account, each with a name, description and release date (`ModelListResponse`, `ModelInfo`).
- README section on listing models.

## Changed

- `SystemOneAsync` and `ListModelsAsync` now share one request pipeline, so both get the same authentication, timeout, retry and logging behaviour.
- Log messages and `TypeSafeApiException.Endpoint` for `/v1/models` use the `GET` method label. `/v1/systemone` is unchanged.



# 0.1.0

Beta release.

## Added

- `TypeSafeClient.SystemOneAsync` calls `POST /v1/systemone` with a state, a set of questions, an optional model and optional extra body fields.
- Question types: `NoulQuestion`, `ChoiceQuestion` and `ScoreQuestion`.
- Typed answers (`NoulAnswer`, `ChoiceAnswer`, `ScoreAnswer`) with `Nouls`, `Choices` and `Scores` accessors on `SystemOneResponse`. Unknown answer kinds are skipped, and the raw body stays available.
- `TypeSafeClientOptions` for `ApiKey`, `BaseUrl`, `Model`, `Retry`, `Timeout`, `HttpClient`, `LogLevel` and `Logger`, falling back to the `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, `TYPESAFE_DEFAULT_MODEL` and `TYPESAFE_LOG_LEVEL` environment variables.
- `RetryPolicy` with exponential backoff, jitter, `Retry-After` support and an overall time budget, overridable per call.
- Typed exceptions for API errors, invalid responses, connection failures and timeouts.
- Optional request and response logging, with credentials redacted.