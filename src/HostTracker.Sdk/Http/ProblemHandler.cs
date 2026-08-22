using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HostTracker.Sdk.Http
{
    /// <summary>
    /// Turns every failing answer into a <see cref="HostTrackerException"/> before the generated
    /// client sees it, so callers have one exception type to catch whether the failure carried an
    /// RFC 9457 problem document, an HTML 502 from a proxy, or no answer at all.
    /// It also records each answer's <see cref="ResponseMetadata"/> for any open
    /// <see cref="ResponseCapture"/>.
    /// </summary>
    public sealed class ProblemHandler : DelegatingHandler
    {
        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw HostTrackerException.Network(
                    $"HostTracker request to {Describe(request)} failed: {ex.Message}", ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw HostTrackerException.Network(
                    $"HostTracker request to {Describe(request)} timed out.", ex);
            }

            var headers = CollectHeaders(response);
            var metadata = new ResponseMetadata(
                (int)response.StatusCode,
                headers,
                HeaderReader.First(headers, SdkHeaders.RequestId),
                IsTrue(HeaderReader.First(headers, SdkHeaders.IdempotencyReplayed)),
                request.Headers.TryGetValues(SdkHeaders.IdempotencyKey, out var keys) ? keys.FirstOrDefault() : null,
                RateLimitSnapshot.From(headers),
                HostTrackerException.ParseRetryAfter(HeaderReader.First(headers, SdkHeaders.RetryAfter)),
                HeaderReader.First(headers, "Location"),
                request.Options.TryGetValue(RetryHandler.AttemptsOption, out var attempts) ? attempts : 1,
                request.Method.Method,
                request.RequestUri);
            ResponseCaptureScope.Record(metadata);

            if ((int)response.StatusCode < 400) return response;

            string? body = null;
            try
            {
                if (response.Content is not null)
                    body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is System.IO.IOException)
            {
                // A truncated error body must not mask the status it belonged to.
            }
            finally
            {
                response.Dispose();
            }

            throw new HostTrackerException(
                $"HostTracker API {(int)metadata.StatusCode} on {Describe(request)}.",
                metadata.StatusCode,
                body,
                headers,
                null);
        }

        private static bool IsTrue(string? raw) =>
            string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "1", StringComparison.Ordinal);

        private static string Describe(HttpRequestMessage request) =>
            request.Method.Method + " " + (request.RequestUri?.PathAndQuery ?? request.RequestUri?.ToString() ?? "?");

        internal static IReadOnlyDictionary<string, IEnumerable<string>> CollectHeaders(HttpResponseMessage response)
        {
            var map = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers) map[h.Key] = h.Value;
            if (response.Content?.Headers is not null)
                foreach (var h in response.Content.Headers) map[h.Key] = h.Value;
            return map;
        }
    }
}
