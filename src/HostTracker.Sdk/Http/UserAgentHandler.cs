using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace HostTracker.Sdk.Http
{
    /// <summary>
    /// Identifies the SDK as <c>hosttracker-sdk-dotnet/&lt;version&gt;</c>, plus the caller's own
    /// product token when <see cref="HostTrackerOptions.UserAgentSuffix"/> is set.
    /// </summary>
    public sealed class UserAgentHandler : DelegatingHandler
    {
        /// <summary>The product name this SDK reports.</summary>
        public const string ProductName = "hosttracker-sdk-dotnet";

        private static readonly string Version = ReadVersion();
        private readonly string _value;

        /// <summary>Creates the handler, optionally appending <paramref name="suffix"/>.</summary>
        public UserAgentHandler(string? suffix)
        {
            _value = ProductName + "/" + Version;
            if (!string.IsNullOrWhiteSpace(suffix)) _value += " " + suffix!.Trim();
        }

        /// <summary>The <c>User-Agent</c> value this SDK sends.</summary>
        public string Value => _value;

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.UserAgent.Count == 0)
                request.Headers.TryAddWithoutValidation("User-Agent", _value);
            return base.SendAsync(request, cancellationToken);
        }

        private static string ReadVersion()
        {
            var asm = typeof(UserAgentHandler).GetTypeInfo().Assembly;
            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Strip the "+<commit sha>" source-link suffix.
                var plus = informational!.IndexOf('+');
                return plus > 0 ? informational.Substring(0, plus) : informational;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }
}
