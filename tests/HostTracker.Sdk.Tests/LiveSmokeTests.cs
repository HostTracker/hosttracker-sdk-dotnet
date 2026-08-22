using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HostTracker.Sdk.Generated;
using Xunit;
using Xunit.Abstractions;

namespace HostTracker.Sdk.Tests
{
    /// <summary>
    /// One opt-in pass against a real instance. Read-only apart from the instant check: no monitor,
    /// contact or webhook is written.
    /// </summary>
    [Trait("Category", "Live")]
    public class LiveSmokeTests
    {
        private readonly ITestOutputHelper _output;

        public LiveSmokeTests(ITestOutputHelper output) => _output = output;

        [LiveFact]
        public async Task Account_and_quota_read_back()
        {
            using var client = LiveEnvironment.CreateClient();
            using var capture = client.CaptureResponses();

            var account = await client.Account.GetAccountAsync();
            Assert.NotEqual(Guid.Empty, account.Id);
            _output.WriteLine($"GET /account -> id={account.Id} login={account.Login} " +
                              $"package={account.Package?.Name} requestId={capture.Last?.RequestId}");

            var quota = await client.Account.GetAccountQuotaAsync();
            Assert.NotNull(quota.Scopes);
            _output.WriteLine($"GET /account/quota -> apiEnabled={quota.ApiEnabled} " +
                              $"scopes=[{string.Join(",", quota.Scopes!.Select(s => s.Scope))}] " +
                              $"pools=[{string.Join(",", quota.Pools?.Keys ?? Array.Empty<string>())}] " +
                              $"rateLimit={capture.Last?.RateLimit}");
        }

        [LiveFact]
        public async Task Monitors_list_and_paginate()
        {
            using var client = LiveEnvironment.CreateClient();

            var first = await client.Monitors.ListMonitorAsync(limit: 2);
            _output.WriteLine($"GET /monitor?limit=2 -> {first.Data.Count} rows, hasMore={first.HasMore}, " +
                              $"nextCursor={(first.NextCursor is null ? "null" : "<opaque>")}");
            Assert.True(first.Data.Count <= 2);
            Assert.Equal(first.NextCursor is not null, first.HasMore);

            var pages = 0;
            var rows = 0;
            await foreach (var page in Pagination.PagesAsync<MonitorPage, MonitorView>(
                (cursor, ct) => client.Monitors.ListMonitorAsync(limit: 2, cursor: cursor, cancellationToken: ct)))
            {
                pages++;
                rows += page.Data.Count;
                if (pages == 3) break;
            }
            _output.WriteLine($"paginated {pages} page(s), {rows} row(s)");
            Assert.InRange(pages, 1, 3);
        }

        [LiveFact]
        public async Task A_single_monitor_reads_back_with_its_settings()
        {
            using var client = LiveEnvironment.CreateClient();

            var page = await client.Monitors.ListMonitorAsync(limit: 1);
            if (page.Data.Count == 0)
            {
                _output.WriteLine("account has no monitors - nothing to expand");
                return;
            }

            var id = page.Data.First().Id;
            var monitor = await client.Monitors.GetMonitorAsync(id, expand: new[] { "settings" });
            Assert.Equal(id, monitor.Id);
            _output.WriteLine($"GET /monitor/{id}?expand=settings -> type={monitor.Type} " +
                              $"state={monitor.State} settings={(monitor.Settings is null ? "absent" : "present")}");
        }

        [LiveFact]
        public async Task The_reference_tier_answers_without_a_token()
        {
            using var client = LiveEnvironment.CreateClient(authenticated: false);
            using var capture = client.CaptureResponses();

            var types = await client.MonitorTypes.ListMonitorTypeAsync();
            Assert.NotEmpty(types.Data);
            _output.WriteLine($"GET /monitor/type (anonymous) -> {types.Data.Count} types, " +
                              $"policy={capture.Last?.RateLimit?.Policy}");
        }

        [LiveFact]
        public async Task The_raw_door_dials_a_rooted_path_through_the_real_transport()
        {
            // The mocked tests cannot catch this: on Linux a rooted path can resolve to
            // file:///monitor and die inside HttpClient with NotSupportedException.
            using var client = LiveEnvironment.CreateClient();

            var json = await client.SendJsonAsync(System.Net.Http.HttpMethod.Get, "/monitor?limit=1");

            Assert.True(json.TryGetProperty("data", out var data));
            _output.WriteLine($"raw GET /monitor?limit=1 -> {data.GetArrayLength()} row(s)");
        }

        [LiveFact]
        public async Task An_unknown_id_maps_to_the_not_found_problem()
        {
            using var client = LiveEnvironment.CreateClient();

            var ex = await Assert.ThrowsAsync<HostTrackerException>(
                () => client.Monitors.GetMonitorAsync(Guid.Empty));

            _output.WriteLine($"GET /monitor/{Guid.Empty} -> {ex.StatusCode} {ex.Code} " +
                              $"requestId={ex.RequestId} title={ex.Title}");
            Assert.Equal(404, ex.StatusCode);
            Assert.Equal(ProblemCodes.NotFound, ex.Code);
        }

        [LiveFact]
        public async Task An_instant_check_runs_to_done()
        {
            using var client = LiveEnvironment.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var watch = Stopwatch.StartNew();

            var states = new List<string?>();
            IcResultView result;
            try
            {
                result = await client.RunCheckAsync(
                    new IcCreateRequest { Url = "https://www.host-tracker.com", Type = "http" },
                    new RunCheckOptions
                    {
                        Timeout = TimeSpan.FromSeconds(85),
                        OnPoll = r => states.Add(r.State),
                    },
                    cts.Token);
            }
            catch (HostTrackerException ex) when (
                ex.IsCode(ProblemCodes.ServiceUnavailable) || ex.IsCode(ProblemCodes.UpstreamError))
            {
                // A stopped checking pipeline is a fact about the instance, not an SDK defect.
                _output.WriteLine($"POST /check refused: {ex.StatusCode} {ex.Code} - {ex.Detail ?? ex.Title}. " +
                                  "The instant-check pipeline is not running on this instance; " +
                                  "treating as environment, not a failure.");
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _output.WriteLine($"POST /check did not finish in 90s (polls: {string.Join(",", states)}).");
                throw;
            }

            _output.WriteLine($"POST /check -> {result.DbId}/{result.Id} state={result.State} " +
                              $"events={result.Events?.Count ?? 0} in {watch.Elapsed.TotalSeconds:F1}s " +
                              $"(polls: {string.Join(",", states)})");
            Assert.Equal(InstantCheckStates.Done, result.State);
        }
    }
}
