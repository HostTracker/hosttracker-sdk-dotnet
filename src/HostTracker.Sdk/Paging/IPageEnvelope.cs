using System.Collections.Generic;
using HostTracker.Sdk.Generated;

namespace HostTracker.Sdk
{
    /// <summary>
    /// The one collection envelope of the v2 surface - <c>{ data, nextCursor, hasMore }</c> - seen
    /// uniformly. Every generated <c>*Page</c> type implements this (explicitly, so its own
    /// properties keep their published names), which is what lets
    /// <see cref="Pagination"/> walk any list endpoint without a per-call adapter.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    public interface IPageEnvelope<out T>
    {
        /// <summary>This page's rows. Never null - an empty page is empty, not missing.</summary>
        IReadOnlyList<T> Items { get; }

        /// <summary>The opaque cursor for the next page, or null when this was the last one.</summary>
        string? Cursor { get; }

        /// <summary>True when another page exists. Always equals <c>Cursor is not null</c>.</summary>
        bool More { get; }

        /// <summary>The delta cursor for the next sync cycle, where the family supports one.</summary>
        string? Sync { get; }

        /// <summary>The <c>expand=count</c> block, when it was asked for.</summary>
        PageCount? Counts { get; }
    }

    /// <summary>Helpers the generated page adapters use.</summary>
    public static class PageEnvelope
    {
        /// <summary>Materializes a page's rows, treating a missing collection as empty.</summary>
        public static IReadOnlyList<T> AsList<T>(ICollection<T>? data)
        {
            if (data is null) return System.Array.Empty<T>();
            if (data is IReadOnlyList<T> list) return list;
            var copy = new List<T>(data);
            return copy;
        }
    }
}
