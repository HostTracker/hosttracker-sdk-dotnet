using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using HostTracker.Sdk.Generated;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    public class PagingTests
    {
        private static string Page(string idPrefix, int count, string? nextCursor)
        {
            var rows = string.Join(",", Enumerable.Range(0, count).Select(i => $$"""
            {"id":"{{Guid.NewGuid()}}","type":"http","name":"{{idPrefix}}-{{i}}","url":"https://example.com",
             "state":"up","since":1735689600,"enabled":true,"updated":1735689600,"created":1735689600,
             "openStat":false,"fullLog":false}
            """));
            var cursor = nextCursor is null ? "null" : $"\"{nextCursor}\"";
            var hasMore = nextCursor is null ? "false" : "true";
            return $$"""{"data":[{{rows}}],"nextCursor":{{cursor}},"hasMore":{{hasMore}},"syncCursor":"sync-1"}""";
        }

        [Fact]
        public async Task Walks_three_pages_and_stops_on_a_null_cursor()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Page("a", 2, "cursor-2"));
            handler.Json(HttpStatusCode.OK, Page("b", 2, "cursor-3"));
            handler.Json(HttpStatusCode.OK, Page("c", 1, null));

            var names = new List<string>();
            await foreach (var monitor in Pagination.PaginateAsync<MonitorPage, MonitorView>(
                (cursor, ct) => client.Monitors.ListMonitorAsync(cursor: cursor, cancellationToken: ct)))
            {
                names.Add(monitor.Name!);
            }

            Assert.Equal(new[] { "a-0", "a-1", "b-0", "b-1", "c-0" }, names);
            Assert.Equal(3, handler.Requests.Count);
            Assert.DoesNotContain("cursor", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
            Assert.Contains("cursor=cursor-2", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
            Assert.Contains("cursor=cursor-3", handler.Requests[2].Uri.Query, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_pages_variant_keeps_the_envelope_reachable()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Page("a", 1, "cursor-2"));
            handler.Json(HttpStatusCode.OK, Page("b", 1, null));

            var syncCursors = new List<string?>();
            await foreach (var page in Pagination.PagesAsync<MonitorPage, MonitorView>(
                (cursor, ct) => client.Monitors.ListMonitorAsync(cursor: cursor, cancellationToken: ct)))
            {
                syncCursors.Add(page.SyncCursor);
            }

            Assert.Equal(new[] { "sync-1", "sync-1" }, syncCursors);
        }

        [Fact]
        public async Task The_single_type_argument_form_walks_the_same_pages()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Page("a", 1, "cursor-2"));
            handler.Json(HttpStatusCode.OK, Page("b", 1, null));

            var count = 0;
            await foreach (var _ in Pagination.PaginateAsync<MonitorView>(
                async (cursor, ct) => await client.Monitors.ListMonitorAsync(cursor: cursor, cancellationToken: ct)))
            {
                count++;
            }

            Assert.Equal(2, count);
        }

        [Fact]
        public async Task An_empty_closed_list_yields_nothing_and_costs_one_call()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, """{"data":[],"nextCursor":null,"hasMore":false}""");

            var count = 0;
            await foreach (var _ in Pagination.PaginateAsync<MonitorPage, MonitorView>(
                (cursor, ct) => client.Monitors.ListMonitorAsync(cursor: cursor, cancellationToken: ct)))
            {
                count++;
            }

            Assert.Equal(0, count);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task A_repeated_cursor_is_refused_rather_than_looping_forever()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK, Page("a", 1, "same"));
            handler.Json(HttpStatusCode.OK, Page("b", 1, "same"));

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in Pagination.PaginateAsync<MonitorPage, MonitorView>(
                    (cursor, ct) => client.Monitors.ListMonitorAsync(cursor: cursor, cancellationToken: ct)))
                {
                }
            });
        }

        [Fact]
        public async Task The_expand_count_block_arrives_through_the_envelope_interface()
        {
            var (client, handler, _) = TestClient.Create();
            handler.Json(HttpStatusCode.OK,
                """{"data":[],"nextCursor":null,"hasMore":false,"count":{"total":120,"matched":7}}""");

            var page = await client.Monitors.ListMonitorAsync(expand: new[] { "count" });

            IPageEnvelope<MonitorView> envelope = page;
            Assert.Equal(120, envelope.Counts!.Total);
            Assert.Equal(7, envelope.Counts.Matched);
            Assert.False(envelope.More);
            Assert.Null(envelope.Cursor);
        }
    }
}
