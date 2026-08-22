# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- Regenerated from the published document now that it names its own components. The shapes the
  generator used to name after the property that held them carry the document's names instead:
  `MonitorWriteRequestLocations` -> `MonitorLocations`, `MonitorPatchRequestRecheck` ->
  `MonitorRecheck`, `MonitorBulkCreateRequestItem` -> `MonitorBulkItem`,
  `ContactWriteRequestActivePeriod` -> `ContactActivePeriod`, `StatusPageWriteRequestSettings` ->
  `StatusPageSettings`, `WebhookWriteRequestHeader` -> `WebhookHeader`, `MonitorBulkDeleteRequestCallback`
  -> `JobCallback`, `Problem_not_foundError` -> `NotFoundError`, and about ninety more. One name per
  shape now, so the `2`-suffixed twins (`ProblemError2`) are gone. The wire is unchanged.
- `GetMonitorResultSnapshotAsync` takes the optional `if_None_Match` the document now declares. An
  unchanged snapshot answers `304`, which arrives as a `HostTrackerException` with `StatusCode` 304.
- `scripts/prep-spec.py` drops the four transforms the document made unnecessary: tag PascalCasing,
  `$ref` sibling removal, `anyOf` null collapsing, and page-envelope hoisting. Enums are still
  opened, closed sets included: NSwag's System.Text.Json output spells a member's wire value only in
  `[EnumMember]`, which `JsonStringEnumConverter` ignores on net8, so a generated enum would write
  `"VoiceCall"` for `voiceCall` and fail to read `"monitor.down"`.

### Security

- `RunCheckAsync` takes only the path and query of the server-supplied `resultUrl` and dials them on
  the configured `BaseUrl`, so the bearer token cannot reach another origin. Non-http(s) values are
  still refused.

## [0.1.0] - Unreleased

First release. Targets `net8.0`; package id `HostTracker.Sdk`.

### Added

- Generated typed client for the HostTracker API v2 operations, grouped by family
  (`Monitors`, `Contacts`, `Webhooks`, `Jobs`, `InstantChecks`, …), with methods named after their
  `operationId`. Produced by NSwag from the published OpenAPI document and committed to the repo.
- `HostTrackerClient` over a single `HttpClient` and handler pipeline: bearer auth, SDK user-agent,
  automatic `Idempotency-Key` on writes, narrow automatic retry, and one error type.
- `HostTrackerException` carrying the full RFC 9457 problem document (`Code`, `Type`, `Title`,
  `Detail`, `Instance`, `Errors[]`) plus `RequestId`, `RetryAfter` and the `RateLimit-*` snapshot.
  Non-problem failures map to the same type with `http_error` / `network_error`.
- Retry policy: `429 rate_limited`, `503 service_unavailable` with a `Retry-After`, 429/503 with no
  problem body, and transport failures. Never `quota_exceeded`. Writes only when keyed. Timeouts are
  per attempt.
- `Pagination.PaginateAsync` / `PagesAsync` over the `{ data, nextCursor, hasMore }` envelope, with
  every generated `*Page` type implementing `IPageEnvelope<T>`.
- `WaitForJobAsync` and `JobResultsAsync` for the async bulk doors, honouring `Retry-After` and
  treating `partial` as a success and `interrupted` as resumable.
- `RunCheckAsync` for `POST /check`, following the server-supplied `resultUrl` to a terminal state.
- `WebhookSignature.Verify` for both the `HT-Signature` and Standard Webhooks schemes, including
  secret rotation and timestamp tolerance, and `WebhookEvent.Parse` for the delivery envelope.
- `CaptureResponses()` for per-call response metadata the generated signatures cannot return.
- `UnixTime` helpers, and generated constant classes for the document's vocabularies.
- `SendJsonAsync` escape hatch for explicit nulls and endpoints newer than the SDK build.

[Unreleased]: https://github.com/HostTracker/hosttracker-sdk-dotnet/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/HostTracker/hosttracker-sdk-dotnet/releases/tag/v0.1.0
