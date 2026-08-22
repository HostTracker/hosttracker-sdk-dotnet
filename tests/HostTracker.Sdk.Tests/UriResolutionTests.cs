using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using HostTracker.Sdk.Generated;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    /// <summary>
    /// Regression cover for the Uri trap that fires on Linux and macOS only: a rooted path
    /// (<c>/flaky/ratelimit</c>) parses as a valid absolute <c>file://</c> URI there, so code that
    /// treats <c>TryCreate(..., UriKind.Absolute)</c> success as "this is a URL" sends the request to
    /// the filesystem. On Windows the same code passes.
    /// </summary>
    public class UriResolutionTests
    {
        private const string Ok = """{"ok":true}""";

        private const string Done = """
        {"id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","dbId":1,"state":"done",
         "url":"https://example.com","type":"http","created":1735689600,"events":[]}
        """;

        private static string Accepted(string resultUrl) => $$"""
        {"id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","dbId":1,"retryAfter":0,
         "estimatedDurationSec":20,"resultUrl":"{{resultUrl}}","created":1735689600}
        """;

        [Fact]
        public void A_rooted_path_really_does_parse_as_an_absolute_file_uri()
        {
            // The premise of every assertion below.
            Assert.True(Uri.TryCreate("/flaky/ratelimit", UriKind.Absolute, out var parsed));
            Assert.Equal(Uri.UriSchemeFile, parsed!.Scheme);
        }

        [Theory]
        [InlineData("/flaky/ratelimit", "https://api2.example.test/flaky/ratelimit")]
        [InlineData("/monitor?limit=2", "https://api2.example.test/monitor?limit=2")]
        [InlineData("monitor", "https://api2.example.test/monitor")]
        [InlineData("https://other.example.test/x", "https://other.example.test/x")]
        [InlineData("http://other.example.test/x", "http://other.example.test/x")]
        [InlineData("HTTPS://Other.Example.Test/x", "https://other.example.test/x")]
        public async Task The_raw_door_resolves_paths_against_the_base_url(string path, string expected)
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Ok);

            await client.SendJsonAsync(HttpMethod.Get, path);

            Assert.Equal(expected, Assert.Single(handler.Requests).Uri.AbsoluteUri);
        }

        [Fact]
        public async Task A_rooted_path_keeps_a_base_url_path_prefix()
        {
            var handler = new StubHandler();
            handler.Json(HttpStatusCode.OK, Ok);
            using var client = new HostTrackerClient(new HostTrackerOptions
            {
                Token = "t",
                BaseUrl = "https://www.host-tracker.com/api2/",
                Handler = handler,
            });

            await client.SendJsonAsync(HttpMethod.Get, "/flaky/ratelimit");

            Assert.Equal("https://www.host-tracker.com/api2/flaky/ratelimit",
                Assert.Single(handler.Requests).Uri.AbsoluteUri);
        }

        [Theory]
        [InlineData("/check/1/3f2504e0-4f89-11d3-9a0c-0305e82c3301",
                    "https://api2.example.test/check/1/3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
        [InlineData("https://api2.example.test/check/1/3f2504e0-4f89-11d3-9a0c-0305e82c3301",
                    "https://api2.example.test/check/1/3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
        public async Task RunCheck_follows_a_rooted_or_absolute_resultUrl(string resultUrl, string expected)
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Accepted, $$"""
            {"id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","dbId":1,"retryAfter":0,
             "estimatedDurationSec":20,"resultUrl":"{{resultUrl}}","created":1735689600}
            """);
            handler.Json(HttpStatusCode.OK, """
            {"id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","dbId":1,"state":"done",
             "url":"https://example.com","type":"http","created":1735689600,"events":[]}
            """);

            await client.RunCheckAsync(new IcCreateRequest { Url = "https://example.com", Type = "http" });

            Assert.Equal(expected, handler.Requests[1].Uri.AbsoluteUri);
        }

        [Theory]
        [InlineData("https://evil.example.test/check/1/3f2504e0-4f89-11d3-9a0c-0305e82c3301",
                    "https://api2.example.test/check/1/3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
        [InlineData("http://evil.example.test:8080/check/1/x?y=2", "https://api2.example.test/check/1/x?y=2")]
        [InlineData("//evil.example.test/check/1/x", "https://api2.example.test/check/1/x")]
        [InlineData("https://evil.example.test", "https://api2.example.test/")]
        public async Task RunCheck_keeps_a_foreign_resultUrl_on_the_configured_origin(
            string resultUrl, string expected)
        {
            // Only the path and query are the server's; the scheme and host stay the client's own,
            // so the bearer token never leaves the configured origin.
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Accepted, Accepted(resultUrl));
            handler.Json(HttpStatusCode.OK, Done);

            await client.RunCheckAsync(new IcCreateRequest { Url = "https://example.com", Type = "http" });

            Assert.Equal(expected, handler.Requests[1].Uri.AbsoluteUri);
            Assert.Equal("Bearer test-token", handler.Requests[1].Header("Authorization"));
        }

        [Fact]
        public async Task A_foreign_resultUrl_keeps_a_base_url_path_prefix()
        {
            var handler = new StubHandler();
            handler.Json(HttpStatusCode.Accepted, Accepted("https://evil.example.test/check/1/x"));
            handler.Json(HttpStatusCode.OK, Done);
            using var client = new HostTrackerClient(new HostTrackerOptions
            {
                Token = "t",
                BaseUrl = "https://www.host-tracker.com/api2/",
                Handler = handler,
                Delay = (_, _) => Task.CompletedTask,
            });

            await client.RunCheckAsync(new IcCreateRequest { Url = "https://example.com", Type = "http" });

            Assert.Equal("https://www.host-tracker.com/api2/check/1/x", handler.Requests[1].Uri.AbsoluteUri);
        }

        [Fact]
        public async Task The_generated_operations_resolve_against_the_base_url_too()
        {
            // They build un-rooted paths ("monitor"), so they never hit the trap; pinned here in case
            // a generator setting change starts emitting rooted ones.
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, """{"data":[],"nextCursor":null,"hasMore":false}""");

            await client.Monitors.ListMonitorAsync(limit: 2);

            var uri = Assert.Single(handler.Requests).Uri;
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("api2.example.test", uri.Host);
            Assert.Equal("/monitor", uri.AbsolutePath);
        }

        [Theory]
        [InlineData("/api2")]
        [InlineData("api2.host-tracker.com")]
        [InlineData("ftp://api2.host-tracker.com")]
        [InlineData("file:///etc/passwd")]
        [InlineData("not a url")]
        [InlineData("")]
        public void A_base_url_that_is_not_an_http_url_is_refused_up_front(string baseUrl)
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                new HostTrackerClient(new HostTrackerOptions { BaseUrl = baseUrl }));
            Assert.Equal("BaseUrl", ex.ParamName);
        }

        [Fact]
        public void The_fallback_branch_would_have_honoured_an_embedded_scheme()
        {
            // The second half of the same trap: `new Uri(base, value)` does not keep a value inside
            // the base when the value carries its own scheme.
            Assert.True(Uri.TryCreate(new Uri("https://api2.example.test"), "file:///etc/passwd", out var combined));
            Assert.Equal(Uri.UriSchemeFile, combined!.Scheme);
        }

        [Theory]
        [InlineData("file:///etc/passwd")]
        [InlineData("ftp://evil.example.test/x")]
        [InlineData("gopher://evil.example.test/x")]
        public async Task A_non_http_scheme_is_refused_rather_than_dialled(string path)
        {
            var (client, handler, _) = TestClient.Create();

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => client.SendJsonAsync(HttpMethod.Get, path));

            Assert.Equal("path", ex.ParamName);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task A_resultUrl_the_SDK_will_not_follow_is_reported_not_dialled()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.Accepted, """
            {"id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","dbId":1,"retryAfter":0,
             "estimatedDurationSec":20,"resultUrl":"file:///etc/passwd","created":1735689600}
            """);

            var ex = await Assert.ThrowsAsync<HostTrackerException>(() => client.RunCheckAsync(
                new IcCreateRequest { Url = "https://example.com", Type = "http" }));

            Assert.Equal(ProblemCodes.HttpError, ex.Code);
            Assert.Contains("will not follow", ex.Message, StringComparison.Ordinal);
            Assert.Single(handler.Requests);   // the create only; nothing was followed
        }
    }
}
