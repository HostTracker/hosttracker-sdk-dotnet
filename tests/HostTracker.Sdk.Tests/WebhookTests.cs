using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HostTracker.Sdk.Generated;
using HostTracker.Sdk.Http;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    public class WebhookTests
    {
        private const string Secret = "whsec_c2VjcmV0LXZhbHVlLWZvci10ZXN0cw==";
        private const string OtherSecret = "whsec_b3RoZXItc2VjcmV0LWZvci10ZXN0cw==";

        // A body with a non-ASCII character, so a UTF-8 mistake cannot pass unnoticed.
        private const string Body = """
        {"id":"d_5b1f4e0c9a2d4f7b8c1e2a3b4c5d6e7f","event":"monitor.down","occurredAt":1735689600,
         "apiVersion":"v2","data":{"monitor":{"id":"4e49d7a2-4ab5-45e2-b9f8-1d59f505ad45",
         "name":"Marketing sitê","url":"https://example.com"},"state":"down"}}
        """;

        private static byte[] Raw => Encoding.UTF8.GetBytes(Body);

        /// <summary>The documented HT algorithm, written out here rather than reused from the SDK.</summary>
        private static string HtSignature(long t, params string[] secrets)
        {
            var parts = new List<string> { "t=" + t.ToString(CultureInfo.InvariantCulture) };
            foreach (var secret in secrets)
            {
                var signed = Encoding.UTF8.GetBytes(t.ToString(CultureInfo.InvariantCulture) + ".");
                var payload = new byte[signed.Length + Raw.Length];
                Buffer.BlockCopy(signed, 0, payload, 0, signed.Length);
                Buffer.BlockCopy(Raw, 0, payload, signed.Length, Raw.Length);
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                parts.Add("v1=" + Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant());
            }
            return string.Join(",", parts);
        }

        /// <summary>The documented Standard Webhooks algorithm, likewise written out here.</summary>
        private static string StandardSignature(string id, long t, string secret)
        {
            var prefix = Encoding.UTF8.GetBytes(id + "." + t.ToString(CultureInfo.InvariantCulture) + ".");
            var payload = new byte[prefix.Length + Raw.Length];
            Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
            Buffer.BlockCopy(Raw, 0, payload, prefix.Length, Raw.Length);
            var key = Convert.FromBase64String(secret.Substring("whsec_".Length));
            using var hmac = new HMACSHA256(key);
            return "v1," + Convert.ToBase64String(hmac.ComputeHash(payload));
        }

        private static Dictionary<string, string> Headers(params (string, string)[] pairs)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in pairs) map[k] = v;
            return map;
        }

        private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1735689600);

        [Fact]
        public void A_correct_HT_signature_verifies()
        {
            var headers = Headers((SdkHeaders.HtSignature, HtSignature(Now.ToUnixTimeSeconds(), Secret)));

            var result = WebhookSignature.Verify(headers, Raw, new[] { Secret }, now: Now);

            Assert.True(result.IsValid);
            Assert.Equal(WebhookScheme.HostTracker, result.Scheme);
            Assert.Null(result.Reason);
            Assert.Equal(Now, result.Timestamp);
        }

        [Fact]
        public void A_signature_from_the_wrong_secret_is_refused()
        {
            var headers = Headers((SdkHeaders.HtSignature, HtSignature(Now.ToUnixTimeSeconds(), OtherSecret)));

            var result = WebhookSignature.Verify(headers, Raw, new[] { Secret }, now: Now);

            Assert.False(result.IsValid);
            Assert.Equal("no_matching_signature", result.Reason);
        }

        [Fact]
        public void A_tampered_body_is_refused()
        {
            var headers = Headers((SdkHeaders.HtSignature, HtSignature(Now.ToUnixTimeSeconds(), Secret)));
            var tampered = Encoding.UTF8.GetBytes(Body.Replace("\"down\"", "\"up\"", StringComparison.Ordinal));

            var result = WebhookSignature.Verify(headers, tampered, new[] { Secret }, now: Now);

            Assert.False(result.IsValid);
            Assert.Equal("no_matching_signature", result.Reason);
        }

        [Fact]
        public void A_stale_timestamp_is_refused_even_with_a_correct_signature()
        {
            var stale = Now.AddSeconds(-301).ToUnixTimeSeconds();
            var headers = Headers((SdkHeaders.HtSignature, HtSignature(stale, Secret)));

            var result = WebhookSignature.Verify(headers, Raw, new[] { Secret }, now: Now);

            Assert.False(result.IsValid);
            Assert.Equal("timestamp_out_of_tolerance", result.Reason);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(stale), result.Timestamp);
        }

        [Fact]
        public void A_timestamp_inside_the_tolerance_verifies()
        {
            var recent = Now.AddSeconds(-299).ToUnixTimeSeconds();
            var headers = Headers((SdkHeaders.HtSignature, HtSignature(recent, Secret)));

            Assert.True(WebhookSignature.Verify(headers, Raw, new[] { Secret }, now: Now).IsValid);
        }

        [Fact]
        public void A_widened_tolerance_accepts_an_older_delivery()
        {
            var old = Now.AddHours(-1).ToUnixTimeSeconds();
            var headers = Headers((SdkHeaders.HtSignature, HtSignature(old, Secret)));

            Assert.True(WebhookSignature
                .Verify(headers, Raw, new[] { Secret }, TimeSpan.FromHours(2), Now).IsValid);
        }

        [Fact]
        public void During_rotation_two_v1_values_ride_along_and_either_secret_verifies()
        {
            var header = HtSignature(Now.ToUnixTimeSeconds(), OtherSecret, Secret);
            Assert.Equal(3, header.Split(',').Length);
            var headers = Headers((SdkHeaders.HtSignature, header));

            Assert.True(WebhookSignature.Verify(headers, Raw, new[] { Secret }, now: Now).IsValid);
            Assert.True(WebhookSignature.Verify(headers, Raw, new[] { OtherSecret }, now: Now).IsValid);
            Assert.True(WebhookSignature.Verify(headers, Raw, new[] { OtherSecret, Secret }, now: Now).IsValid);
            Assert.False(WebhookSignature.Verify(headers, Raw, new[] { "whsec_" + Convert.ToBase64String(
                Encoding.UTF8.GetBytes("nope")) }, now: Now).IsValid);
        }

        [Fact]
        public void The_Standard_Webhooks_triple_verifies_with_its_own_key_derivation()
        {
            const string id = "d_5b1f4e0c9a2d4f7b8c1e2a3b4c5d6e7f";
            var t = Now.ToUnixTimeSeconds();
            var headers = Headers(
                (SdkHeaders.WebhookId, id),
                (SdkHeaders.WebhookTimestamp, t.ToString(CultureInfo.InvariantCulture)),
                (SdkHeaders.WebhookSignature, StandardSignature(id, t, Secret)));

            var result = WebhookSignature.Verify(headers, Raw, new[] { Secret }, now: Now);

            Assert.True(result.IsValid);
            Assert.Equal(WebhookScheme.StandardWebhooks, result.Scheme);
        }

        [Fact]
        public void The_two_schemes_do_not_accept_each_others_signatures()
        {
            const string id = "d_5b1f4e0c9a2d4f7b8c1e2a3b4c5d6e7f";
            var t = Now.ToUnixTimeSeconds();

            // The Standard triple, checked under the HT scheme: no HT-Signature at all.
            var swOnly = Headers(
                (SdkHeaders.WebhookId, id),
                (SdkHeaders.WebhookTimestamp, t.ToString(CultureInfo.InvariantCulture)),
                (SdkHeaders.WebhookSignature, StandardSignature(id, t, Secret)));
            Assert.Equal("no_signature_header",
                WebhookSignature.Verify(swOnly, Raw, new[] { Secret }, now: Now, scheme: WebhookScheme.HostTracker).Reason);

            // The HT header's hex digest is not the Standard base64 one over the same secret.
            var mixed = Headers(
                (SdkHeaders.WebhookId, id),
                (SdkHeaders.WebhookTimestamp, t.ToString(CultureInfo.InvariantCulture)),
                (SdkHeaders.WebhookSignature, "v1," + HtSignature(t, Secret).Split("v1=")[1]));
            Assert.False(WebhookSignature
                .Verify(mixed, Raw, new[] { Secret }, now: Now, scheme: WebhookScheme.StandardWebhooks).IsValid);
        }

        [Fact]
        public void Auto_prefers_the_HT_header_when_both_ride_along()
        {
            const string id = "d_5b1f4e0c9a2d4f7b8c1e2a3b4c5d6e7f";
            var t = Now.ToUnixTimeSeconds();
            var headers = Headers(
                (SdkHeaders.HtSignature, HtSignature(t, Secret)),
                (SdkHeaders.WebhookId, id),
                (SdkHeaders.WebhookTimestamp, t.ToString(CultureInfo.InvariantCulture)),
                (SdkHeaders.WebhookSignature, StandardSignature(id, t, Secret)));

            var result = WebhookSignature.Verify(headers, Raw, new[] { Secret }, now: Now);

            Assert.True(result.IsValid);
            Assert.Equal(WebhookScheme.HostTracker, result.Scheme);
        }

        [Fact]
        public void A_missing_header_and_a_missing_secret_are_both_named()
        {
            Assert.Equal("no_signature_header",
                WebhookSignature.Verify(Headers(), Raw, new[] { Secret }, now: Now).Reason);
            Assert.Equal("no_secret", WebhookSignature.Verify(
                Headers((SdkHeaders.HtSignature, HtSignature(Now.ToUnixTimeSeconds(), Secret))),
                Raw, Array.Empty<string>(), now: Now).Reason);
        }

        [Fact]
        public void A_malformed_header_is_named_rather_than_silently_failing()
        {
            Assert.Equal("malformed_signature", WebhookSignature.Verify(
                Headers((SdkHeaders.HtSignature, "t=notanumber,v1=abcd")), Raw, new[] { Secret }, now: Now).Reason);
            Assert.Equal("malformed_signature", WebhookSignature.Verify(
                Headers((SdkHeaders.HtSignature, "v1=abcd")), Raw, new[] { Secret }, now: Now).Reason);
        }

        [Fact]
        public void EnsureValid_throws_with_the_reason_attached()
        {
            var result = WebhookSignature.Verify(Headers(), Raw, new[] { Secret }, now: Now);
            var ex = Assert.Throws<WebhookSignatureException>(result.EnsureValid);
            Assert.Equal("no_signature_header", ex.Reason);
        }

        [Fact]
        public void The_envelope_parses_into_its_named_members_and_a_typed_payload()
        {
            var evt = WebhookEvent.Parse(Body);

            Assert.Equal("d_5b1f4e0c9a2d4f7b8c1e2a3b4c5d6e7f", evt.Id);
            Assert.Equal(WebhookEvents.MonitorDown, evt.Event);
            Assert.Equal(1735689600, evt.OccurredAt);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1735689600), evt.OccurredAtUtc);
            Assert.Equal("v2", evt.ApiVersion);
            Assert.Equal(typeof(WebhookMonitorAlert), evt.DataType);

            var alert = evt.DataAs<WebhookMonitorAlert>();
            Assert.NotNull(alert);
            Assert.Equal("Marketing sitê", alert!.Monitor!.Name);
        }

        [Fact]
        public void An_event_this_build_predates_still_parses_with_its_data_readable()
        {
            var evt = WebhookEvent.Parse(
                """{"id":"d_1","event":"monitor.somethingNew","occurredAt":1,"apiVersion":"v2","data":{"x":1}}""");

            Assert.Equal("monitor.somethingNew", evt.Event);
            Assert.Null(evt.DataType);
            Assert.Equal(1, evt.Data.GetProperty("x").GetInt32());
        }

        [Fact]
        public void A_body_that_is_not_an_envelope_is_refused()
        {
            Assert.Throws<FormatException>(() => WebhookEvent.Parse("""{"hello":"world"}"""));
            Assert.Throws<FormatException>(() => WebhookEvent.Parse("not json"));
            Assert.False(WebhookEvent.TryParse(Encoding.UTF8.GetBytes("not json"), out _));
        }
    }
}
