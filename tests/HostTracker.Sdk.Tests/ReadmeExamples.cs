using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using HostTracker.Sdk.Generated;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    /// <summary>
    /// The README's snippets, compiled but never executed, so that a regeneration renaming a type
    /// or a member breaks the build instead of leaving the docs stale.
    /// </summary>
    public class ReadmeExamples
    {
        [Fact]
        public void Every_readme_snippet_compiles() => Assert.NotNull(typeof(HostTrackerClient));

        private static async Task QuickStart(string token, byte[] rawBody, string webhookSecret)
        {
            using var client = new HostTrackerClient(token);

            var page = await client.Monitors.ListMonitorAsync(limit: 50);
            foreach (var m in page.Data)
                Console.WriteLine($"{m.Name,-30} {m.State} since {UnixTime.ToDateTimeOffset(m.Since):u}");

            var created = await client.Monitors.CreateMonitorAsync(new MonitorWriteRequest
            {
                Type = MonitorTypes.Http,
                Url = "https://example.com",
                Name = "Marketing site",
                Interval = 5,
                Locations = new MonitorLocations { Pools = new[] { "allworld" } },
            });
            Console.WriteLine($"created {created.Id}");

            var result = await client.RunCheckAsync(new IcCreateRequest
            {
                Url = "https://example.com",
                Type = "http",
            });
            Console.WriteLine($"{result.State} with {result.Events?.Count ?? 0} location report(s)");

            var verdict = WebhookSignature.Verify(
                new Dictionary<string, string>(), rawBody, secrets: new[] { webhookSecret });
            verdict.EnsureValid();
            var evt = WebhookEvent.Parse(rawBody);
            if (evt.Event == WebhookEvents.MonitorDown)
                Console.WriteLine(evt.DataAs<WebhookMonitorAlert>()!.Monitor!.Url);
        }

        private static async Task Operations(HostTrackerClient client, Guid webhookId)
        {
            await client.Monitors.ListMonitorAsync(
                state: new[] { MonitorStates.Down }, expand: new[] { "lastIncident" });
            await client.Contacts.CreateContactAsync(new ContactWriteRequest
            {
                Type = ContactTypes.Email,
                Address = "ops@example.com",
            });
            await client.Webhooks.TestWebhookAsync(webhookId);
            await client.Monitors.QueryMonitorAsync(new MonitorQueryRequest { Limit = 10 });
        }

        private static async Task Errors(HostTrackerClient client, MonitorWriteRequest request)
        {
            try
            {
                await client.Monitors.CreateMonitorAsync(request);
            }
            catch (HostTrackerException ex) when (ex.IsCode(ProblemCodes.DuplicateMonitor))
            {
                var existing = ex.Errors.FirstOrDefault()?.Extensions["existingId"].GetString();
                Console.WriteLine(existing);
            }
            catch (HostTrackerException ex) when (ex.IsCode(ProblemCodes.QuotaExceeded))
            {
            }
            catch (HostTrackerException ex)
            {
                Console.Error.WriteLine($"{ex.StatusCode} {ex.Code}: {ex.Detail} (request {ex.RequestId})");
                foreach (var e in ex.Errors) Console.Error.WriteLine($"  {e.Pointer}: {e.Reason}");
            }
        }

        private static async Task Metadata(HostTrackerClient client)
        {
            using var capture = client.CaptureResponses();
            var page = await client.Monitors.ListMonitorAsync(limit: 50);

            var meta = capture.Last!;
            Console.WriteLine($"{meta.RequestId} attempts={meta.Attempts} replayed={meta.IdempotencyReplayed}");
            Console.WriteLine(meta.RateLimit);
            Console.WriteLine(page.Data.Count);
        }

        private static async Task Paging(HostTrackerClient client)
        {
            await foreach (var monitor in Pagination.PaginateAsync<MonitorPage, MonitorView>(
                (cursor, ct) => client.Monitors.ListMonitorAsync(limit: 200, cursor: cursor, cancellationToken: ct)))
            {
                Console.WriteLine(monitor.Url);
            }
        }

        private static async Task Jobs(HostTrackerClient client, MonitorBulkCreateRequest request)
        {
            var accepted = await client.Monitors.BulkCreateMonitorAsync(request);
            var job = await client.WaitForJobAsync(accepted.JobId);

            if (JobStateInfo.IsPartial(job.State))
            {
                await foreach (var item in client.JobResultsAsync(accepted.JobId))
                    if (item.Status == JobItemStatuses.Failed)
                        Console.WriteLine($"row {item.Index}: {item.Error}");
            }

            await client.Jobs.ResumeJobAsync(accepted.JobId);
            await client.Monitors.BulkCreateMonitorAsync(request, idempotency_Key: "my-own-key");
        }

        private static async Task InstantCheck(HostTrackerClient client)
        {
            await client.RunCheckAsync(
                new IcCreateRequest { Url = "https://example.com", Type = "http", Pools = new[] { "allworld" } },
                new RunCheckOptions { OnPoll = r => Console.WriteLine($"{r.Events?.Count ?? 0} reports so far") });
        }

        private static void Webhooks(
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
            byte[] rawBody,
            string currentSecret,
            string previousSecret,
            HashSet<string> seen)
        {
            var verdict = WebhookSignature.Verify(headers, rawBody, secrets: new[] { currentSecret, previousSecret });
            if (!verdict.IsValid) return;

            var evt = WebhookEvent.Parse(rawBody);
            if (!seen.Add(evt.Id)) return;
        }

        private static void Timestamps(MonitorView monitor)
        {
            DateTimeOffset since = UnixTime.ToDateTimeOffset(monitor.Since);
            long from = UnixTime.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-7));
            Console.WriteLine($"{since} {from}");
        }

        private static async Task RawDoor(HostTrackerClient client)
        {
            await client.SendJsonAsync(HttpMethod.Patch, "/account",
                new Dictionary<string, object?> { ["defaultAgentPools"] = null });
        }

        private static void Configuration(System.Net.IWebProxy myProxy)
        {
            using var client = new HostTrackerClient(new HostTrackerOptions
            {
                Token = "…",
                BaseUrl = "https://api2.host-tracker.com",
                Timeout = TimeSpan.FromSeconds(30),
                MaxRetries = 2,
                MaxRetryDelay = TimeSpan.FromSeconds(60),
                Idempotency = IdempotencyMode.Auto,
                UserAgentSuffix = "acme-deploy/2.1",
                Handler = new HttpClientHandler { Proxy = myProxy },
            });
        }
    }
}
