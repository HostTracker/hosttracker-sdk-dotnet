using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HostTracker.Sdk.Generated;

namespace HostTracker.Sdk
{
    /// <summary>How <see cref="HostTrackerClient.WaitForJobAsync"/> paces and bounds its polling.</summary>
    public sealed class JobWaitOptions
    {
        /// <summary>How long to keep polling before giving up. Default 10 minutes.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Wait between polls when the answer carries no <c>Retry-After</c>. Default 2 seconds.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Upper bound on a single honoured <c>Retry-After</c>. Default 60 seconds.</summary>
        public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Return an <c>interrupted</c> job instead of continuing to poll. Default true: the state is
        /// not terminal, but only the caller can decide whether to <c>POST /job/{id}/resume</c> it.
        /// </summary>
        public bool StopOnInterrupted { get; set; } = true;

        /// <summary>How many items of the job's per-item receipts each poll should fetch. Default: the API's own.</summary>
        public int? ResultLimit { get; set; }

        /// <summary>Called after every poll - a progress hook.</summary>
        public Action<JobView>? OnPoll { get; set; }
    }

    /// <summary>Reading a job's <c>state</c>, whose vocabulary is closed but whose spelling is a plain string.</summary>
    public static class JobStateInfo
    {
        private static readonly HashSet<string> TerminalStates = new HashSet<string>(StringComparer.Ordinal)
        {
            JobStates.Succeeded, JobStates.Partial, JobStates.Failed, JobStates.Cancelled,
        };

        /// <summary>
        /// True for the four states a job never leaves. <c>interrupted</c> is not one of them: the
        /// server running the job died, and <c>POST /job/{id}/resume</c> continues it.
        /// </summary>
        public static bool IsTerminal(string? state) => state is not null && TerminalStates.Contains(state);

        /// <summary>
        /// True for <c>partial</c>, which is a success: the batch ran to the end with some items
        /// failed. Read the per-item receipts and resubmit only those, not the whole batch.
        /// </summary>
        public static bool IsPartial(string? state) => string.Equals(state, JobStates.Partial, StringComparison.Ordinal);

        /// <summary>True for <c>interrupted</c> - resumable, not failed.</summary>
        public static bool IsInterrupted(string? state) =>
            string.Equals(state, JobStates.Interrupted, StringComparison.Ordinal);
    }

    public sealed partial class HostTrackerClient
    {
        /// <summary>
        /// Polls <c>GET /job/{id}</c> until the job reaches a terminal state, pacing itself with the
        /// <c>Retry-After</c> every non-terminal poll carries (a terminal job carries none - that is
        /// the loop's exit condition, and this helper honours it).
        /// <para>
        /// Returns on <c>succeeded</c>, <c>partial</c>, <c>failed</c> and <c>cancelled</c> - and, by
        /// default, on <c>interrupted</c> too, which is not terminal: only the caller can decide
        /// whether to resume it. <c>partial</c> is a success with some failed items, not an error.
        /// </para>
        /// </summary>
        /// <param name="jobId">The job id a 202 answer returned.</param>
        /// <param name="options">Polling bounds; null takes the defaults.</param>
        /// <param name="cancellationToken">Stops the wait.</param>
        /// <exception cref="TimeoutException">The job was still running when the budget ran out.</exception>
        public async Task<JobView> WaitForJobAsync(
            Guid jobId, JobWaitOptions? options = null, CancellationToken cancellationToken = default)
        {
            var opt = options ?? new JobWaitOptions();
            var deadline = opt.Timeout == System.Threading.Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : DateTimeOffset.UtcNow + opt.Timeout;

            using var capture = CaptureResponses();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                capture.Clear();

                var job = await Jobs.GetJobAsync(jobId, opt.ResultLimit, null, null, cancellationToken)
                    .ConfigureAwait(false);
                opt.OnPoll?.Invoke(job);

                if (JobStateInfo.IsTerminal(job.State)) return job;
                if (opt.StopOnInterrupted && JobStateInfo.IsInterrupted(job.State)) return job;

                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException(
                        $"Job {jobId} was still '{job.State ?? "?"}' after {opt.Timeout}. " +
                        "It keeps running server-side - poll GET /job/{id} again.");

                var wait = capture.Last?.RetryAfter ?? opt.PollInterval;
                if (wait > opt.MaxPollInterval) wait = opt.MaxPollInterval;
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                await DelayAsync(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Every per-item receipt of a job, walked across pages. A failed item's <c>error</c> is a
        /// full problem document naming exactly what that row got wrong.
        /// </summary>
        /// <param name="jobId">The job to read.</param>
        /// <param name="cancellationToken">Stops the walk.</param>
        public IAsyncEnumerable<JobItemView> JobResultsAsync(
            Guid jobId, CancellationToken cancellationToken = default) =>
            PaginateJobResultsAsync(jobId, cancellationToken);

        private async IAsyncEnumerable<JobItemView> PaginateJobResultsAsync(
            Guid jobId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string? cursor = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = await Jobs.GetJobAsync(jobId, null, cursor, null, cancellationToken).ConfigureAwait(false);
                var rows = job.Results;
                if (rows is not null)
                    foreach (var row in rows) yield return row;

                if (job.HasMore != true || string.IsNullOrEmpty(job.NextCursor)) yield break;
                cursor = job.NextCursor;
            }
        }

        internal Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            _options.Delay is null ? Task.Delay(delay, cancellationToken) : _options.Delay(delay, cancellationToken);
    }
}
