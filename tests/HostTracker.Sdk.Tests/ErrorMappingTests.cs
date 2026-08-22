using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    public class ErrorMappingTests
    {
        private const string ValidationProblem = """
        {
          "type": "https://api2.host-tracker.com/problems/invalid_interval",
          "title": "The check interval is not one this account may use.",
          "status": 422,
          "code": "invalid_interval",
          "detail": "7 is not an allowed interval.",
          "instance": "/monitor/4e49d7a2-4ab5-45e2-b9f8-1d59f505ad45",
          "errors": [ { "pointer": "/interval", "reason": "not_allowed", "value": 7, "allowed": [1,5,15,30,60] } ]
        }
        """;

        [Fact]
        public async Task ProblemJson_maps_every_member_onto_the_one_exception_type()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Problem(HttpStatusCode.UnprocessableEntity, ValidationProblem,
                ("X-Request-Id", "req_abc123"),
                ("RateLimit-Policy", "account;q=1000;w=60"),
                ("RateLimit-Limit", "1000"),
                ("RateLimit-Remaining", "997"),
                ("RateLimit-Reset", "42"));

            var ex = await Assert.ThrowsAsync<HostTrackerException>(
                () => client.Monitors.GetMonitorAsync(Guid.NewGuid()));

            Assert.Equal(422, ex.StatusCode);
            Assert.Equal("invalid_interval", ex.Code);
            Assert.True(ex.IsCode("invalid_interval"));
            Assert.Equal("https://api2.host-tracker.com/problems/invalid_interval", ex.Type);
            Assert.Equal("The check interval is not one this account may use.", ex.Title);
            Assert.Equal("7 is not an allowed interval.", ex.Detail);
            Assert.Equal("/monitor/4e49d7a2-4ab5-45e2-b9f8-1d59f505ad45", ex.Instance);
            Assert.Equal("req_abc123", ex.RequestId);

            var error = Assert.Single(ex.Errors);
            Assert.Equal("/interval", error.Pointer);
            Assert.Equal("not_allowed", error.Reason);
            Assert.Equal(7, error.Extensions["value"].GetInt32());
            Assert.Equal(JsonValueKind.Array, error.Extensions["allowed"].ValueKind);

            Assert.NotNull(ex.RateLimit);
            Assert.Equal("account;q=1000;w=60", ex.RateLimit!.Policy);
            Assert.Equal(1000, ex.RateLimit.Limit);
            Assert.Equal(997, ex.RateLimit.Remaining);
            Assert.Equal(42, ex.RateLimit.Reset);
            Assert.False(ex.RateLimit.Unmetered);
        }

        [Fact]
        public async Task Non_json_502_from_a_proxy_maps_to_http_error()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Raw(HttpStatusCode.BadGateway,
                "<html><head><title>502 Bad Gateway</title></head><body>nginx</body></html>", "text/html");

            var ex = await Assert.ThrowsAsync<HostTrackerException>(
                () => client.Account.GetAccountAsync());

            Assert.Equal(502, ex.StatusCode);
            Assert.Equal(ProblemCodes.HttpError, ex.Code);
            Assert.Null(ex.Type);
            Assert.Contains("502", ex.Message, StringComparison.Ordinal);
            Assert.Empty(ex.Errors);
        }

        [Fact]
        public async Task Transport_failure_maps_to_network_error_with_the_inner_exception()
        {
            var (client, handler, _) = TestClient.Create(o => o.MaxRetries = 0);
            handler.Throws(new System.Net.Http.HttpRequestException("Name or service not known"));

            var ex = await Assert.ThrowsAsync<HostTrackerException>(
                () => client.Account.GetAccountAsync());

            Assert.Equal(0, ex.StatusCode);
            Assert.Equal(ProblemCodes.NetworkError, ex.Code);
            Assert.IsType<System.Net.Http.HttpRequestException>(ex.InnerException);
        }

        [Fact]
        public async Task Rate_limited_and_quota_exceeded_share_429_and_stay_distinct()
        {
            var (client, handler, _) = TestClient.Create(o => o.MaxRetries = 0);
            handler.Problem((HttpStatusCode)429,
                """
                {"status":429,"code":"quota_exceeded","title":"Quota spent",
                 "errors":[{"limit":1000,"remaining":0,"resetAt":1735689600}]}
                """);

            var ex = await Assert.ThrowsAsync<HostTrackerException>(
                () => client.Account.GetAccountAsync());

            Assert.Equal(429, ex.StatusCode);
            Assert.Equal(ProblemCodes.QuotaExceeded, ex.Code);
            Assert.False(ex.IsCode(ProblemCodes.RateLimited));
        }

        [Fact]
        public async Task Unknown_problem_code_passes_through_as_a_string()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Problem(HttpStatusCode.Conflict,
                """{"status":409,"code":"a_code_this_sdk_predates","title":"Something new"}""");

            var ex = await Assert.ThrowsAsync<HostTrackerException>(
                () => client.Account.GetAccountAsync());

            Assert.Equal("a_code_this_sdk_predates", ex.Code);
        }

        [Theory]
        [InlineData("30", 30)]
        [InlineData("0", 0)]
        [InlineData("", null)]
        [InlineData("not-a-date", null)]
        public void RetryAfter_reads_delta_seconds(string raw, int? expectedSeconds)
        {
            var parsed = HostTrackerException.ParseRetryAfter(raw);
            Assert.Equal(expectedSeconds is null ? (TimeSpan?)null : TimeSpan.FromSeconds(expectedSeconds.Value), parsed);
        }

        [Fact]
        public void RetryAfter_reads_an_http_date()
        {
            var when = DateTimeOffset.UtcNow.AddSeconds(45);
            var parsed = HostTrackerException.ParseRetryAfter(when.ToString("R"));
            Assert.NotNull(parsed);
            Assert.InRange(parsed!.Value.TotalSeconds, 30, 50);
        }
    }
}
