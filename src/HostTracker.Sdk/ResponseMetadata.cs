using System;
using System.Collections.Generic;

namespace HostTracker.Sdk
{
    /// <summary>
    /// What one HTTP answer carried besides its body. The generated operations return the body
    /// alone; open a <see cref="ResponseCapture"/> to read this alongside it.
    /// </summary>
    public sealed class ResponseMetadata
    {
        internal ResponseMetadata(
            int statusCode,
            IReadOnlyDictionary<string, IEnumerable<string>> headers,
            string? requestId,
            bool idempotencyReplayed,
            string? idempotencyKey,
            RateLimitSnapshot? rateLimit,
            TimeSpan? retryAfter,
            string? location,
            int attempts,
            string method,
            Uri? requestUri)
        {
            StatusCode = statusCode;
            Headers = headers;
            RequestId = requestId;
            IdempotencyReplayed = idempotencyReplayed;
            IdempotencyKey = idempotencyKey;
            RateLimit = rateLimit;
            RetryAfter = retryAfter;
            Location = location;
            Attempts = attempts;
            Method = method;
            RequestUri = requestUri;
        }

        /// <summary>The HTTP status of the answer.</summary>
        public int StatusCode { get; }

        /// <summary>Every response header.</summary>
        public IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }

        /// <summary>The <c>X-Request-Id</c> the API echoes on every answer.</summary>
        public string? RequestId { get; }

        /// <summary>
        /// True when the answer was replayed from a stored idempotent write rather than executed
        /// again (<c>Idempotency-Replayed: true</c>).
        /// </summary>
        public bool IdempotencyReplayed { get; }

        /// <summary>The <c>Idempotency-Key</c> that was sent, generated or caller-supplied.</summary>
        public string? IdempotencyKey { get; }

        /// <summary>The <c>RateLimit-*</c> snapshot, when the answer was metered.</summary>
        public RateLimitSnapshot? RateLimit { get; }

        /// <summary>The <c>Retry-After</c> the answer carried - on 202s, non-terminal job polls, 429s, 503s.</summary>
        public TimeSpan? RetryAfter { get; }

        /// <summary>The <c>Location</c> header - the job or instant-check a 202 started.</summary>
        public string? Location { get; }

        /// <summary>How many HTTP attempts this call took (1 when it succeeded first time).</summary>
        public int Attempts { get; }

        /// <summary>The request method.</summary>
        public string Method { get; }

        /// <summary>The resolved request URI.</summary>
        public Uri? RequestUri { get; }
    }

    /// <summary>
    /// Collects the <see cref="ResponseMetadata"/> of every call made inside its scope, on the
    /// current async flow. Nested captures all receive the same records.
    /// </summary>
    /// <example>
    /// <code>
    /// using var capture = client.CaptureResponses();
    /// var page = await client.Monitors.ListMonitorAsync(limit: 50);
    /// Console.WriteLine(capture.Last!.RequestId);
    /// </code>
    /// </example>
    public sealed class ResponseCapture : IDisposable
    {
        private readonly List<ResponseMetadata> _all = new List<ResponseMetadata>();
        private readonly ResponseCapture? _outer;
        private bool _disposed;

        internal ResponseCapture(ResponseCapture? outer) => _outer = outer;

        /// <summary>The most recent answer, or null when nothing has been sent yet.</summary>
        public ResponseMetadata? Last
        {
            get { lock (_all) { return _all.Count == 0 ? null : _all[_all.Count - 1]; } }
        }

        /// <summary>Every answer seen inside the scope, oldest first.</summary>
        public IReadOnlyList<ResponseMetadata> All
        {
            get { lock (_all) { return _all.ToArray(); } }
        }

        internal void Record(ResponseMetadata metadata)
        {
            lock (_all) { _all.Add(metadata); }
            _outer?.Record(metadata);
        }

        /// <summary>Forget everything captured so far.</summary>
        public void Clear() { lock (_all) { _all.Clear(); } }

        /// <summary>Closes the scope and restores the enclosing one.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ResponseCaptureScope.Pop(this, _outer);
        }
    }

    internal static class ResponseCaptureScope
    {
        private static readonly System.Threading.AsyncLocal<ResponseCapture?> Current =
            new System.Threading.AsyncLocal<ResponseCapture?>();

        internal static ResponseCapture Push()
        {
            var capture = new ResponseCapture(Current.Value);
            Current.Value = capture;
            return capture;
        }

        internal static void Pop(ResponseCapture capture, ResponseCapture? outer)
        {
            if (ReferenceEquals(Current.Value, capture)) Current.Value = outer;
        }

        internal static void Record(ResponseMetadata metadata) => Current.Value?.Record(metadata);
    }
}
