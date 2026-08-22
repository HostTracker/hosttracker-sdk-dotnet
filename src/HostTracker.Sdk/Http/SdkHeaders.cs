namespace HostTracker.Sdk.Http
{
    /// <summary>The header names this SDK reads and writes.</summary>
    public static class SdkHeaders
    {
        /// <summary>The caller-chosen key that makes a mutating request replayable.</summary>
        public const string IdempotencyKey = "Idempotency-Key";

        /// <summary>Set to <c>true</c> when the answer was replayed from a stored write.</summary>
        public const string IdempotencyReplayed = "Idempotency-Replayed";

        /// <summary>Echoed on every answer; equals the traceId a 500 carries.</summary>
        public const string RequestId = "X-Request-Id";

        /// <summary>Seconds (or an HTTP date) to wait before trying again.</summary>
        public const string RetryAfter = "Retry-After";

        /// <summary>The HostTracker webhook signature header: <c>t=&lt;unix&gt;,v1=&lt;hex&gt;</c>.</summary>
        public const string HtSignature = "HT-Signature";

        /// <summary>The webhook's delivery id, stable across retries.</summary>
        public const string HtDelivery = "HT-Delivery";

        /// <summary>The event type of a webhook delivery.</summary>
        public const string HtEvent = "HT-Event";

        /// <summary>The webhook's id.</summary>
        public const string HtWebhook = "HT-Webhook";

        /// <summary>The attempt number of a webhook delivery, from 1.</summary>
        public const string HtAttempt = "HT-Attempt";

        /// <summary>Standard Webhooks: the delivery id.</summary>
        public const string WebhookId = "webhook-id";

        /// <summary>Standard Webhooks: the signing timestamp, Unix seconds.</summary>
        public const string WebhookTimestamp = "webhook-timestamp";

        /// <summary>Standard Webhooks: space-separated <c>v1,&lt;base64&gt;</c> signatures.</summary>
        public const string WebhookSignature = "webhook-signature";
    }
}
