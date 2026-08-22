using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;

namespace HostTracker.Sdk.Tests
{
    internal static class LiveEnvironment
    {
        /// <summary>The instance to smoke, e.g. <c>https://api2.host-tracker.com</c>. Unset = tests skipped.</summary>
        public static string? BaseUrl => Environment.GetEnvironmentVariable("HT_BASE_URL");

        /// <summary>The token, from <c>HT_TOKEN</c> or the file named by <c>HT_TOKEN_FILE</c>.</summary>
        public static string? Token
        {
            get
            {
                var direct = Environment.GetEnvironmentVariable("HT_TOKEN");
                if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();

                var path = Environment.GetEnvironmentVariable("HT_TOKEN_FILE");
                return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
                    ? null
                    : File.ReadAllText(path).Trim();
            }
        }

        /// <summary>
        /// A handler that trusts a self-signed certificate on localhost only; every other host keeps
        /// full chain validation.
        /// </summary>
        public static HttpMessageHandler Transport()
        {
            var handler = new HttpClientHandler();
            var host = new Uri(BaseUrl!).Host;
            if (host is "localhost" or "127.0.0.1" or "::1")
            {
                handler.ServerCertificateCustomValidationCallback =
                    (request, _, _, errors) =>
                        errors == SslPolicyErrors.None ||
                        request.RequestUri?.Host is "localhost" or "127.0.0.1" or "::1";
            }
            return handler;
        }

        public static HostTrackerClient CreateClient(bool authenticated = true) =>
            new HostTrackerClient(new HostTrackerOptions
            {
                BaseUrl = BaseUrl!,
                Token = authenticated ? Token : null,
                Handler = Transport(),
                UserAgentSuffix = "hosttracker-sdk-dotnet-smoke",
                Timeout = TimeSpan.FromSeconds(60),
            });
    }
}
