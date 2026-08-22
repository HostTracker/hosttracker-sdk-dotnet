using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HostTracker.Sdk.Http;

namespace HostTracker.Sdk
{
    /// <summary>Which signature header a verification used, or should use.</summary>
    public enum WebhookScheme
    {
        /// <summary>Prefer <c>HT-Signature</c>; fall back to the Standard Webhooks triple.</summary>
        Auto = 0,

        /// <summary>HostTracker's own <c>HT-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c>.</summary>
        HostTracker = 1,

        /// <summary>The Standard Webhooks <c>webhook-id</c>/<c>-timestamp</c>/<c>-signature</c> triple.</summary>
        StandardWebhooks = 2,
    }

    /// <summary>The verdict of <see cref="WebhookSignature.Verify(IEnumerable{KeyValuePair{string, IEnumerable{string}}}, byte[], IEnumerable{string}, TimeSpan?, DateTimeOffset?, WebhookScheme)"/>.</summary>
    public sealed class WebhookVerificationResult
    {
        internal WebhookVerificationResult(bool isValid, WebhookScheme scheme, string? reason, DateTimeOffset? timestamp)
        {
            IsValid = isValid;
            Scheme = scheme;
            Reason = reason;
            Timestamp = timestamp;
        }

        /// <summary>True when a signature matched one of the supplied secrets, in tolerance.</summary>
        public bool IsValid { get; }

        /// <summary>The scheme that was checked.</summary>
        public WebhookScheme Scheme { get; }

        /// <summary>
        /// Why it failed: <c>no_signature_header</c>, <c>malformed_signature</c>,
        /// <c>timestamp_out_of_tolerance</c>, <c>no_matching_signature</c>, <c>no_secret</c>.
        /// Null when it passed.
        /// </summary>
        public string? Reason { get; }

        /// <summary>The signing timestamp the header carried, when it could be read.</summary>
        public DateTimeOffset? Timestamp { get; }

        /// <summary>Throws <see cref="WebhookSignatureException"/> unless the delivery verified.</summary>
        public void EnsureValid()
        {
            if (!IsValid) throw new WebhookSignatureException(Reason ?? "invalid_signature", Scheme);
        }
    }

    /// <summary>Thrown when a webhook delivery fails signature verification.</summary>
    public sealed class WebhookSignatureException : Exception
    {
        internal WebhookSignatureException(string reason, WebhookScheme scheme)
            : base($"Webhook signature verification failed ({scheme}): {reason}.")
        {
            Reason = reason;
            Scheme = scheme;
        }

        /// <summary>The machine-readable failure reason.</summary>
        public string Reason { get; }

        /// <summary>The scheme that was checked.</summary>
        public WebhookScheme Scheme { get; }
    }

    /// <summary>
    /// Verifies the signature HostTracker puts on every webhook delivery.
    /// <para>
    /// Verify the raw request bytes, before any JSON parse or re-serialization - a reformatted body
    /// no longer matches its signature. Both schemes ride every delivery and differ in signed
    /// content, key derivation and encoding, so halves of the two must never be mixed.
    /// </para>
    /// <para>
    /// During a secret rotation the header carries two <c>v1</c> values for 24 hours. Pass both
    /// secrets; a match against either verifies the delivery.
    /// </para>
    /// </summary>
    public static class WebhookSignature
    {
        /// <summary>The default clock-skew tolerance: 300 seconds either way.</summary>
        public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(300);

        private static readonly char[] SignatureListSeparator = { ' ' };

        /// <summary>Verifies a delivery against one secret.</summary>
        /// <param name="headers">The delivery's request headers.</param>
        /// <param name="rawBody">The raw request body bytes, exactly as received.</param>
        /// <param name="secret">The webhook's signing secret, including its <c>whsec_</c> prefix.</param>
        /// <param name="tolerance">Clock-skew tolerance; default 300 seconds.</param>
        /// <param name="now">The instant to measure the timestamp against; default <see cref="DateTimeOffset.UtcNow"/>.</param>
        /// <param name="scheme">Which signature to check; default <see cref="WebhookScheme.Auto"/>.</param>
        public static WebhookVerificationResult Verify(
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
            byte[] rawBody,
            string secret,
            TimeSpan? tolerance = null,
            DateTimeOffset? now = null,
            WebhookScheme scheme = WebhookScheme.Auto) =>
            Verify(headers, rawBody, new[] { secret }, tolerance, now, scheme);

        /// <summary>Verifies a delivery against any of several secrets (key rotation).</summary>
        /// <param name="headers">The delivery's request headers.</param>
        /// <param name="rawBody">The raw request body bytes, exactly as received.</param>
        /// <param name="secrets">Every currently valid signing secret, each with its <c>whsec_</c> prefix.</param>
        /// <param name="tolerance">Clock-skew tolerance; default 300 seconds.</param>
        /// <param name="now">The instant to measure the timestamp against; default <see cref="DateTimeOffset.UtcNow"/>.</param>
        /// <param name="scheme">Which signature to check; default <see cref="WebhookScheme.Auto"/>.</param>
        public static WebhookVerificationResult Verify(
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
            byte[] rawBody,
            IEnumerable<string> secrets,
            TimeSpan? tolerance = null,
            DateTimeOffset? now = null,
            WebhookScheme scheme = WebhookScheme.Auto)
        {
            ArgumentNullException.ThrowIfNull(rawBody);
            var map = Flatten(headers);
            return Verify(map, rawBody, secrets, tolerance, now, scheme);
        }

        /// <summary>Verifies a delivery whose headers are already a simple name/value map.</summary>
        /// <param name="headers">The delivery's request headers (lookup is case-insensitive).</param>
        /// <param name="rawBody">The raw request body bytes, exactly as received.</param>
        /// <param name="secrets">Every currently valid signing secret, each with its <c>whsec_</c> prefix.</param>
        /// <param name="tolerance">Clock-skew tolerance; default 300 seconds.</param>
        /// <param name="now">The instant to measure the timestamp against; default <see cref="DateTimeOffset.UtcNow"/>.</param>
        /// <param name="scheme">Which signature to check; default <see cref="WebhookScheme.Auto"/>.</param>
        public static WebhookVerificationResult Verify(
            IReadOnlyDictionary<string, string> headers,
            byte[] rawBody,
            IEnumerable<string> secrets,
            TimeSpan? tolerance = null,
            DateTimeOffset? now = null,
            WebhookScheme scheme = WebhookScheme.Auto)
        {
            ArgumentNullException.ThrowIfNull(headers);
            ArgumentNullException.ThrowIfNull(rawBody);

            var keys = (secrets ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray();
            var window = tolerance ?? DefaultTolerance;
            var at = now ?? DateTimeOffset.UtcNow;

            var htHeader = Get(headers, SdkHeaders.HtSignature);
            var swHeader = Get(headers, SdkHeaders.WebhookSignature);

            var chosen = scheme switch
            {
                WebhookScheme.HostTracker => WebhookScheme.HostTracker,
                WebhookScheme.StandardWebhooks => WebhookScheme.StandardWebhooks,
                _ => htHeader is not null ? WebhookScheme.HostTracker
                    : swHeader is not null ? WebhookScheme.StandardWebhooks
                    : WebhookScheme.HostTracker,
            };

            if (keys.Length == 0)
                return new WebhookVerificationResult(false, chosen, "no_secret", null);

            return chosen == WebhookScheme.HostTracker
                ? VerifyHostTracker(htHeader, rawBody, keys, window, at)
                : VerifyStandard(swHeader, Get(headers, SdkHeaders.WebhookId),
                    Get(headers, SdkHeaders.WebhookTimestamp), rawBody, keys, window, at);
        }

        private static WebhookVerificationResult VerifyHostTracker(
            string? header, byte[] rawBody, string[] secrets, TimeSpan tolerance, DateTimeOffset now)
        {
            const WebhookScheme scheme = WebhookScheme.HostTracker;
            if (string.IsNullOrWhiteSpace(header))
                return new WebhookVerificationResult(false, scheme, "no_signature_header", null);

            string? t = null;
            var signatures = new List<string>();
            foreach (var part in header!.Split(','))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var name = part.Substring(0, eq).Trim();
                var value = part.Substring(eq + 1).Trim();
                if (string.Equals(name, "t", StringComparison.Ordinal)) t ??= value;
                else if (string.Equals(name, "v1", StringComparison.Ordinal)) signatures.Add(value);
            }

            if (t is null || signatures.Count == 0 ||
                !long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
                return new WebhookVerificationResult(false, scheme, "malformed_signature", null);

            var stamped = DateTimeOffset.FromUnixTimeSeconds(unix);
            if (tolerance > TimeSpan.Zero && Abs(now - stamped) > tolerance)
                return new WebhookVerificationResult(false, scheme, "timestamp_out_of_tolerance", stamped);

            // signed = "<t>" + "." + <raw body bytes>
            var prefix = Encoding.UTF8.GetBytes(t + ".");
            var signed = Concat(prefix, rawBody);

            foreach (var secret in secrets)
            {
                // The HMAC key is the UTF-8 of the whole secret, whsec_ prefix included.
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var expected = hmac.ComputeHash(signed);
                foreach (var candidate in signatures)
                {
                    if (!TryParseHex(candidate, out var actual)) continue;
                    if (actual.Length == expected.Length &&
                        CryptographicOperations.FixedTimeEquals(expected, actual))
                        return new WebhookVerificationResult(true, scheme, null, stamped);
                }
            }

            return new WebhookVerificationResult(false, scheme, "no_matching_signature", stamped);
        }

        private static WebhookVerificationResult VerifyStandard(
            string? header, string? id, string? timestamp, byte[] rawBody,
            string[] secrets, TimeSpan tolerance, DateTimeOffset now)
        {
            const WebhookScheme scheme = WebhookScheme.StandardWebhooks;
            if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(timestamp))
                return new WebhookVerificationResult(false, scheme, "no_signature_header", null);
            if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
                return new WebhookVerificationResult(false, scheme, "malformed_signature", null);

            var stamped = DateTimeOffset.FromUnixTimeSeconds(unix);
            if (tolerance > TimeSpan.Zero && Abs(now - stamped) > tolerance)
                return new WebhookVerificationResult(false, scheme, "timestamp_out_of_tolerance", stamped);

            var signatures = header!
                .Split(SignatureListSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    var comma = part.IndexOf(',');
                    return comma < 0 ? part : part.Substring(comma + 1);
                })
                .Where(s => s.Length > 0)
                .ToArray();
            if (signatures.Length == 0)
                return new WebhookVerificationResult(false, scheme, "malformed_signature", stamped);

            // signed = "<id>.<timestamp>." + <raw body bytes>
            var prefix = Encoding.UTF8.GetBytes(id + "." + timestamp + ".");
            var signed = Concat(prefix, rawBody);

            foreach (var secret in secrets)
            {
                using var hmac = new HMACSHA256(StandardKey(secret));
                var expected = Convert.ToBase64String(hmac.ComputeHash(signed));
                var expectedBytes = Encoding.UTF8.GetBytes(expected);
                foreach (var candidate in signatures)
                {
                    var actual = Encoding.UTF8.GetBytes(candidate);
                    if (actual.Length == expectedBytes.Length &&
                        CryptographicOperations.FixedTimeEquals(expectedBytes, actual))
                        return new WebhookVerificationResult(true, scheme, null, stamped);
                }
            }

            return new WebhookVerificationResult(false, scheme, "no_matching_signature", stamped);
        }

        /// <summary>
        /// The Standard Webhooks key derivation: drop the <c>whsec_</c> prefix, then base64-decode
        /// the remainder. A remainder that is not valid base64 is used as its own UTF-8 bytes.
        /// </summary>
        internal static byte[] StandardKey(string secret)
        {
            var body = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret.Substring(6) : secret;
            try { return Convert.FromBase64String(body); }
            catch (FormatException) { return Encoding.UTF8.GetBytes(body); }
        }

        private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? -value : value;

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        private static bool TryParseHex(string value, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (value.Length == 0 || value.Length % 2 != 0) return false;
            var result = new byte[value.Length / 2];
            for (var i = 0; i < result.Length; i++)
            {
                if (!byte.TryParse(value.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out result[i]))
                    return false;
            }
            bytes = result;
            return true;
        }

        private static Dictionary<string, string> Flatten(
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers is null) return map;
            foreach (var kv in headers)
            {
                if (kv.Value is null) continue;
                foreach (var v in kv.Value) { map[kv.Key] = v; break; }
            }
            return map;
        }

        private static string? Get(IReadOnlyDictionary<string, string> headers, string name)
        {
            if (headers.TryGetValue(name, out var direct)) return direct;
            foreach (var kv in headers)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
    }
}
