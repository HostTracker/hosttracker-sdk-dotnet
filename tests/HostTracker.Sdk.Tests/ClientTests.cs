using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using HostTracker.Sdk.Http;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    public class ClientTests
    {
        private const string EmptyPage = """{"data":[],"nextCursor":null,"hasMore":false}""";

        [Fact]
        public void Defaults_to_the_production_host()
        {
            using var client = new HostTrackerClient("token");
            Assert.Equal(new Uri(HostTrackerOptions.DefaultBaseUrl), client.BaseUrl);
        }

        [Fact]
        public async Task Sends_the_bearer_token_and_the_SDK_user_agent()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, EmptyPage);

            await client.Monitors.ListMonitorAsync();

            var request = Assert.Single(handler.Requests);
            Assert.Equal("Bearer test-token", request.Header("Authorization"));
            var agent = request.Header("User-Agent")!;
            Assert.StartsWith("hosttracker-sdk-dotnet/", agent, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Appends_the_callers_own_product_token_to_the_user_agent()
        {
            var (client, handler, _) = TestClient.Create(o => o.UserAgentSuffix = "acme-deploy/2.1");
            handler.Json(HttpStatusCode.OK, EmptyPage);

            await client.Monitors.ListMonitorAsync();

            // HttpHeaders splits a User-Agent into its product tokens, so read them all back.
            var products = Assert.Single(handler.Requests).Headers["User-Agent"];
            Assert.Equal(new[] { "hosttracker-sdk-dotnet/0.2.0", "acme-deploy/2.1" }, products);
        }

        [Fact]
        public async Task Without_a_token_it_sends_no_Authorization_header_at_all()
        {
            var (client, handler, _) = TestClient.Create(o => o.Token = null);
            handler.Json(HttpStatusCode.OK, EmptyPage);

            await client.MonitorTypes.ListMonitorTypeAsync();

            Assert.Null(Assert.Single(handler.Requests).Header("Authorization"));
        }

        [Fact]
        public async Task The_token_rides_the_reference_tier_operations_too()
        {
            // The reference-tier operations declare `security: []`, but some are auth-aware: a token
            // adds account limits and moves the call to the per-account bucket.
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, EmptyPage);
            handler.Json(HttpStatusCode.OK, EmptyPage);

            await client.MonitorTypes.ListMonitorTypeAsync();
            await client.MonitoringLocations.ListAgentAsync();

            Assert.All(handler.Requests, r => Assert.Equal("Bearer test-token", r.Header("Authorization")));
        }

        [Fact]
        public async Task Honours_a_base_url_that_carries_a_path_prefix()
        {
            var handler = new StubHandler();
            handler.Json(HttpStatusCode.OK, EmptyPage);
            using var client = new HostTrackerClient(new HostTrackerOptions
            {
                Token = "t",
                BaseUrl = "https://www.host-tracker.com/api2/",
                Handler = handler,
            });

            await client.Monitors.ListMonitorAsync();

            var uri = Assert.Single(handler.Requests).Uri;
            Assert.Equal("www.host-tracker.com", uri.Host);
            Assert.Equal("/api2/monitor", uri.AbsolutePath);
        }

        [Fact]
        public async Task Puts_every_family_on_the_one_pipeline()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, EmptyPage);
            handler.Json(HttpStatusCode.OK, """{"data":[],"nextCursor":null,"hasMore":false}""");
            handler.Json(HttpStatusCode.OK, """{"data":[],"nextCursor":null,"hasMore":false}""");

            await client.Monitors.ListMonitorAsync();
            await client.Webhooks.ListWebhookAsync();
            await client.Jobs.ListJobAsync();

            Assert.Equal(3, handler.Requests.Count);
            Assert.All(handler.Requests, r => Assert.Equal("Bearer test-token", r.Header("Authorization")));
            Assert.Same(client.InstantChecks, client.Checks);
        }

        [Fact]
        public void Refuses_an_unusable_base_url()
        {
            Assert.Throws<ArgumentException>(() =>
                new HostTrackerClient(new HostTrackerOptions { BaseUrl = "not a url" }));
            Assert.Throws<ArgumentException>(() =>
                new HostTrackerClient(new HostTrackerOptions { BaseUrl = "" }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new HostTrackerClient(new HostTrackerOptions { MaxRetries = -1 }));
        }

        [Fact]
        public async Task A_caller_supplied_HttpClient_is_used_as_it_is()
        {
            var handler = new StubHandler();
            handler.Json(HttpStatusCode.OK, EmptyPage);
            using var http = new System.Net.Http.HttpClient(handler)
            {
                BaseAddress = new Uri("https://api2.example.test"),
            };
            using var client = new HostTrackerClient(new HostTrackerOptions { Token = "t", HttpClient = http });

            await client.Monitors.ListMonitorAsync();

            // No SDK handlers were added to it, so no Authorization header either.
            Assert.Null(Assert.Single(handler.Requests).Header("Authorization"));
            Assert.Same(http, client.HttpClient);
        }

        [Fact]
        public async Task Nested_captures_both_see_the_same_answers()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, EmptyPage, ("X-Request-Id", "outer"));
            handler.Json(HttpStatusCode.OK, EmptyPage, ("X-Request-Id", "inner"));

            using var outer = client.CaptureResponses();
            await client.Monitors.ListMonitorAsync();
            using (var inner = client.CaptureResponses())
            {
                await client.Monitors.ListMonitorAsync();
                Assert.Equal("inner", Assert.Single(inner.All).RequestId);
            }

            Assert.Equal(new[] { "outer", "inner" }, outer.All.Select(m => m.RequestId));
        }

        [Fact]
        public void Unix_seconds_convert_both_ways()
        {
            var instant = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
            Assert.Equal(1735689600, UnixTime.FromDateTimeOffset(instant));
            Assert.Equal(instant, UnixTime.ToDateTimeOffset(1735689600));
            Assert.Null(UnixTime.ToDateTimeOffset((long?)null));
            Assert.Null(UnixTime.FromDateTimeOffset((DateTimeOffset?)null));
            Assert.Equal(DateTimeKind.Utc, UnixTime.ToUtcDateTime(1735689600).Kind);
            Assert.Equal(1735689600, UnixTime.FromDateTime(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void The_generated_vocabularies_carry_the_documents_own_tokens()
        {
            Assert.Equal(14, MonitorTypes.All.Count);
            Assert.Contains(MonitorTypes.Http, MonitorTypes.All);
            Assert.Equal("domainExp", MonitorTypes.DomainExp);
            Assert.Equal(new[] { "up", "down", "paused", "maintenance" }, MonitorStates.All);
            Assert.Equal("monitor.repeatedlyDown", WebhookEvents.MonitorRepeatedlyDown);
            Assert.Equal(15, WebhookEvents.All.Count);
            Assert.Equal(7, JobStates.All.Count);
        }

        [Fact]
        public void A_write_body_omits_what_the_caller_never_set()
        {
            var request = new Generated.MonitorPatchRequest { Name = "renamed" };
            var json = System.Text.Json.JsonSerializer.Serialize(request, SdkJson.Default);

            Assert.Equal("""{"name":"renamed"}""", json);
        }

        [Fact]
        public async Task An_explicit_null_goes_through_the_raw_door()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, """{"id":"4e49d7a2-4ab5-45e2-b9f8-1d59f505ad45"}""");

            await client.SendJsonAsync(
                System.Net.Http.HttpMethod.Patch,
                "/account",
                new System.Collections.Generic.Dictionary<string, object?> { ["defaultAgentPools"] = null });

            var request = Assert.Single(handler.Requests);
            Assert.Equal("PATCH", request.Method);
            Assert.Equal("/account", request.Uri.AbsolutePath);
            Assert.Equal("""{"defaultAgentPools":null}""", request.Body);
            // The raw door still rides the pipeline, so the write is keyed like any other.
            Assert.NotNull(request.Header(SdkHeaders.IdempotencyKey));
        }

        [Fact]
        public async Task The_raw_door_maps_failures_onto_the_same_exception_type()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Problem(HttpStatusCode.NotFound,
                """{"status":404,"code":"not_found","title":"No such monitor"}""");

            var ex = await Assert.ThrowsAsync<HostTrackerException>(() => client.SendJsonAsync(
                System.Net.Http.HttpMethod.Get, "/monitor/00000000-0000-0000-0000-000000000000"));

            Assert.Equal(ProblemCodes.NotFound, ex.Code);
        }
    }
}
