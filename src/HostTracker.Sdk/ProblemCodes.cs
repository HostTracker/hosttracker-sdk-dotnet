namespace HostTracker.Sdk
{
    /// <summary>
    /// The problem <c>code</c> values worth branching on. <c>code</c> - never the HTTP status
    /// alone - is the machine-readable field: <see cref="RateLimited"/> and
    /// <see cref="QuotaExceeded"/> deliberately share status 429 and need different remediation.
    /// The registry is open: an unknown code arrives as a plain string.
    /// </summary>
    public static class ProblemCodes
    {
        /// <summary>422 - a value failed validation; <c>errors[]</c> names each one.</summary>
        public const string ValidationFailed = "validation_failed";
        /// <summary>400 - the body could not be read as the operation's shape.</summary>
        public const string MalformedRequest = "malformed_request";
        /// <summary>404 - no such resource, or not visible to this account.</summary>
        public const string NotFound = "not_found";
        /// <summary>401 - missing, malformed or expired credential. The only 401.</summary>
        public const string InvalidToken = "invalid_token";
        /// <summary>403 - the token does not carry the scope the operation needs.</summary>
        public const string MissingScope = "missing_scope";
        /// <summary>403 - the caller's IP is outside the token's allow-list.</summary>
        public const string IpNotAllowed = "ip_not_allowed";
        /// <summary>403 - the account's package does not allow this.</summary>
        public const string PackageLimit = "package_limit";
        /// <summary>429 - a rate window is full. Retryable; honour <c>Retry-After</c>.</summary>
        public const string RateLimited = "rate_limited";
        /// <summary>429 - the account's quota is spent. Not retryable; wait for the reset or upgrade.</summary>
        public const string QuotaExceeded = "quota_exceeded";
        /// <summary>409 - the same idempotency key was used with a different body, or is in flight.</summary>
        public const string IdempotencyKeyConflict = "idempotency_key_conflict";
        /// <summary>400 - this operation requires an <c>Idempotency-Key</c>.</summary>
        public const string IdempotencyKeyRequired = "idempotency_key_required";
        /// <summary>409 - a bulk delete's <c>expectedCount</c> no longer matches the selection.</summary>
        public const string SelectionMismatch = "selection_mismatch";
        /// <summary>422 - an unknown <c>expand</c> token; the problem lists what is allowed.</summary>
        public const string UnknownExpand = "unknown_expand";
        /// <summary>422 - an unknown <c>fields</c> token.</summary>
        public const string UnknownField = "unknown_field";
        /// <summary>422 - an unknown query/body parameter.</summary>
        public const string UnknownParameter = "unknown_parameter";
        /// <summary>422 - an unknown value in an enum-valued filter.</summary>
        public const string UnknownEnumValue = "unknown_enum_value";
        /// <summary>422 - a cursor that does not belong to this request's sort/filters.</summary>
        public const string InvalidCursor = "invalid_cursor";
        /// <summary>422 - <c>limit</c> outside 1..500.</summary>
        public const string InvalidLimit = "invalid_limit";
        /// <summary>409 - a monitor with that address already exists.</summary>
        public const string DuplicateMonitor = "duplicate_monitor";
        /// <summary>409 - a contact with that address already exists.</summary>
        public const string DuplicateContact = "duplicate_contact";
        /// <summary>500 - an unhandled server fault; quote <c>requestId</c> in support requests.</summary>
        public const string InternalError = "internal_error";
        /// <summary>502 - an upstream the API depends on failed.</summary>
        public const string UpstreamError = "upstream_error";
        /// <summary>503 - temporarily unavailable. Retryable when a <c>Retry-After</c> rides along.</summary>
        public const string ServiceUnavailable = "service_unavailable";

        /// <summary>
        /// SDK-side code for a non-problem HTTP failure - an HTML 502 from a proxy, an empty body,
        /// anything that is not <c>application/problem+json</c>.
        /// </summary>
        public const string HttpError = "http_error";

        /// <summary>SDK-side code for a transport failure - DNS, TLS, connection reset, timeout.</summary>
        public const string NetworkError = "network_error";
    }
}
