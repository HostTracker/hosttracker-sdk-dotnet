using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HostTracker.Sdk
{
    /// <summary>
    /// Walks a cursor-paginated collection. Pass the list call as a lambda that takes the cursor;
    /// the helper follows <c>nextCursor</c> until it is null. Cursors are opaque - never build,
    /// parse or reorder one, and never change the sort mid-walk (that is <c>422 invalid_cursor</c>).
    /// </summary>
    /// <example>
    /// <code>
    /// await foreach (var m in Pagination.PaginateAsync(
    ///         (cursor, ct) =&gt; client.Monitors.ListMonitorAsync(limit: 200, cursor: cursor, cancellationToken: ct)))
    /// {
    ///     Console.WriteLine(m.Name);
    /// }
    /// </code>
    /// </example>
    public static class Pagination
    {
        /// <summary>Yields every row across every page of <paramref name="fetch"/>.</summary>
        /// <typeparam name="TPage">The generated page envelope type.</typeparam>
        /// <typeparam name="TItem">The row type.</typeparam>
        /// <param name="fetch">The list call; receives the cursor for the page to fetch (null = first).</param>
        /// <param name="cancellationToken">Stops the walk.</param>
        public static async IAsyncEnumerable<TItem> PaginateAsync<TPage, TItem>(
            Func<string?, CancellationToken, Task<TPage>> fetch,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TPage : IPageEnvelope<TItem>
        {
            await foreach (var page in PagesAsync<TPage, TItem>(fetch, cancellationToken).ConfigureAwait(false))
            {
                foreach (var item in page.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                }
            }
        }

        /// <summary>
        /// Yields the pages themselves, so the envelope's <c>syncCursor</c>, <c>count</c> and
        /// <c>summary</c> stay reachable.
        /// </summary>
        /// <typeparam name="TPage">The generated page envelope type.</typeparam>
        /// <typeparam name="TItem">The row type.</typeparam>
        /// <param name="fetch">The list call; receives the cursor for the page to fetch (null = first).</param>
        /// <param name="cancellationToken">Stops the walk.</param>
        public static async IAsyncEnumerable<TPage> PagesAsync<TPage, TItem>(
            Func<string?, CancellationToken, Task<TPage>> fetch,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TPage : IPageEnvelope<TItem>
        {
            ArgumentNullException.ThrowIfNull(fetch);

            string? cursor = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await fetch(cursor, cancellationToken).ConfigureAwait(false);
                if (page is null) yield break;
                yield return page;

                var next = page.Cursor;
                // hasMore always equals `nextCursor is not null`; trust the cursor and stop on either.
                if (!page.More || string.IsNullOrEmpty(next)) yield break;
                if (!seen.Add(next!))
                    throw new InvalidOperationException(
                        "The API returned a cursor that was already followed; stopping to avoid an endless walk.");
                cursor = next;
            }
        }
        /// <summary>
        /// Single-type-argument form: name the row type and let the page type follow.
        /// </summary>
        /// <typeparam name="TItem">The row type.</typeparam>
        /// <param name="fetch">The list call; receives the cursor for the page to fetch (null = first).</param>
        /// <param name="cancellationToken">Stops the walk.</param>
        /// <example>
        /// <code>
        /// await foreach (var m in Pagination.PaginateAsync&lt;MonitorView&gt;(
        ///         async (cursor, ct) =&gt; await client.Monitors.ListMonitorAsync(cursor: cursor, cancellationToken: ct)))
        /// { }
        /// </code>
        /// </example>
        public static IAsyncEnumerable<TItem> PaginateAsync<TItem>(
            Func<string?, CancellationToken, Task<IPageEnvelope<TItem>>> fetch,
            CancellationToken cancellationToken = default) =>
            PaginateAsync<IPageEnvelope<TItem>, TItem>(fetch, cancellationToken);

        /// <summary>Single-type-argument form of <see cref="PagesAsync{TPage, TItem}"/>.</summary>
        /// <typeparam name="TItem">The row type.</typeparam>
        /// <param name="fetch">The list call; receives the cursor for the page to fetch (null = first).</param>
        /// <param name="cancellationToken">Stops the walk.</param>
        public static IAsyncEnumerable<IPageEnvelope<TItem>> PagesAsync<TItem>(
            Func<string?, CancellationToken, Task<IPageEnvelope<TItem>>> fetch,
            CancellationToken cancellationToken = default) =>
            PagesAsync<IPageEnvelope<TItem>, TItem>(fetch, cancellationToken);
    }
}
