using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using HostTracker.Sdk.Generated;
using HostTracker.Sdk.Http;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    public class IdempotencyTests
    {
        private const string CreatedMonitor = """
        {"monitor":{"id":"4e49d7a2-4ab5-45e2-b9f8-1d59f505ad45","type":"http","name":"m",
          "url":"https://example.com","state":"up","since":1735689600,"enabled":true,
          "updated":1735689600,"created":1735689600,"openStat":false,"fullLog":false}}
        """;

        private static MonitorWriteRequest NewMonitor() => new MonitorWriteRequest
        {
            Type = MonitorTypes.Http,
            Url = "https://example.com",
            Name = "sdk-smoke",
        };

        [Fact]
        public async Task Auto_mode_stamps_a_key_on_a_write()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Created, CreatedMonitor);

            await client.Monitors.CreateMonitorAsync(NewMonitor());

            var key = Assert.Single(handler.Requests).Header(SdkHeaders.IdempotencyKey);
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.True(Guid.TryParse(key, out _));
        }

        [Fact]
        public async Task The_same_key_rides_every_attempt_of_one_call()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Problem((HttpStatusCode)429,
                """{"status":429,"code":"rate_limited","title":"Slow down"}""", ("Retry-After", "1"));
            handler.Json(HttpStatusCode.Created, CreatedMonitor);

            await client.Monitors.CreateMonitorAsync(NewMonitor());

            Assert.Equal(2, handler.Requests.Count);
            var keys = handler.Requests.Select(r => r.Header(SdkHeaders.IdempotencyKey)).ToArray();
            Assert.NotNull(keys[0]);
            Assert.Equal(keys[0], keys[1]);
            // The retried attempt must carry the body again, not an empty one.
            Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
            Assert.Contains("https://example.com", handler.Requests[1].Body!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Two_calls_get_two_different_keys()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Created, CreatedMonitor);
            handler.Json(HttpStatusCode.Created, CreatedMonitor);

            await client.Monitors.CreateMonitorAsync(NewMonitor());
            await client.Monitors.CreateMonitorAsync(NewMonitor());

            Assert.NotEqual(
                handler.Requests[0].Header(SdkHeaders.IdempotencyKey),
                handler.Requests[1].Header(SdkHeaders.IdempotencyKey));
        }

        [Fact]
        public async Task Off_mode_stamps_nothing()
        {
            var (client, handler, _) = TestClient.Create(o => o.Idempotency = IdempotencyMode.Off);
            handler.Json(HttpStatusCode.Created, CreatedMonitor);

            await client.Monitors.CreateMonitorAsync(NewMonitor());

            Assert.Null(Assert.Single(handler.Requests).Header(SdkHeaders.IdempotencyKey));
        }

        [Fact]
        public async Task A_caller_supplied_key_is_never_replaced()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Created, CreatedMonitor);

            await client.Monitors.CreateMonitorAsync(NewMonitor(), null, "my-own-key");

            Assert.Equal("my-own-key", Assert.Single(handler.Requests).Header(SdkHeaders.IdempotencyKey));
        }

        [Fact]
        public async Task A_read_never_gets_a_key()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, """{"data":[],"nextCursor":null,"hasMore":false}""");

            await client.Monitors.ListMonitorAsync();

            Assert.Null(Assert.Single(handler.Requests).Header(SdkHeaders.IdempotencyKey));
        }

        [Fact]
        public async Task Idempotency_Replayed_is_surfaced_on_the_response_metadata()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Created, CreatedMonitor,
                ("Idempotency-Replayed", "true"),
                ("X-Request-Id", "req_replay"),
                ("Location", "/monitor/4e49d7a2-4ab5-45e2-b9f8-1d59f505ad45"));

            using var capture = client.CaptureResponses();
            await client.Monitors.CreateMonitorAsync(NewMonitor());

            var meta = Assert.Single(capture.All);
            Assert.True(meta.IdempotencyReplayed);
            Assert.Equal(201, meta.StatusCode);
            Assert.Equal("req_replay", meta.RequestId);
            Assert.Equal("/monitor/4e49d7a2-4ab5-45e2-b9f8-1d59f505ad45", meta.Location);
            Assert.Equal(handler.Requests[0].Header(SdkHeaders.IdempotencyKey), meta.IdempotencyKey);
            Assert.Equal(1, meta.Attempts);
        }

        [Fact]
        public async Task Response_metadata_counts_the_attempts_a_retry_took()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Problem((HttpStatusCode)429,
                """{"status":429,"code":"rate_limited"}""", ("Retry-After", "1"));
            handler.Json(HttpStatusCode.OK, """{"data":[],"nextCursor":null,"hasMore":false}""",
                ("RateLimit-Policy", "none"));

            using var capture = client.CaptureResponses();
            await client.Monitors.ListMonitorAsync();

            var meta = Assert.Single(capture.All);
            Assert.Equal(2, meta.Attempts);
            Assert.True(meta.RateLimit!.Unmetered);
            Assert.Null(meta.RateLimit.Limit);
        }
    }
}
