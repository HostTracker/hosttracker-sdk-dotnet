using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HostTracker.Sdk.Http
{
    /// <summary>
    /// Keeps a base URL that carries a path prefix (say <c>https://www.host-tracker.com/api2/</c>)
    /// working. The generated client builds root-absolute paths (<c>/monitor</c>), and
    /// <see cref="Uri"/> composition would drop the prefix; this handler puts it back. It is a
    /// no-op for the usual origin-only base URL.
    /// </summary>
    public sealed class BasePathHandler : DelegatingHandler
    {
        private readonly string _prefix;

        /// <summary>Creates the handler for <paramref name="baseUrl"/>.</summary>
        public BasePathHandler(Uri baseUrl)
        {
            ArgumentNullException.ThrowIfNull(baseUrl);
            var path = baseUrl.AbsolutePath.TrimEnd('/');
            _prefix = path == "/" ? string.Empty : path;
        }

        /// <summary>True when the base URL has no path and the handler does nothing.</summary>
        public bool IsNoOp => _prefix.Length == 0;

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!IsNoOp && request.RequestUri is Uri uri && uri.IsAbsoluteUri &&
                !uri.AbsolutePath.StartsWith(_prefix + "/", StringComparison.Ordinal) &&
                !string.Equals(uri.AbsolutePath, _prefix, StringComparison.Ordinal))
            {
                var builder = new UriBuilder(uri) { Path = _prefix + uri.AbsolutePath };
                request.RequestUri = builder.Uri;
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
