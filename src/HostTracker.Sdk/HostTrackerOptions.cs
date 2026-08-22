using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HostTracker.Sdk
{
    /// <summary>How the client stamps <c>Idempotency-Key</c> on mutating requests.</summary>
    public enum IdempotencyMode
    {
        /// <summary>
        /// Generate a fresh key for every POST/PATCH/PUT/DELETE that is not a <c>/q</c> body-query
        /// twin, so an automatic retry replays instead of executing twice. This is the default.
        /// </summary>
        Auto = 0,

        /// <summary>Never generate a key. One is still sent if the caller passes it explicitly.</summary>
        Off = 1,
    }

    /// <summary>Everything a <see cref="HostTrackerClient"/> needs to talk to the API.</summary>
    public sealed class HostTrackerOptions
    {
        /// <summary>The production API host.</summary>
        public const string DefaultBaseUrl = "https://api2.host-tracker.com";

        /// <summary>
        /// The API token, minted on the profile page. Long-lived; there is no refresh flow.
        /// Leave it null to reach only the anonymous reference tier
        /// (<c>GET /monitor/type</c>, <c>GET /agent</c>, ...).
        /// </summary>
        public string? Token { get; set; }

        /// <summary>Base address, default <see cref="DefaultBaseUrl"/>. A path prefix is honoured.</summary>
        public string BaseUrl { get; set; } = DefaultBaseUrl;

        /// <summary>
        /// Budget for ONE HTTP attempt. Default 30 seconds. Deliberately per attempt: a call that
        /// correctly honours two <c>Retry-After</c> waits must not be killed by the single-request
        /// budget. Ignored when <see cref="HttpClient"/> is supplied - that client's own timeout rules.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Appended to the SDK's own <c>User-Agent</c> (<c>hosttracker-sdk-dotnet/&lt;version&gt;</c>),
        /// e.g. <c>"acme-deploy/2.1"</c>.
        /// </summary>
        public string? UserAgentSuffix { get; set; }

        /// <summary>
        /// How many times a retryable failure is retried (so 2 = up to 3 attempts). Default 2.
        /// Only <c>429 rate_limited</c>, <c>503 service_unavailable</c> carrying a <c>Retry-After</c>,
        /// and transport failures are ever retried - never <c>quota_exceeded</c>.
        /// </summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>Upper bound on a single honoured <c>Retry-After</c>. Default 60 seconds.</summary>
        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Idempotency-key policy. Default <see cref="IdempotencyMode.Auto"/>.</summary>
        public IdempotencyMode Idempotency { get; set; } = IdempotencyMode.Auto;

        /// <summary>
        /// The innermost handler - supply one to control TLS, proxies or DNS, or to point the client
        /// at a stub in tests. Ignored when <see cref="HttpClient"/> is set.
        /// </summary>
        public HttpMessageHandler? Handler { get; set; }

        /// <summary>
        /// Takes complete control of transport: the SDK sends through this client and adds no
        /// handlers of its own to it, so auth, user-agent, idempotency and retry become the caller's
        /// responsibility. Prefer <see cref="Handler"/> for anything short of that.
        /// </summary>
        public HttpClient? HttpClient { get; set; }

        /// <summary>Dispose <see cref="Handler"/> (or the SDK-created handler) with the client. Default true.</summary>
        public bool DisposeHandler { get; set; } = true;

        /// <summary>
        /// Test seam for the retry pacing - replaces <c>Task.Delay</c>. A test can record the delays
        /// the client decided on and return immediately.
        /// </summary>
        public Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

        /// <summary>Test seam for the generated idempotency key. Default <c>Guid.NewGuid().ToString()</c>.</summary>
        public Func<string>? IdempotencyKeyFactory { get; set; }

        internal HostTrackerOptions Clone() => (HostTrackerOptions)MemberwiseClone();

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
                throw new ArgumentException("BaseUrl must be set.", nameof(BaseUrl));
            // Scheme-checked, not just "parses as absolute": on Linux "/api2" would otherwise pass
            // as file:///api2 and fail later inside the transport. See Http.SdkUri.
            if (!Http.SdkUri.IsHttpUrl(BaseUrl, out _))
                throw new ArgumentException(
                    $"BaseUrl '{BaseUrl}' is not an absolute http:// or https:// URL.", nameof(BaseUrl));
            if (MaxRetries < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxRetries), "MaxRetries cannot be negative.");
            if (Timeout <= TimeSpan.Zero && Timeout != System.Threading.Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout must be positive.");
        }
    }
}
