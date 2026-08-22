using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace HostTracker.Sdk
{
    /// <summary>
    /// One entry of a problem document's <c>errors[]</c>. The named members are the ones every
    /// entry may carry; per-code remediation fields (<c>allowed</c>, <c>min</c>, <c>max</c>,
    /// <c>didYouMean</c>, <c>existingId</c>, ...) arrive in <see cref="Extensions"/>.
    /// </summary>
    public sealed class ProblemError
    {
        internal ProblemError(string? pointer, string? parameter, string? reason, JsonElement raw)
        {
            Pointer = pointer;
            Parameter = parameter;
            Reason = reason;
            Raw = raw;
        }

        /// <summary>JSON Pointer into the request body, or <c>/paramName</c> for a query failure.</summary>
        public string? Pointer { get; }

        /// <summary>The query parameter's name, when the failure is on the query string.</summary>
        public string? Parameter { get; }

        /// <summary>Why it failed - <c>required</c>, <c>empty</c>, <c>wrong_type</c>, ...</summary>
        public string? Reason { get; }

        /// <summary>The whole entry, for the members this type does not name.</summary>
        public JsonElement Raw { get; }

        /// <summary>Every member of the entry, keyed by name.</summary>
        public IReadOnlyDictionary<string, JsonElement> Extensions
        {
            get
            {
                var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (Raw.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in Raw.EnumerateObject()) map[p.Name] = p.Value;
                }
                return map;
            }
        }

        /// <inheritdoc/>
        public override string ToString() =>
            (Pointer ?? Parameter ?? "?") + (Reason is null ? "" : ": " + Reason);
    }

    /// <summary>
    /// The single exception every HostTracker call throws - an RFC 9457 problem document when the
    /// server sent one, and the same shape with <see cref="ProblemCodes.HttpError"/> or
    /// <see cref="ProblemCodes.NetworkError"/> when it did not (an HTML 502 from a proxy, a DNS
    /// failure, a timeout). Branch on <see cref="Code"/>, never on <see cref="StatusCode"/> alone:
    /// <c>rate_limited</c> and <c>quota_exceeded</c> are both 429 and want different handling.
    /// </summary>
    public class HostTrackerException : Exception
    {
        private static readonly IReadOnlyDictionary<string, IEnumerable<string>> NoHeaders =
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly IReadOnlyList<ProblemError> NoErrors = Array.Empty<ProblemError>();

        /// <summary>
        /// The constructor the generated client uses. Prefer reading the parsed members
        /// (<see cref="Code"/>, <see cref="Errors"/>, ...) over the raw <paramref name="response"/>.
        /// </summary>
        public HostTrackerException(
            string message,
            int statusCode,
            string? response,
            IReadOnlyDictionary<string, IEnumerable<string>>? headers,
            Exception? innerException)
            : base(BuildMessage(message, statusCode, response), innerException)
        {
            StatusCode = statusCode;
            Response = response;
            Headers = headers ?? NoHeaders;
            RateLimit = headers is null ? null : RateLimitSnapshot.From(headers);
            RequestId = HeaderReader.First(Headers, "X-Request-Id");
            RetryAfter = ParseRetryAfter(HeaderReader.First(Headers, "Retry-After"));

            var problem = ProblemParser.Parse(response);
            Code = problem.Code ?? DefaultCode(statusCode);
            Type = problem.Type;
            Title = problem.Title ?? message;
            Detail = problem.Detail;
            Instance = problem.Instance;
            Errors = problem.Errors ?? NoErrors;
            if (problem.RetryAfterSeconds is int s && RetryAfter is null)
                RetryAfter = TimeSpan.FromSeconds(s);
        }

        internal HostTrackerException(string message, int statusCode, string code, Exception? inner)
            : base(message, inner)
        {
            StatusCode = statusCode;
            Code = code;
            Title = message;
            Headers = NoHeaders;
            Errors = NoErrors;
        }

        /// <summary>The HTTP status, or 0 when the request never got an answer.</summary>
        public int StatusCode { get; }

        /// <summary>
        /// The machine-readable branch field. One of the API's problem codes, or
        /// <see cref="ProblemCodes.HttpError"/>/<see cref="ProblemCodes.NetworkError"/>. Unknown
        /// codes pass through as-is - the registry is open.
        /// </summary>
        public string Code { get; }

        /// <summary>The problem type URI, dereferenceable at <c>GET /problems/{code}</c>.</summary>
        public string? Type { get; }

        /// <summary>Short human-readable summary.</summary>
        public string? Title { get; }

        /// <summary>The occurrence-specific explanation.</summary>
        public string? Detail { get; }

        /// <summary>The occurrence's own URI, stamped per request.</summary>
        public string? Instance { get; }

        /// <summary>Per-field failures, each naming what was wrong and often how to fix it.</summary>
        public IReadOnlyList<ProblemError> Errors { get; }

        /// <summary>The <c>X-Request-Id</c> of the failing call - quote it in support requests.</summary>
        public string? RequestId { get; }

        /// <summary>
        /// How long the server asked the caller to wait, from <c>Retry-After</c> or the problem's
        /// own <c>retryAfter</c>. Present on 429s, 503s and in-flight idempotency conflicts.
        /// </summary>
        public TimeSpan? RetryAfter { get; }

        /// <summary>The <c>RateLimit-*</c> headers of the failing answer, when it carried them.</summary>
        public RateLimitSnapshot? RateLimit { get; }

        /// <summary>The raw response body, when there was one.</summary>
        public string? Response { get; }

        /// <summary>Every response header of the failing answer.</summary>
        public IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }

        /// <summary>True when <see cref="Code"/> equals <paramref name="code"/> (ordinal).</summary>
        public bool IsCode(string code) => string.Equals(Code, code, StringComparison.Ordinal);

        /// <summary>True for the two authentication/authorisation families (401 and 403).</summary>
        public bool IsAuthFailure => StatusCode == 401 || StatusCode == 403;

        private static string DefaultCode(int statusCode) =>
            statusCode == 0 ? ProblemCodes.NetworkError : ProblemCodes.HttpError;

        private static string BuildMessage(string message, int statusCode, string? response)
        {
            var problem = ProblemParser.Parse(response);
            var code = problem.Code;
            var detail = problem.Detail ?? problem.Title;
            if (code is null && detail is null)
                return message;
            var head = "HostTracker API " + statusCode.ToString(CultureInfo.InvariantCulture);
            if (code is not null) head += " " + code;
            return detail is null ? head : head + ": " + detail;
        }

        internal static TimeSpan? ParseRetryAfter(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw!.Trim();
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return seconds < 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
            {
                var delta = when - DateTimeOffset.UtcNow;
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            }
            return null;
        }

        internal static HostTrackerException Network(string message, Exception inner) =>
            new HostTrackerException(message, 0, ProblemCodes.NetworkError, inner);
    }

    /// <summary>
    /// The generic form the generated client throws when a declared non-success response has a
    /// typed body. <see cref="Result"/> is that body; every other member behaves exactly as on
    /// <see cref="HostTrackerException"/>.
    /// </summary>
    /// <typeparam name="TResult">The declared response type.</typeparam>
    public class HostTrackerException<TResult> : HostTrackerException
    {
        /// <summary>The constructor the generated client uses.</summary>
        public HostTrackerException(
            string message,
            int statusCode,
            string? response,
            IReadOnlyDictionary<string, IEnumerable<string>>? headers,
            TResult result,
            Exception? innerException)
            : base(message, statusCode, response, headers, innerException)
        {
            Result = result;
        }

        /// <summary>The deserialized response body.</summary>
        public TResult Result { get; }
    }

    internal static class ProblemParser
    {
        internal readonly struct Parsed
        {
            public Parsed(string? code, string? type, string? title, string? detail, string? instance,
                IReadOnlyList<ProblemError>? errors, int? retryAfterSeconds)
            {
                Code = code; Type = type; Title = title; Detail = detail;
                Instance = instance; Errors = errors; RetryAfterSeconds = retryAfterSeconds;
            }

            public string? Code { get; }
            public string? Type { get; }
            public string? Title { get; }
            public string? Detail { get; }
            public string? Instance { get; }
            public IReadOnlyList<ProblemError>? Errors { get; }
            public int? RetryAfterSeconds { get; }
        }

        internal static Parsed Parse(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return default;
            try
            {
                using var doc = JsonDocument.Parse(body!);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return default;

                var errors = new List<ProblemError>();
                int? retryAfter = null;
                if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in errs.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Object) continue;
                        errors.Add(new ProblemError(
                            Str(e, "pointer"), Str(e, "parameter"), Str(e, "reason"), e.Clone()));
                        if (retryAfter is null &&
                            e.TryGetProperty("retryAfter", out var ra) && ra.ValueKind == JsonValueKind.Number)
                            retryAfter = ra.GetInt32();
                        if (retryAfter is null &&
                            e.TryGetProperty("retryAfterSeconds", out var ras) && ras.ValueKind == JsonValueKind.Number)
                            retryAfter = ras.GetInt32();
                    }
                }

                return new Parsed(
                    Str(root, "code"), Str(root, "type"), Str(root, "title"),
                    Str(root, "detail"), Str(root, "instance"),
                    errors.Count == 0 ? null : errors, retryAfter);
            }
            catch (JsonException)
            {
                return default;
            }
        }

        private static string? Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
