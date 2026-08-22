using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using HostTracker.Sdk.Http;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    public class RetryTests
    {
        private const string EmptyMonitorPage = """{"data":[],"nextCursor":null,"hasMore":false}""";

        private static string RateLimited(int retryAfterSeconds) => $$"""
        {"status":429,"code":"rate_limited","title":"Too many requests",
         "errors":[{"limit":600,"window":60,"retryAfter":{{retryAfterSeconds}}}]}
        """;

        [Fact]
        public async Task Retries_429_rate_limited_and_honours_Retry_After()
        {
            var (client, handler, delays) = TestClient.Create();
            handler.Problem((HttpStatusCode)429, RateLimited(7), ("Retry-After", "7"));
            handler.Json(HttpStatusCode.OK, EmptyMonitorPage);

            var page = await client.Monitors.ListMonitorAsync();

            Assert.Empty(page.Data);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delays));
        }

        [Fact]
        public async Task Never_retries_429_quota_exceeded()
        {
            var (client, handler, delays) = TestClient.Create();
            handler.Problem((HttpStatusCode)429,
                """{"status":429,"code":"quota_exceeded","title":"Quota spent"}""",
                ("Retry-After", "60"));

            var ex = await Assert.ThrowsAsync<HostTrackerException>(() => client.Monitors.ListMonitorAsync());

            Assert.Equal(ProblemCodes.QuotaExceeded, ex.Code);
            Assert.Single(handler.Requests);
            Assert.Empty(delays);
        }

        [Fact]
        public async Task Retries_503_only_when_it_carries_a_Retry_After()
        {
            var (withHeader, handlerA, delaysA) = TestClient.Create();
            handlerA.Problem(HttpStatusCode.ServiceUnavailable,
                """{"status":503,"code":"service_unavailable","title":"Down for a moment"}""",
                ("Retry-After", "3"));
            handlerA.Json(HttpStatusCode.OK, EmptyMonitorPage);
            await withHeader.Monitors.ListMonitorAsync();
            Assert.Equal(2, handlerA.Requests.Count);
            Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(delaysA));

            var (withoutHeader, handlerB, delaysB) = TestClient.Create();
            handlerB.Problem(HttpStatusCode.ServiceUnavailable,
                """{"status":503,"code":"service_unavailable","title":"Down for a moment"}""");
            await Assert.ThrowsAsync<HostTrackerException>(() => withoutHeader.Monitors.ListMonitorAsync());
            Assert.Single(handlerB.Requests);
            Assert.Empty(delaysB);
        }

        [Fact]
        public async Task Caps_a_long_Retry_After_at_MaxRetryDelay()
        {
            var (client, handler, delays) = TestClient.Create(o => o.MaxRetryDelay = TimeSpan.FromSeconds(60));
            handler.Problem((HttpStatusCode)429, RateLimited(3600), ("Retry-After", "3600"));
            handler.Json(HttpStatusCode.OK, EmptyMonitorPage);

            await client.Monitors.ListMonitorAsync();

            Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(delays));
        }

        [Fact]
        public async Task Stops_after_MaxRetries_and_surfaces_the_last_failure()
        {
            var (client, handler, delays) = TestClient.Create(o => o.MaxRetries = 2);
            for (var i = 0; i < 3; i++)
                handler.Problem((HttpStatusCode)429, RateLimited(1), ("Retry-After", "1"));

            var ex = await Assert.ThrowsAsync<HostTrackerException>(() => client.Monitors.ListMonitorAsync());

            Assert.Equal(ProblemCodes.RateLimited, ex.Code);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(2, delays.Count);
        }

        [Fact]
        public async Task Retries_a_transport_failure_on_a_read()
        {
            var (client, handler, delays) = TestClient.Create();
            handler.Throws(new System.Net.Http.HttpRequestException("connection reset"));
            handler.Json(HttpStatusCode.OK, EmptyMonitorPage);

            await client.Monitors.ListMonitorAsync();

            Assert.Equal(2, handler.Requests.Count);
            Assert.Single(delays);
        }

        [Fact]
        public async Task Does_not_retry_an_unkeyed_write()
        {
            var (client, handler, delays) = TestClient.Create(o => o.Idempotency = IdempotencyMode.Off);
            handler.Problem((HttpStatusCode)429, RateLimited(1), ("Retry-After", "1"));

            await Assert.ThrowsAsync<HostTrackerException>(
                () => client.Monitors.CreateMonitorAsync(new Generated.MonitorWriteRequest
                {
                    Type = MonitorTypes.Http,
                    Url = "https://example.com",
                }));

            Assert.Single(handler.Requests);
            Assert.Empty(delays);
        }

        [Fact]
        public async Task Retries_a_429_that_carries_no_problem_code_at_all()
        {
            // An edge throttle in front of the API can answer in plain text, not problem+json.
            var (client, handler, delays) = TestClient.Create();
            handler.Raw((HttpStatusCode)429, "API calls quota exceeded!", "text/plain", ("Retry-After", "4"));
            handler.Json(HttpStatusCode.OK, EmptyMonitorPage);

            await client.Monitors.ListMonitorAsync();

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(TimeSpan.FromSeconds(4), Assert.Single(delays));
        }

        [Fact]
        public async Task Does_not_retry_a_429_carrying_some_other_code()
        {
            var (client, handler, delays) = TestClient.Create();
            handler.Problem((HttpStatusCode)429,
                """{"status":429,"code":"some_other_reason"}""", ("Retry-After", "1"));

            var ex = await Assert.ThrowsAsync<HostTrackerException>(() => client.Monitors.ListMonitorAsync());

            Assert.Equal("some_other_reason", ex.Code);
            Assert.Single(handler.Requests);
            Assert.Empty(delays);
        }

        [Fact]
        public async Task A_429_without_a_Retry_After_backs_off_within_the_jitter_window()
        {
            var (client, handler, delays) = TestClient.Create();
            handler.Problem((HttpStatusCode)429, RateLimited(1));
            handler.Json(HttpStatusCode.OK, EmptyMonitorPage);

            await client.Monitors.ListMonitorAsync();

            // Full jitter over rand(0, min(5s, 200ms * 2^0)).
            Assert.InRange(Assert.Single(delays), TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
        }

        [Fact]
        public async Task Transport_backoff_stays_inside_the_five_second_ceiling()
        {
            var (client, handler, delays) = TestClient.Create(o => o.MaxRetries = 6);
            for (var i = 0; i < 6; i++)
                handler.Throws(new System.Net.Http.HttpRequestException("connection reset"));
            handler.Json(HttpStatusCode.OK, EmptyMonitorPage);

            await client.Monitors.ListMonitorAsync();

            Assert.Equal(6, delays.Count);
            Assert.All(delays, d => Assert.InRange(d, TimeSpan.Zero, TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task An_attempt_that_outruns_the_timeout_maps_to_network_error()
        {
            var (client, handler, _) = TestClient.Create(o =>
            {
                o.MaxRetries = 0;
                o.Timeout = TimeSpan.FromMilliseconds(50);
            });
            handler.Slow(TimeSpan.FromSeconds(5), HttpStatusCode.OK, EmptyMonitorPage);

            var ex = await Assert.ThrowsAsync<HostTrackerException>(() => client.Monitors.ListMonitorAsync());

            Assert.Equal(ProblemCodes.NetworkError, ex.Code);
            Assert.Equal(0, ex.StatusCode);
        }

        [Fact]
        public async Task The_timeout_bounds_each_attempt_not_the_whole_retry_ladder()
        {
            // Three attempts of ~60ms each under a 150ms budget: a per-call timeout would kill this.
            var (client, handler, _) = TestClient.Create(o =>
            {
                o.MaxRetries = 2;
                o.Timeout = TimeSpan.FromMilliseconds(150);
            });
            handler.Slow(TimeSpan.FromMilliseconds(60), (HttpStatusCode)429, """{"code":"rate_limited"}""");
            handler.Slow(TimeSpan.FromMilliseconds(60), (HttpStatusCode)429, """{"code":"rate_limited"}""");
            handler.Slow(TimeSpan.FromMilliseconds(60), HttpStatusCode.OK, EmptyMonitorPage);

            var page = await client.Monitors.ListMonitorAsync();

            Assert.Empty(page.Data);
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public async Task Query_twins_are_retried_like_the_reads_they_are()
        {
            var (client, handler, _) = TestClient.Create(o => o.Idempotency = IdempotencyMode.Off);
            handler.Problem((HttpStatusCode)429, RateLimited(1), ("Retry-After", "1"));
            handler.Json(HttpStatusCode.OK, EmptyMonitorPage);

            await client.Monitors.QueryMonitorAsync(new Generated.MonitorQueryRequest { Limit = 2 });

            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, r => Assert.EndsWith("/monitor/q", r.Uri.AbsolutePath, StringComparison.Ordinal));
            Assert.All(handler.Requests, r => Assert.Null(r.Header(SdkHeaders.IdempotencyKey)));
        }
    }
}
