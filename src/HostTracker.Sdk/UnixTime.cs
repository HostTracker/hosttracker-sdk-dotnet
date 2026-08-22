using System;

namespace HostTracker.Sdk
{
    /// <summary>
    /// Converts between the wire's Unix seconds and .NET instants. The API speaks Unix seconds in
    /// both directions - never ISO-8601, never milliseconds - and the wire types keep the integers.
    /// Members whose name ends in <c>Ms</c> (a delivery's <c>latencyMs</c>) are elapsed
    /// milliseconds, not instants, and do not belong here.
    /// </summary>
    public static class UnixTime
    {
        /// <summary>Reads a wire timestamp as a UTC instant.</summary>
        public static DateTimeOffset ToDateTimeOffset(long unixSeconds) =>
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        /// <summary>Reads an optional wire timestamp as a UTC instant.</summary>
        public static DateTimeOffset? ToDateTimeOffset(long? unixSeconds) =>
            unixSeconds is long s ? DateTimeOffset.FromUnixTimeSeconds(s) : (DateTimeOffset?)null;

        /// <summary>Reads a wire timestamp as a UTC <see cref="DateTime"/>.</summary>
        public static DateTime ToUtcDateTime(long unixSeconds) =>
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

        /// <summary>Writes an instant as a wire timestamp.</summary>
        public static long FromDateTimeOffset(DateTimeOffset value) => value.ToUnixTimeSeconds();

        /// <summary>Writes an optional instant as a wire timestamp.</summary>
        public static long? FromDateTimeOffset(DateTimeOffset? value) =>
            value is DateTimeOffset v ? v.ToUnixTimeSeconds() : (long?)null;

        /// <summary>Writes a <see cref="DateTime"/> as a wire timestamp, treating Unspecified as UTC.</summary>
        public static long FromDateTime(DateTime value) =>
            new DateTimeOffset(value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime()).ToUnixTimeSeconds();
    }
}
