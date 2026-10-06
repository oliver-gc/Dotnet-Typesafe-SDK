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