# HostTracker .NET SDK

The official .NET client for the [HostTracker](https://www.host-tracker.com) API v2 - uptime,
blacklist, certificate and instant checks, contacts and alerting, webhooks, status pages and reports.

- **Package:** [`HostTracker.Sdk`](https://www.nuget.org/packages/HostTracker.Sdk) · **Target:** `net8.0`
- **API reference:** <https://www.host-tracker.com/apidocs/v2> · **Base URL:** `https://api2.host-tracker.com`
- The typed surface is **generated from the published OpenAPI document**; a hand-written layer adds
  auth, retry, idempotency, paging, job/check polling and webhook verification.

```bash
dotnet add package HostTracker.Sdk
```

## Quick start

Mint a token on your [profile page](https://www.host-tracker.com/Profile) (scopes: `monitor`, `check`,
`webhook`). It is a long-lived JWT - keep it out of source control.

```csharp
using HostTracker.Sdk;
using HostTracker.Sdk.Generated;

using var client = new HostTrackerClient(Environment.GetEnvironmentVariable("HT_TOKEN"));

// 1. List monitors.
var page = await client.Monitors.ListMonitorAsync(limit: 50);
foreach (var m in page.Data)
    Console.WriteLine($"{m.Name,-30} {m.State} since {UnixTime.ToDateTimeOffset(m.Since):u}");

// 2. Create one.
var created = await client.Monitors.CreateMonitorAsync(new MonitorWriteRequest
{
    Type = MonitorTypes.Http,
    Url = "https://example.com",
    Name = "Marketing site",
    Interval = 5,
    Locations = new MonitorLocations { Pools = new[] { "allworld" } },
});
Console.WriteLine($"created {created.Id}");

// 3. Run an on-demand check and wait for the verdict.
var result = await client.RunCheckAsync(new IcCreateRequest
{
    Url = "https://example.com",
    Type = "http",
});
Console.WriteLine($"{result.State} with {result.Events?.Count ?? 0} location report(s)");

// 4. Verify a webhook delivery (in your endpoint, on the RAW bytes).
var verdict = WebhookSignature.Verify(request.Headers, rawBody, secrets: new[] { webhookSecret });
verdict.EnsureValid();
var evt = WebhookEvent.Parse(rawBody);
if (evt.Event == WebhookEvents.MonitorDown)
    Console.WriteLine(evt.DataAs<WebhookMonitorAlert>()!.Monitor!.Url);
```

## Configuration

```csharp
using var client = new HostTrackerClient(new HostTrackerOptions
{
    Token = "…",                                   // omit for the anonymous reference tier
    BaseUrl = "https://api2.host-tracker.com",     // default
    Timeout = TimeSpan.FromSeconds(30),            // PER ATTEMPT, not per call
    MaxRetries = 2,
    MaxRetryDelay = TimeSpan.FromSeconds(60),
    Idempotency = IdempotencyMode.Auto,
    UserAgentSuffix = "acme-deploy/2.1",
    Handler = new HttpClientHandler { Proxy = myProxy },
});
```

| Option | Default | What it does |
|---|---|---|
| `Token` | - | `Authorization: Bearer …` on every request. Omit it and only the anonymous reference tier answers. |
| `BaseUrl` | `https://api2.host-tracker.com` | A path prefix is honoured (`…/api2/`). |
| `Timeout` | 30 s | Budget for **one HTTP attempt**. A call honouring two `Retry-After` waits is not killed by it. |
| `MaxRetries` | 2 | Retries after the first attempt. |
| `MaxRetryDelay` | 60 s | Ceiling on a single honoured `Retry-After`. |
| `Idempotency` | `Auto` | Stamps a fresh `Idempotency-Key` on every write. |
| `UserAgentSuffix` | - | Appended to `hosttracker-sdk-dotnet/<version>`. |
| `Handler` | `HttpClientHandler` | The innermost transport - proxies, TLS, DNS. |
| `HttpClient` | - | Total control: the SDK adds no handlers of its own to it. |

The client is thread-safe and long-lived - build one per process (or register it as a singleton), not one per call.

## Operations

Operations are grouped by their API family; each method is named after its `operationId`:

`Monitors`, `MonitorTypes`, `Results`, `Incidents`, `Maintenance`, `Contacts`, `Alerts`, `Reports`,
`Webhooks`, `StatusPages`, `Account`, `MonitoringLocations`, `InstantChecks` (alias `Checks`), `Jobs`.

```csharp
await client.Monitors.ListMonitorAsync(state: new[] { MonitorStates.Down }, expand: new[] { "lastIncident" });
await client.Contacts.CreateContactAsync(new ContactWriteRequest { Type = ContactTypes.Email, Address = "ops@example.com" });
await client.Webhooks.TestWebhookAsync(webhookId);
```

Every paged `GET` also answers a `POST <path>/q` body-query twin with the same parameters as one JSON
object - reach for `QueryMonitorAsync(new MonitorQueryRequest { … })` when a filter is too long or too
awkward for a URL. The answer is identical.

Two operations return binary content as a `FileResponse` (a stream plus its headers):
`GetMonitorResultSnapshotAsync` (the check's screenshot) and `GetReportContentAsync`.

## Errors

Every failure - a problem document, an HTML 502 from a proxy, a DNS failure, a timeout - arrives as a
single `HostTrackerException`. **Branch on `Code`, never on `StatusCode` alone**: `rate_limited` and
`quota_exceeded` are both 429 and want opposite handling.

```csharp
try
{
    await client.Monitors.CreateMonitorAsync(request);
}
catch (HostTrackerException ex) when (ex.IsCode(ProblemCodes.DuplicateMonitor))
{
    var existing = ex.Errors.FirstOrDefault()?.Extensions["existingId"].GetString();
}
catch (HostTrackerException ex) when (ex.IsCode(ProblemCodes.QuotaExceeded))
{
    // The allowance is spent - wait for the reset or upgrade. Retrying changes nothing.
}
catch (HostTrackerException ex)
{
    Console.Error.WriteLine($"{ex.StatusCode} {ex.Code}: {ex.Detail} (request {ex.RequestId})");
    foreach (var e in ex.Errors) Console.Error.WriteLine($"  {e.Pointer}: {e.Reason}");
}
```

`Status`, `Code`, `Type`, `Title`, `Detail`, `Instance`, `Errors[]`, `RequestId`, `RetryAfter`,
`RateLimit` and the raw `Response` are all on the exception. Codes the SDK predates pass through as
plain strings - `ProblemCodes` names the ones worth branching on, not all of them.

## Retries and idempotency

Retried automatically: `429 rate_limited` (honouring `Retry-After`, capped at `MaxRetryDelay`),
`503 service_unavailable` when it carries a `Retry-After`, a 429/503 with no problem body at all (an
edge throttle in front of the API can answer in plain text), and transport failures. Never
`quota_exceeded`. Without a `Retry-After` the wait is full jitter over `200ms · 2^n`, capped at 5 s.

A **write** is retried only when an `Idempotency-Key` rides with it, which in the default `Auto` mode is
always: the SDK stamps a fresh UUID on every `POST`/`PATCH`/`PUT`/`DELETE` (never on a `/q` twin - that
is a read), and the SAME key is reused across the retry, so the server replays its stored answer instead
of executing twice. Some operations require the header; the auto key satisfies them, and passing one
explicitly always wins:

```csharp
await client.Monitors.BulkCreateMonitorAsync(request, idempotency_Key: myOwnKey);
```

## Response metadata

The generated methods return the body. Open a capture scope for what rode alongside it:

```csharp
using var capture = client.CaptureResponses();
var page = await client.Monitors.ListMonitorAsync(limit: 50);

var meta = capture.Last!;
Console.WriteLine($"{meta.RequestId} attempts={meta.Attempts} replayed={meta.IdempotencyReplayed}");
Console.WriteLine(meta.RateLimit);   // policy=account;q=1000;w=60 limit=1000 remaining=997 reset=42
```

`RateLimit.Policy` may be the literal `none` (`Unmetered`), in which case the numeric members are
absent rather than zero.

## Paging

Lists are cursor-paginated: `{ data, nextCursor, hasMore }`. Cursors are opaque - never build, parse or
reorder one, and never change the sort mid-walk.

```csharp
await foreach (var monitor in Pagination.PaginateAsync<MonitorPage, MonitorView>(
    (cursor, ct) => client.Monitors.ListMonitorAsync(limit: 200, cursor: cursor, cancellationToken: ct)))
{
    Console.WriteLine(monitor.Url);
}
```

`PagesAsync` yields the envelopes instead, keeping `SyncCursor`, `Count` and `Summary` reachable. Every
`*Page` type also implements `IPageEnvelope<T>` if you would rather walk it yourself.

## Jobs

Bulk mutations answer `202 { jobId, accepted }` and finish asynchronously.

```csharp
var accepted = await client.Monitors.BulkCreateMonitorAsync(request);
var job = await client.WaitForJobAsync(accepted.JobId);

if (JobStateInfo.IsPartial(job.State))          // partial is a SUCCESS with some failed items
{
    await foreach (var item in client.JobResultsAsync(accepted.JobId))
        if (item.Status == JobItemStatuses.Failed) Console.WriteLine($"row {item.Index}: {item.Error}");
}
```

`WaitForJobAsync` paces itself with the `Retry-After` on every non-terminal poll and returns on
`succeeded`, `partial`, `failed` and `cancelled`. It also returns an `interrupted` job - which is *not*
terminal: the server running it died, and only you can decide whether to
`client.Jobs.ResumeJobAsync(jobId)`.

## Instant checks

```csharp
var result = await client.RunCheckAsync(
    new IcCreateRequest { Url = "https://example.com", Type = "http", Pools = new[] { "allworld" } },
    new RunCheckOptions { OnPoll = r => Console.WriteLine($"{r.Events?.Count ?? 0} reports so far") });
```

`RunCheckAsync` posts the check and then **follows the `resultUrl` the server handed back** until
`state == "done"`, honouring each poll's `retryAfter`. A check is addressed by the pair `(dbId, id)` -
never build that path yourself. The SDK follows `resultUrl` on the configured host only. When the checking pipeline is unavailable the create is refused with
`503 service_unavailable` rather than returning an id that never resolves.

## Webhooks

Every delivery carries both HostTracker's own `HT-Signature` and the Standard Webhooks triple. Verify the
**raw request bytes** before parsing - a re-serialized body no longer matches its signature.

```csharp
app.MapPost("/hooks/hosttracker", async (HttpRequest req) =>
{
    using var buffer = new MemoryStream();
    await req.Body.CopyToAsync(buffer);
    var rawBody = buffer.ToArray();

    var verdict = WebhookSignature.Verify(
        req.Headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value!)),
        rawBody,
        secrets: new[] { currentSecret, previousSecret });   // both, while a rotation is in flight
    if (!verdict.IsValid) return Results.Unauthorized();

    var evt = WebhookEvent.Parse(rawBody);
    if (!seen.Add(evt.Id)) return Results.Ok();              // HT-Delivery is stable across retries
    …
    return Results.Ok();
});
```

Rotation (`PATCH /webhook/{id} {"secret":{"rotate":true}}`) signs with both secrets for 24 hours and puts
two `v1` values in the header - pass both secrets and either match verifies. The tolerance is 300 seconds
by default (`tolerance:`), and `now:` makes a test deterministic. `WebhookScheme.StandardWebhooks` forces
the other scheme; the default `Auto` prefers `HT-Signature` when it is present.

## Timestamps

Unix **seconds** in both directions, everywhere, including inside webhook envelopes - never ISO-8601,
never milliseconds. The wire types keep the integers:

```csharp
DateTimeOffset since = UnixTime.ToDateTimeOffset(monitor.Since);
long from = UnixTime.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-7));
```

(Members whose name ends in `Ms` - a delivery's `latencyMs` - are elapsed milliseconds, not instants.)

## Staying forward compatible

The API adds endpoints, response members and vocabulary values without a version bump, so the SDK is
built not to break on them: unknown response members are kept in each type's `AdditionalProperties`,
every vocabulary is a plain string (with `MonitorTypes`, `MonitorStates`, `JobStates`,
`WebhookEvents`, … as constants for discoverability), and unknown problem codes pass through. A value
the server adds to an open vocabulary arrives as itself rather than as a deserialization failure.

For anything the typed surface cannot express yet - an explicit `null` in a PATCH body ("clear this",
as opposed to an absent member's "leave alone"), or an endpoint newer than your SDK build - there is one
escape hatch that still rides the full pipeline:

```csharp
await client.SendJsonAsync(HttpMethod.Patch, "/account",
    new Dictionary<string, object?> { ["defaultAgentPools"] = null });
```

The path may be rooted (`/account`), relative (`account`) or an absolute `http(s)` URL; a rooted path
keeps a base-URL path prefix. Anything that would resolve to another scheme is refused rather than
dialled - including a `resultUrl` the server hands back.

## Regenerating the client

`src/HostTracker.Sdk/Generated/` is generated by [NSwag](https://github.com/RicoSuter/NSwag) (pinned in
`.config/dotnet-tools.json`) and **committed** - consumers never run the generator. Never hand-edit it;
everything custom lives beside it in `src/HostTracker.Sdk/`.

```bash
./scripts/regen.sh                                    # from the public openapi repo
HT_SPEC=/path/to/openapi-3.0.json ./scripts/regen.sh  # from a local document
```

`scripts/prep-spec.py` normalizes the document first (vocabularies, `oneOf` unions, `format: uri`,
the `Idempotency-Key` header); the transforms and why each one exists are listed at the top of that file.

## Development

```bash
dotnet build HostTracker.Sdk.sln -c Release
dotnet test  HostTracker.Sdk.sln -c Release
```

The live smoke tests are opt-in and read-only apart from one instant check:

```bash
HT_BASE_URL=https://api2.host-tracker.com HT_TOKEN_FILE=/path/to/token \
  dotnet test -c Release --filter "Category=Live"
```

## Licence

MIT - see [LICENSE](LICENSE).
