using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HostTracker.Sdk.Http
{
    /// <summary>
    /// The automatic-retry policy, deliberately narrow.
    /// <list type="bullet">
    /// <item><description><c>429 rate_limited</c> - honouring <c>Retry-After</c>, capped.</description></item>
    /// <item><description><c>503 service_unavailable</c> - only when it carries a <c>Retry-After</c>.</description></item>
    /// <item><description>transport failures (DNS, TLS, reset, request timeout).</description></item>
    /// </list>
    /// A 429 or 503 whose body carries no problem code counts too - an edge throttle in front of the
    /// API can answer in plain text.
    /// <para>
    /// <c>429 quota_exceeded</c> is never retried: the account's allowance is spent. A write is
    /// retried only when an <c>Idempotency-Key</c> rides with it, so the server replays instead of
    /// executing twice; a <c>/q</c> body query counts as the read it is.
    /// </para>
    /// <para>
    /// The request timeout is applied per attempt, not to the whole ladder, so a sequence honouring
    /// two 30-second <c>Retry-After</c> waits is not killed by the 30-second per-request budget.
    /// </para>
    /// </summary>
    public sealed class RetryHandler : DelegatingHandler
    {
        internal static readonly HttpRequestOptionsKey<int> AttemptsOption =
            new HttpRequestOptionsKey<int>("HostTracker.Attempts");

        /// <summary>Base of the exponential backoff used when no <c>Retry-After</c> says otherwise.</summary>
        private static readonly TimeSpan BackoffBase = TimeSpan.FromMilliseconds(200);

        /// <summary>Ceiling on a self-chosen backoff. A server-supplied wait is capped separately, higher.</summary>
        private static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(5);

        private readonly int _maxRetries;
        private readonly TimeSpan _maxDelay;
        private readonly TimeSpan _attemptTimeout;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        /// <summary>Creates the handler.</summary>
        /// <param name="maxRetries">Retries after the first attempt. 0 disables retrying.</param>
        /// <param name="maxDelay">Upper bound on one honoured <c>Retry-After</c>.</param>
        /// <param name="attemptTimeout">Budget for ONE attempt; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for none.</param>
        /// <param name="delay">Delay implementation; the default is <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
        public RetryHandler(
            int maxRetries,
            TimeSpan maxDelay,
            TimeSpan attemptTimeout,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _maxRetries = maxRetries < 0 ? 0 : maxRetries;
            _maxDelay = maxDelay <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : maxDelay;
            _attemptTimeout = attemptTimeout;
            _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
        }

        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[]? body = null;
            if (request.Content is not null)
                body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                request.Options.Set(AttemptsOption, attempt + 1);
                using var clone = await CloneAsync(request, body).ConfigureAwait(false);

                // The per-attempt budget. On success the timer is switched off but the source is not
                // disposed: with ResponseHeadersRead the body is still streaming against this token,
                // and a disposed source would abort the read. It stays linked to the caller's token.
                var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_attemptTimeout != System.Threading.Timeout.InfiniteTimeSpan && _attemptTimeout > TimeSpan.Zero)
                    attemptCts.CancelAfter(_attemptTimeout);

                HttpResponseMessage response;
                try
                {
                    response = await base.SendAsync(clone, attemptCts.Token).ConfigureAwait(false);
                    attemptCts.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
                }
                catch (Exception ex) when (IsTransport(ex, cancellationToken))
                {
                    attemptCts.Dispose();
                    if (attempt >= _maxRetries || !CanRetryRequest(request)) throw;
                    await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch
                {
                    attemptCts.Dispose();
                    throw;
                }

                if (attempt >= _maxRetries)
                    return response;

                var status = (int)response.StatusCode;
                if (status != 429 && status != 503)
                    return response;

                // Both retryable statuses need the problem code, so buffer the body and put it back
                // for whoever reads it next.
                var text = await BufferAsync(response, cancellationToken).ConfigureAwait(false);
                if (!ShouldRetry(request, response, text, out var wait))
                    return response;

                response.Dispose();
                await _delay(Clamp(wait ?? Backoff(attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>True when the request may be replayed - a read, or a write carrying a key.</summary>
        public static bool CanRetryRequest(HttpRequestMessage request)
        {
            var m = request.Method.Method;
            if (string.Equals(m, "GET", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "HEAD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                return true;
            if (IdempotencyHandler.IsQueryTwin(request)) return true;
            return request.Headers.Contains(SdkHeaders.IdempotencyKey);
        }

        private static bool ShouldRetry(HttpRequestMessage request, HttpResponseMessage response,
            string? body, out TimeSpan? wait)
        {
            wait = null;
            if (!CanRetryRequest(request)) return false;

            var code = ReadCode(body);
            var retryAfter = HostTrackerException.ParseRetryAfter(
                response.Headers.TryGetValues(SdkHeaders.RetryAfter, out var v) ? v.FirstOrDefault() : null);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // quota_exceeded shares the status and is not retryable.
                if (string.Equals(code, ProblemCodes.QuotaExceeded, StringComparison.Ordinal)) return false;
                if (code is not null && !string.Equals(code, ProblemCodes.RateLimited, StringComparison.Ordinal))
                    return false;
                wait = retryAfter;
                return true;
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                if (retryAfter is null) return false;
                if (code is not null && !string.Equals(code, ProblemCodes.ServiceUnavailable, StringComparison.Ordinal))
                    return false;
                wait = retryAfter;
                return true;
            }

            return false;
        }

        private static string? ReadCode(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body!);
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                       doc.RootElement.TryGetProperty("code", out var c) &&
                       c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
            }
            catch (JsonException) { return null; }
        }

        private static async Task<string?> BufferAsync(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.Content is null) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var replacement = new ByteArrayContent(bytes);
            foreach (var h in response.Content.Headers)
                replacement.Headers.TryAddWithoutValidation(h.Key, h.Value);
            response.Content = replacement;
            return Encoding.UTF8.GetString(bytes);
        }

        private static bool IsTransport(Exception ex, CancellationToken ct)
        {
            if (ex is HttpRequestException) return true;
            // A per-request timeout surfaces as TaskCanceledException with no cancellation asked for.
            if (ex is OperationCanceledException && !ct.IsCancellationRequested) return true;
            return false;
        }

        private TimeSpan Clamp(TimeSpan value) =>
            value < TimeSpan.Zero ? TimeSpan.Zero : (value > _maxDelay ? _maxDelay : value);

        /// <summary>
        /// Full jitter over an exponential window - <c>rand(0, min(5s, 200ms * 2^attempt))</c>. Used
        /// only when the server said nothing; a <c>Retry-After</c> always wins.
        /// </summary>
        private static TimeSpan Backoff(int attempt)
        {
            var window = BackoffBase.TotalMilliseconds * Math.Pow(2, attempt);
            if (window > BackoffCap.TotalMilliseconds) window = BackoffCap.TotalMilliseconds;
            return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * window);
        }

        private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, byte[]? body)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
            };
            foreach (var h in request.Headers)
                clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
            foreach (var o in (IDictionary<string, object?>)request.Options)
                ((IDictionary<string, object?>)clone.Options)[o.Key] = o.Value;

            if (body is not null)
            {
                clone.Content = new ByteArrayContent(body);
                if (request.Content is not null)
                {
                    foreach (var h in request.Content.Headers)
                        clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }
            return await Task.FromResult(clone).ConfigureAwait(false);
        }
    }
}
