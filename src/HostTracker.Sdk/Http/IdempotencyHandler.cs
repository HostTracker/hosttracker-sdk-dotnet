using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HostTracker.Sdk.Http
{
    /// <summary>
    /// Stamps an <c>Idempotency-Key</c> on every mutating request so a retry replays the stored
    /// answer instead of executing the write twice.
    /// <para>
    /// Skipped for the <c>/q</c> body-query twins: those are reads that happen to use POST, take no
    /// key, and would be refused if one were sent. A key the caller already set is never replaced.
    /// </para>
    /// </summary>
    public sealed class IdempotencyHandler : DelegatingHandler
    {
        private readonly IdempotencyMode _mode;
        private readonly Func<string> _keyFactory;

        /// <summary>Creates the handler.</summary>
        /// <param name="mode">Auto (stamp every write) or Off (only caller-supplied keys).</param>
        /// <param name="keyFactory">Key generator; defaults to <c>Guid.NewGuid().ToString()</c>.</param>
        public IdempotencyHandler(IdempotencyMode mode, Func<string>? keyFactory = null)
        {
            _mode = mode;
            _keyFactory = keyFactory ?? (() => Guid.NewGuid().ToString());
        }

        /// <summary>True when a request of this method and path should carry a generated key.</summary>
        public static bool ShouldStamp(HttpRequestMessage request)
        {
            var m = request.Method.Method;
            var mutating =
                string.Equals(m, "POST", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "PATCH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "PUT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "DELETE", StringComparison.OrdinalIgnoreCase);
            return mutating && !IsQueryTwin(request);
        }

        /// <summary>True for a <c>POST &lt;path&gt;/q</c> body-query twin - a read, not a write.</summary>
        public static bool IsQueryTwin(HttpRequestMessage request)
        {
            var path = request.RequestUri is null
                ? string.Empty
                : (request.RequestUri.IsAbsoluteUri ? request.RequestUri.AbsolutePath : request.RequestUri.OriginalString);
            var q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            path = path.TrimEnd('/');
            return path.EndsWith("/q", StringComparison.Ordinal) ||
                   string.Equals(path, "q", StringComparison.Ordinal);
        }

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_mode == IdempotencyMode.Auto &&
                !request.Headers.Contains(SdkHeaders.IdempotencyKey) &&
                ShouldStamp(request))
            {
                request.Headers.TryAddWithoutValidation(SdkHeaders.IdempotencyKey, _keyFactory());
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
