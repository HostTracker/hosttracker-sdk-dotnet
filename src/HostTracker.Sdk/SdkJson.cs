using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostTracker.Sdk
{
    /// <summary>
    /// The one JSON configuration the SDK uses - for the generated clients and for the few places
    /// the hand-written layer reads a body itself (the instant-check poll, webhook envelopes).
    /// </summary>
    internal static class SdkJson
    {
        internal static readonly JsonSerializerOptions Default = Create();

        internal static JsonSerializerOptions Create()
        {
            var options = new JsonSerializerOptions();
            Configure(options);
            return options;
        }

        internal static void Configure(JsonSerializerOptions options)
        {
            // A PATCH body's ABSENT member means "leave alone"; an explicit null means "clear".
            // Writing every unset property as null would wipe the resource on the first update.
            // Sending a deliberate null is what HostTrackerClient.SendJsonAsync is for.
            options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.PropertyNameCaseInsensitive = true;
            options.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        }
    }
}
