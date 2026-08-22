using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using HostTracker.Sdk.Generated;

namespace HostTracker.Sdk
{
    /// <summary>
    /// The body HostTracker POSTs to a webhook endpoint:
    /// <c>{ id, event, occurredAt, apiVersion, data }</c>. <see cref="Data"/> stays raw JSON, so an
    /// event type this build predates is still readable.
    /// </summary>
    public sealed class WebhookEvent
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private WebhookEvent(string id, string eventType, long occurredAt, string? apiVersion,
            JsonElement data, JsonElement raw)
        {
            Id = id;
            Event = eventType;
            OccurredAt = occurredAt;
            ApiVersion = apiVersion;
            Data = data;
            Raw = raw;
        }

        /// <summary>
        /// The delivery id - the same token as the <c>HT-Delivery</c> header and stable across
        /// retries, so it is the dedupe key.
        /// </summary>
        public string Id { get; }

        /// <summary>The event type, e.g. <c>monitor.down</c>. See <see cref="WebhookEvents"/>.</summary>
        public string Event { get; }

        /// <summary>When the event happened, Unix seconds.</summary>
        public long OccurredAt { get; }

        /// <summary>When the event happened, as an instant.</summary>
        public DateTimeOffset OccurredAtUtc => UnixTime.ToDateTimeOffset(OccurredAt);

        /// <summary>Always <c>v2</c> today.</summary>
        public string? ApiVersion { get; }

        /// <summary>The event's payload, still as JSON. Shape depends on <see cref="Event"/>.</summary>
        public JsonElement Data { get; }

        /// <summary>The whole envelope, for members this type does not name.</summary>
        public JsonElement Raw { get; }

        /// <summary>
        /// The generated type <see cref="Data"/> carries for this event, or null when the SDK does
        /// not know the event (a newer one than this build).
        /// </summary>
        public Type? DataType => DataTypes.TryGetValue(Event, out var t) ? t : null;

        /// <summary>
        /// Deserializes <see cref="Data"/> into one of the generated payload types.
        /// See <see cref="DataType"/> for which type an event carries.
        /// </summary>
        /// <typeparam name="T">The payload type, e.g. <c>WebhookMonitorAlert</c>.</typeparam>
        public T? DataAs<T>() where T : class =>
            Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? null
                : Data.Deserialize<T>(Options);

        /// <summary>Parses a delivery body. Verify the signature first, on the same raw bytes.</summary>
        /// <param name="rawBody">The raw request body.</param>
        /// <exception cref="FormatException">The body is not a webhook envelope.</exception>
        public static WebhookEvent Parse(string rawBody)
        {
            ArgumentNullException.ThrowIfNull(rawBody);
            return Parse(Encoding.UTF8.GetBytes(rawBody));
        }

        /// <summary>Parses a delivery body. Verify the signature first, on the same raw bytes.</summary>
        /// <param name="rawBody">The raw request body.</param>
        /// <exception cref="FormatException">The body is not a webhook envelope.</exception>
        public static WebhookEvent Parse(byte[] rawBody)
        {
            ArgumentNullException.ThrowIfNull(rawBody);
            if (!TryParse(rawBody, out var value, out var reason))
                throw new FormatException("Not a HostTracker webhook envelope: " + reason + ".");
            return value!;
        }

        /// <summary>Parses a delivery body without throwing.</summary>
        /// <param name="rawBody">The raw request body.</param>
        /// <param name="value">The parsed envelope, when this returns true.</param>
        public static bool TryParse(byte[] rawBody, out WebhookEvent? value) =>
            TryParse(rawBody, out value, out _);

        private static bool TryParse(byte[] rawBody, out WebhookEvent? value, out string reason)
        {
            value = null;
            reason = "empty body";
            if (rawBody is null || rawBody.Length == 0) return false;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                reason = ex.Message;
                return false;
            }

            if (root.ValueKind != JsonValueKind.Object) { reason = "not a JSON object"; return false; }
            if (!root.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.String)
            {
                reason = "no 'event' member";
                return false;
            }

            var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()! : string.Empty;
            var occurredAt = root.TryGetProperty("occurredAt", out var oa) && oa.ValueKind == JsonValueKind.Number
                ? oa.GetInt64() : 0L;
            var apiVersion = root.TryGetProperty("apiVersion", out var av) && av.ValueKind == JsonValueKind.String
                ? av.GetString() : null;
            var data = root.TryGetProperty("data", out var d) ? d : default;

            value = new WebhookEvent(id, evt.GetString()!, occurredAt, apiVersion, data, root);
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Which generated payload type each known event carries. An event added after this build is
        /// simply absent from the map, and <see cref="Data"/> stays readable.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, Type> DataTypes =
            new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                [WebhookEvents.MonitorDown] = typeof(WebhookMonitorAlert),
                [WebhookEvents.MonitorUp] = typeof(WebhookMonitorAlert),
                [WebhookEvents.MonitorRepeatedlyDown] = typeof(WebhookMonitorAlert),
                [WebhookEvents.IncidentOpened] = typeof(WebhookIncidentOpened),
                [WebhookEvents.IncidentClosed] = typeof(WebhookIncidentClosed),
                [WebhookEvents.MonitorCreated] = typeof(MonitorView),
                [WebhookEvents.MonitorUpdated] = typeof(MonitorView),
                [WebhookEvents.MonitorDeleted] = typeof(MonitorDeleteReceipt),
                [WebhookEvents.MaintenanceEnded] = typeof(WebhookMaintenanceEnded),
                [WebhookEvents.CertificateExpiring] = typeof(WebhookCertificateExpiring),
                [WebhookEvents.DomainExpiring] = typeof(WebhookDomainExpiring),
                [WebhookEvents.ContactConfirmed] = typeof(WebhookContactConfirmed),
                [WebhookEvents.ContactUpdated] = typeof(ContactView),
                [WebhookEvents.JobCompleted] = typeof(WebhookJobCompleted),
                [WebhookEvents.JobProgress] = typeof(WebhookJobProgress),
            };
    }
}
