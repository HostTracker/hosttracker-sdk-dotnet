using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace HostTracker.Sdk.Http
{
    /// <summary>
    /// Puts <c>Authorization: Bearer &lt;token&gt;</c> on every request. Without a token the client
    /// still works, but only against the anonymous reference tier. A caller-set Authorization
    /// header is never overwritten.
    /// </summary>
    public sealed class AuthHandler : DelegatingHandler
    {
        private readonly string? _token;

        /// <summary>Creates the handler for <paramref name="token"/> (null = anonymous).</summary>
        public AuthHandler(string? token) => _token = string.IsNullOrWhiteSpace(token) ? null : token;

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_token is not null && request.Headers.Authorization is null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
