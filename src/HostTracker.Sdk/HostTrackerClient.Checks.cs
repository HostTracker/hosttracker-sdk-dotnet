using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HostTracker.Sdk.Generated;
using HostTracker.Sdk.Http;

namespace HostTracker.Sdk
{
    /// <summary>How <see cref="HostTrackerClient.RunCheckAsync"/> paces and bounds its polling.</summary>
    public sealed class RunCheckOptions
    {
        /// <summary>How long to keep polling before giving up. Default 3 minutes.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);

        /// <summary>Wait between polls when a poll carries no <c>retryAfter</c>. Default 2 seconds.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Upper bound on a single honoured <c>retryAfter</c>. Default 30 seconds.</summary>
        public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Called after every poll - the result grows as fleet locations report in.</summary>
        public Action<IcResultView>? OnPoll { get; set; }
    }

    public sealed partial class HostTrackerClient
    {
        /// <summary>
        /// Runs one on-demand check end to end: <c>POST /check</c>, then follows the
        /// <c>resultUrl</c> the answer carries until the check reads <c>state: "done"</c>.
        /// <para>
        /// The URL is followed rather than rebuilt - a check is addressed by the pair
        /// <c>(dbId, id)</c> and the server owns the spelling. Only its path and query are taken:
        /// the poll is always dialled on the configured <see cref="BaseUrl"/>, so the token cannot
        /// reach another origin. Each poll's answer is incremental: <c>events[]</c> grows as
        /// locations report, and <c>retryAfter</c> paces the next poll.
        /// </para>
        /// </summary>
        /// <param name="request">The check to run - <c>{ url, type }</c> at minimum.</param>
        /// <param name="options">Polling bounds; null takes the defaults.</param>
        /// <param name="cancellationToken">Stops the run (the check itself keeps going server-side).</param>
        /// <exception cref="TimeoutException">The check was still running when the budget ran out.</exception>
        public async Task<IcResultView> RunCheckAsync(
            IcCreateRequest request, RunCheckOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var opt = options ?? new RunCheckOptions();

            var accepted = await InstantChecks.CreateInstantCheckAsync(request, null, cancellationToken)
                .ConfigureAwait(false);

            var deadline = opt.Timeout == System.Threading.Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : DateTimeOffset.UtcNow + opt.Timeout;

            var first = TimeSpan.FromSeconds(Math.Max(0, accepted.RetryAfter));
            await DelayAsync(Clamp(first, opt), cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = accepted.ResultUrl is { Length: > 0 }
                    ? await GetJsonAsync<IcResultView>(accepted.ResultUrl!, cancellationToken).ConfigureAwait(false)
                    : await InstantChecks.GetInstantCheckAsync(accepted.DbId, accepted.Id, null, cancellationToken)
                        .ConfigureAwait(false);

                opt.OnPoll?.Invoke(result);
                if (string.Equals(result.State, InstantCheckStates.Done, StringComparison.Ordinal))
                    return result;

                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException(
                        $"Instant check {accepted.DbId}/{accepted.Id} was still '{result.State ?? "?"}' " +
                        $"after {opt.Timeout}. It keeps running - poll {accepted.ResultUrl ?? "GET /check/{dbId}/{id}"} again.");

                var wait = result.RetryAfter is int seconds && seconds > 0
                    ? TimeSpan.FromSeconds(seconds)
                    : opt.PollInterval;
                await DelayAsync(Clamp(wait, opt), cancellationToken).ConfigureAwait(false);
            }
        }

        private static TimeSpan Clamp(TimeSpan value, RunCheckOptions options)
        {
            if (value < TimeSpan.Zero) return TimeSpan.Zero;
            return value > options.MaxPollInterval ? options.MaxPollInterval : value;
        }

        /// <summary>
        /// GETs a server-supplied URL - absolute or root-relative - through the same pipeline as
        /// every other call (auth, retry, error mapping) and deserializes the body.
        /// </summary>
        /// <remarks>
        /// Only the path and query of <paramref name="url"/> are used; the request is dialled on the
        /// configured <see cref="BaseUrl"/>, which keeps the bearer token on the client's own origin.
        /// </remarks>
        /// <typeparam name="T">The expected response shape.</typeparam>
        /// <param name="url">The URL the API handed back; never one the caller built.</param>
        /// <param name="cancellationToken">Stops the request.</param>
        internal async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
        {
            // Not `TryCreate(..., Absolute)`: on Linux a rooted path such as `/check/1/<guid>` is a
            // valid file:// URI (see SdkUri). Anything not resolving to http(s) is reported, not dialled.
            if (!SdkUri.TryResolveOnBase(BaseUrl, url, out var uri))
            {
                throw new HostTrackerException(
                    $"HostTracker returned a URL the SDK will not follow: '{url}'.",
                    0, ProblemCodes.HttpError, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, SdkJson.Default, cancellationToken)
                .ConfigureAwait(false);
            if (value is null)
                throw new HostTrackerException(
                    $"HostTracker returned an empty body for {uri}.",
                    (int)response.StatusCode,
                    null,
                    ProblemHandlerHeaders(response),
                    null);
            return value;
        }

        private static System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IEnumerable<string>>
            ProblemHandlerHeaders(HttpResponseMessage response) => Http.ProblemHandler.CollectHeaders(response);
    }
}
