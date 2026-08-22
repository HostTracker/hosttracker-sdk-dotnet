using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using HostTracker.Sdk.Generated;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    public class InstantCheckTests
    {
        private const string CheckId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

        private static string Accepted(string resultUrl, int retryAfter = 0) => $$"""
        {"id":"{{CheckId}}","dbId":1,"retryAfter":{{retryAfter}},"estimatedDurationSec":20,
         "resultUrl":"{{resultUrl}}","created":1735689600}
        """;

        private static string Result(string state, int? retryAfter) => $$"""
        {"id":"{{CheckId}}","dbId":1,"state":"{{state}}","url":"https://www.host-tracker.com","type":"http",
         "created":1735689600{{(retryAfter is null ? "" : $",\"retryAfter\":{retryAfter}")}},"events":[]}
        """;

        [Fact]
        public async Task Follows_the_relative_resultUrl_the_server_handed_back()
        {
            var (client, handler, delays) = TestClient.Create();
            handler.Json(HttpStatusCode.Accepted, Accepted($"/check/1/{CheckId}", retryAfter: 3));
            handler.Json(HttpStatusCode.OK, Result("running", 2));
            handler.Json(HttpStatusCode.OK, Result("done", null));

            var result = await client.RunCheckAsync(new IcCreateRequest
            {
                Url = "https://www.host-tracker.com",
                Type = "http",
            });

            Assert.Equal(InstantCheckStates.Done, result.State);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal("/check", handler.Requests[0].Uri.AbsolutePath);
            Assert.Equal($"/check/1/{CheckId}", handler.Requests[1].Uri.AbsolutePath);
            Assert.Equal($"/check/1/{CheckId}", handler.Requests[2].Uri.AbsolutePath);
            Assert.Equal(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2) }, delays);
        }

        [Fact]
        public async Task Follows_an_absolute_resultUrl_too()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Accepted, Accepted($"{TestClient.BaseUrl}/check/1/{CheckId}"));
            handler.Json(HttpStatusCode.OK, Result("done", null));

            await client.RunCheckAsync(new IcCreateRequest { Url = "https://example.com", Type = "http" });

            Assert.Equal($"/check/1/{CheckId}", handler.Requests[1].Uri.AbsolutePath);
        }

        [Fact]
        public async Task Follows_a_foreign_origin_resultUrl_on_the_configured_host_only()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Accepted, Accepted($"https://evil.example.test/check/1/{CheckId}?x=1"));
            handler.Json(HttpStatusCode.OK, Result("done", null));

            await client.RunCheckAsync(new IcCreateRequest { Url = "https://example.com", Type = "http" });

            var poll = handler.Requests[1];
            Assert.Equal($"https://api2.example.test/check/1/{CheckId}?x=1", poll.Uri.AbsoluteUri);
            Assert.Equal("Bearer test-token", poll.Header("Authorization"));
            Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "evil.example.test");
        }

        [Fact]
        public async Task Falls_back_to_the_dbId_id_pair_when_no_resultUrl_is_published()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Accepted, $$"""
            {"id":"{{CheckId}}","dbId":7,"retryAfter":0,"estimatedDurationSec":20,"created":1735689600}
            """);
            handler.Json(HttpStatusCode.OK, Result("done", null));

            await client.RunCheckAsync(new IcCreateRequest { Url = "https://example.com", Type = "http" });

            Assert.Equal($"/check/7/{CheckId}", handler.Requests[1].Uri.AbsolutePath);
        }

        [Fact]
        public async Task Reports_each_incremental_poll()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Accepted, Accepted($"/check/1/{CheckId}"));
            handler.Json(HttpStatusCode.OK, Result("running", 1));
            handler.Json(HttpStatusCode.OK, Result("done", null));

            var states = new List<string?>();
            await client.RunCheckAsync(
                new IcCreateRequest { Url = "https://example.com", Type = "http" },
                new RunCheckOptions { OnPoll = r => states.Add(r.State) });

            Assert.Equal(new[] { "running", "done" }, states);
        }

        [Fact]
        public async Task Gives_up_with_a_timeout_when_the_check_never_finishes()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Accepted, Accepted($"/check/1/{CheckId}"));
            handler.Json(HttpStatusCode.OK, Result("running", 1));

            await Assert.ThrowsAsync<TimeoutException>(() => client.RunCheckAsync(
                new IcCreateRequest { Url = "https://example.com", Type = "http" },
                new RunCheckOptions { Timeout = TimeSpan.Zero }));
        }

        [Fact]
        public async Task An_unknown_pool_refusal_surfaces_as_the_problem_it_is()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Problem(HttpStatusCode.UnprocessableEntity, """
            {"status":422,"code":"unknown_pool","title":"Unknown monitoring pool",
             "errors":[{"pointer":"/pools","value":"nowhere"}]}
            """);

            var ex = await Assert.ThrowsAsync<HostTrackerException>(() => client.RunCheckAsync(
                new IcCreateRequest { Url = "https://example.com", Type = "http" }));

            Assert.Equal("unknown_pool", ex.Code);
            Assert.Equal("/pools", Assert.Single(ex.Errors).Pointer);
        }
    }
}
