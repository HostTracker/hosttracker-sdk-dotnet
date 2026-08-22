using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    public class JobTests
    {
        private static readonly Guid JobId = Guid.Parse("8f14e45f-ceea-467a-9575-1e0c1a2b3c4d");

        private static string Job(string state) =>
            $$"""
            {"id":"{{JobId}}","kind":"monitor.bulkCreate","scope":"monitor","state":"{{state}}",
             "cancelRequested":false,"created":1735689600,"expiresAt":1735776000,"resumedCount":0,
             "hasMore":false,"nextCursor":null}
            """;

        private static string JobWithResults(bool hasMore, string nextCursor, string results) =>
            "{\"id\":\"" + JobId + "\",\"state\":\"partial\",\"cancelRequested\":false,\"created\":1," +
            "\"expiresAt\":2,\"resumedCount\":0,\"hasMore\":" + (hasMore ? "true" : "false") +
            ",\"nextCursor\":" + nextCursor + ",\"results\":[" + results + "]}";

        [Theory]
        [InlineData("succeeded")]
        [InlineData("partial")]
        [InlineData("failed")]
        [InlineData("cancelled")]
        public async Task Returns_on_every_terminal_state(string state)
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Job(state));

            var job = await client.WaitForJobAsync(JobId);

            Assert.Equal(state, job.State);
            Assert.True(JobStateInfo.IsTerminal(job.State));
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task Partial_is_a_success_not_an_error()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Job("partial"));

            var job = await client.WaitForJobAsync(JobId);

            Assert.True(JobStateInfo.IsPartial(job.State));
            Assert.True(JobStateInfo.IsTerminal(job.State));
        }

        [Fact]
        public async Task Interrupted_is_returned_flagged_and_is_not_terminal()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Job("interrupted"));

            var job = await client.WaitForJobAsync(JobId);

            Assert.Equal(JobStates.Interrupted, job.State);
            Assert.True(JobStateInfo.IsInterrupted(job.State));
            Assert.False(JobStateInfo.IsTerminal(job.State));
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task Paces_the_poll_loop_with_each_answers_Retry_After()
        {
            var (client, handler, delays) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Job("queued"), ("Retry-After", "5"));
            handler.Json(HttpStatusCode.OK, Job("running"), ("Retry-After", "2"));
            handler.Json(HttpStatusCode.OK, Job("succeeded"));

            var job = await client.WaitForJobAsync(JobId);

            Assert.Equal(JobStates.Succeeded, job.State);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2) }, delays);
        }

        [Fact]
        public async Task Falls_back_to_the_poll_interval_when_no_Retry_After_rides_along()
        {
            var (client, handler, delays) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Job("running"));
            handler.Json(HttpStatusCode.OK, Job("succeeded"));

            await client.WaitForJobAsync(JobId, new JobWaitOptions { PollInterval = TimeSpan.FromSeconds(4) });

            Assert.Equal(TimeSpan.FromSeconds(4), Assert.Single(delays));
        }

        [Fact]
        public async Task Caps_a_long_Retry_After_at_MaxPollInterval()
        {
            var (client, handler, delays) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Job("running"), ("Retry-After", "600"));
            handler.Json(HttpStatusCode.OK, Job("succeeded"));

            await client.WaitForJobAsync(JobId, new JobWaitOptions { MaxPollInterval = TimeSpan.FromSeconds(30) });

            Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(delays));
        }

        [Fact]
        public async Task Gives_up_with_a_timeout_when_the_job_never_settles()
        {
            var (client, handler, _) = TestClient.Create();
            for (var i = 0; i < 4; i++) handler.Json(HttpStatusCode.OK, Job("running"));

            await Assert.ThrowsAsync<TimeoutException>(
                () => client.WaitForJobAsync(JobId, new JobWaitOptions { Timeout = TimeSpan.Zero }));

            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task Reports_progress_through_the_OnPoll_hook()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Job("running"));
            handler.Json(HttpStatusCode.OK, Job("succeeded"));

            var seen = new List<string?>();
            await client.WaitForJobAsync(JobId, new JobWaitOptions { OnPoll = j => seen.Add(j.State) });

            Assert.Equal(new[] { "running", "succeeded" }, seen);
        }

        [Fact]
        public async Task Walks_the_per_item_receipts_across_pages()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, JobWithResults(
                hasMore: true, nextCursor: "\"c2\"",
                results: """{"index":0,"status":"created","entityId":"4e49d7a2-4ab5-45e2-b9f8-1d59f505ad45"}"""));
            handler.Json(HttpStatusCode.OK, JobWithResults(
                hasMore: false, nextCursor: "null",
                results: """{"index":1,"status":"failed","error":{"code":"duplicate_monitor","status":409}}"""));

            var statuses = new List<string?>();
            await foreach (var item in client.JobResultsAsync(JobId)) statuses.Add(item.Status);

            Assert.Equal(new[] { JobItemStatuses.Created, JobItemStatuses.Failed }, statuses);
        }
    }
}
