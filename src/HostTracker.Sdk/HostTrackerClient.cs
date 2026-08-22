using System;
using System.Net.Http;
using HostTracker.Sdk.Generated;
using HostTracker.Sdk.Http;

namespace HostTracker.Sdk
{
    /// <summary>
    /// The entry point: one client, one <see cref="HttpClient"/>, one handler pipeline (user-agent,
    /// bearer auth, idempotency keys, error mapping, retry), and the generated per-tag clients
    /// hanging off it.
    /// <para>
    /// Thread-safe and meant to be long-lived - build one per process (or register it as a
    /// singleton) rather than one per call.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// using var client = new HostTrackerClient(new HostTrackerOptions { Token = token });
    /// var page = await client.Monitors.ListMonitorAsync(limit: 50);
    /// </code>
    /// </example>
    public sealed partial class HostTrackerClient : IDisposable
    {
        private readonly HostTrackerOptions _options;
        private readonly HttpClient _http;
        private readonly bool _ownsHttpClient;
        private bool _disposed;

        /// <summary>Creates a client for <paramref name="token"/> against the production API.</summary>
        /// <param name="token">An API token, or null for the anonymous reference tier.</param>
        public HostTrackerClient(string? token)
            : this(new HostTrackerOptions { Token = token })
        {
        }

        /// <summary>Creates a client from a full option set.</summary>
        /// <param name="options">Token, base URL, timeout, retry and transport settings.</param>
        public HostTrackerClient(HostTrackerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            _options = options.Clone();

            if (_options.HttpClient is not null)
            {
                _http = _options.HttpClient;
                _ownsHttpClient = false;
            }
            else
            {
                _http = BuildHttpClient(_options);
                _ownsHttpClient = true;
            }

            BaseUrl = new Uri(_options.BaseUrl, UriKind.Absolute);

            Monitors = new MonitorsClient(_http);
            MonitorTypes = new MonitorTypesClient(_http);
            Results = new ResultsClient(_http);
            Incidents = new IncidentsClient(_http);
            Maintenance = new MaintenanceClient(_http);
            Contacts = new ContactsClient(_http);
            Alerts = new AlertsClient(_http);
            Reports = new ReportsClient(_http);
            Webhooks = new WebhooksClient(_http);
            StatusPages = new StatusPagesClient(_http);
            Account = new AccountClient(_http);
            MonitoringLocations = new MonitoringLocationsClient(_http);
            InstantChecks = new InstantChecksClient(_http);
            Jobs = new JobsClient(_http);
        }

        /// <summary>The base address every call resolves against.</summary>
        public Uri BaseUrl { get; }

        /// <summary>The <see cref="HttpClient"/> the generated clients send through.</summary>
        public HttpClient HttpClient => _http;

        /// <summary>Monitors: list, create, read, update, delete, copy, bulk, results, spans.</summary>
        public IMonitorsClient Monitors { get; }

        /// <summary>The monitor-type catalogue and its settings schemas (anonymous reference tier).</summary>
        public IMonitorTypesClient MonitorTypes { get; }

        /// <summary>Check results and their snapshots.</summary>
        public IResultsClient Results { get; }

        /// <summary>Downtime incidents.</summary>
        public IIncidentsClient Incidents { get; }

        /// <summary>Maintenance windows.</summary>
        public IMaintenanceClient Maintenance { get; }

        /// <summary>Contacts, contact groups and contact confirmation.</summary>
        public IContactsClient Contacts { get; }

        /// <summary>Alert subscriptions, alert types and the alert log.</summary>
        public IAlertsClient Alerts { get; }

        /// <summary>Report subscriptions and report generation.</summary>
        public IReportsClient Reports { get; }

        /// <summary>Webhooks, their deliveries and their secrets.</summary>
        public IWebhooksClient Webhooks { get; }

        /// <summary>Public status pages, their incidents, templates and subscribers.</summary>
        public IStatusPagesClient StatusPages { get; }

        /// <summary>The account, its quota, limits and members.</summary>
        public IAccountClient Account { get; }

        /// <summary>Monitoring agents, pools and their IPs (anonymous reference tier).</summary>
        public IMonitoringLocationsClient MonitoringLocations { get; }

        /// <summary>On-demand instant checks and their catalogues.</summary>
        public IInstantChecksClient InstantChecks { get; }

        /// <summary>Alias for <see cref="InstantChecks"/> - the <c>/check</c> family.</summary>
        public IInstantChecksClient Checks => InstantChecks;

        /// <summary>Async jobs started by the bulk and report doors.</summary>
        public IJobsClient Jobs { get; }

        /// <summary>
        /// Starts collecting the <see cref="ResponseMetadata"/> of every call made on the current
        /// async flow until the returned scope is disposed - request ids, rate-limit snapshots and
        /// the <c>Idempotency-Replayed</c> flag the generated signatures cannot return.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "Discoverability: the capture scope belongs to the client the caller already holds.")]
        public ResponseCapture CaptureResponses() => ResponseCaptureScope.Push();

        private static HttpClient BuildHttpClient(HostTrackerOptions options)
        {
            var baseUrl = new Uri(options.BaseUrl, UriKind.Absolute);

            HttpMessageHandler inner = options.Handler ?? new HttpClientHandler();
            var retry = new RetryHandler(options.MaxRetries, options.MaxRetryDelay, options.Timeout, options.Delay)
            {
                InnerHandler = inner,
            };
            var problem = new ProblemHandler { InnerHandler = retry };
            var idempotency = new IdempotencyHandler(options.Idempotency, options.IdempotencyKeyFactory)
            {
                InnerHandler = problem,
            };
            var basePath = new BasePathHandler(baseUrl) { InnerHandler = idempotency };
            var auth = new AuthHandler(options.Token) { InnerHandler = basePath };
            var userAgent = new UserAgentHandler(options.UserAgentSuffix) { InnerHandler = auth };

            return new HttpClient(userAgent, disposeHandler: options.DisposeHandler)
            {
                BaseAddress = baseUrl,
                // The timeout is enforced per attempt inside RetryHandler; HttpClient's own timeout
                // would bound the whole retry ladder instead.
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
        }

        /// <summary>Disposes the <see cref="HttpClient"/> this instance created.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsHttpClient) _http.Dispose();
        }
    }
}
