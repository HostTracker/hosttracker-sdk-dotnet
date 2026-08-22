using System;
using System.Collections.Generic;
using System.Globalization;

namespace HostTracker.Sdk
{
    /// <summary>
    /// The <c>RateLimit-*</c> headers of one answer. <see cref="Policy"/> is always present on a
    /// metered call; the numeric members accompany it only when a window actually binds, so under
    /// <c>RateLimit-Policy: none</c> they are absent rather than zero.
    /// </summary>
    public sealed class RateLimitSnapshot
    {
        internal RateLimitSnapshot(string? policy, long? limit, long? remaining, long? reset)
        {
            Policy = policy;
            Limit = limit;
            Remaining = remaining;
            Reset = reset;
        }

        /// <summary><c>&lt;bucket&gt;;q=&lt;limit&gt;[;w=&lt;seconds&gt;]</c>, or the literal <c>none</c>.</summary>
        public string? Policy { get; }

        /// <summary>Requests allowed in the window, when one binds.</summary>
        public long? Limit { get; }

        /// <summary>Requests left in the window, when one binds.</summary>
        public long? Remaining { get; }

        /// <summary>Seconds until the window resets, when one binds.</summary>
        public long? Reset { get; }

        /// <summary>True when the scope has no quota bucket configured (<c>Policy == "none"</c>).</summary>
        public bool Unmetered => string.Equals(Policy, "none", StringComparison.OrdinalIgnoreCase);

        internal static RateLimitSnapshot? From(IReadOnlyDictionary<string, IEnumerable<string>> headers)
        {
            var policy = HeaderReader.First(headers, "RateLimit-Policy");
            var limit = ParseLong(HeaderReader.First(headers, "RateLimit-Limit"));
            var remaining = ParseLong(HeaderReader.First(headers, "RateLimit-Remaining"));
            var reset = ParseLong(HeaderReader.First(headers, "RateLimit-Reset"));
            if (policy is null && limit is null && remaining is null && reset is null)
                return null;
            return new RateLimitSnapshot(policy, limit, remaining, reset);
        }

        private static long? ParseLong(string? raw) =>
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (long?)null;

        /// <inheritdoc/>
        public override string ToString() =>
            $"policy={Policy ?? "-"} limit={Limit?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
            $"remaining={Remaining?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
            $"reset={Reset?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
    }

    internal static class HeaderReader
    {
        internal static string? First(IReadOnlyDictionary<string, IEnumerable<string>> headers, string name)
        {
            if (headers is null) return null;
            foreach (var kv in headers)
            {
                if (!string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Value is null) continue;
                foreach (var v in kv.Value) return v;
            }
            return null;
        }
    }
}
