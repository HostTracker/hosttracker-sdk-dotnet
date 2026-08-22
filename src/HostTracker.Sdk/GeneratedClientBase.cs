using System.Text.Json;

namespace HostTracker.Sdk
{
    /// <summary>
    /// Base class of the generated per-tag clients. It owns the JSON settings in one place so a
    /// regeneration cannot leave one family on different rules.
    /// </summary>
    public abstract class GeneratedClientBase
    {
        /// <summary>
        /// Configures the serializer the generated clients use.
        /// </summary>
        /// <param name="settings">The options instance to configure.</param>
        /// <remarks>
        /// Unset properties are omitted: in a PATCH body an absent member means "leave alone" and an
        /// explicit null means "clear". Send a deliberate null through the request object's
        /// <c>AdditionalProperties</c>, which is written verbatim.
        /// </remarks>
        protected static void UpdateJsonSerializerSettings(JsonSerializerOptions settings) =>
            SdkJson.Configure(settings);
    }
}
