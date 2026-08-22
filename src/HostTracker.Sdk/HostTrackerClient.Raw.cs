using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HostTracker.Sdk.Http;

namespace HostTracker.Sdk
{
    public sealed partial class HostTrackerClient
    {
        private static readonly JsonSerializerOptions RawOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            // Deliberately NOT WhenWritingNull: this door exists precisely to send explicit nulls.
        };

        /// <summary>
        /// The escape hatch: one request against any path, with the body serialized exactly as
        /// given, through the same pipeline as every typed call (auth, user-agent, idempotency,
        /// retry, error mapping).
        /// <para>
        /// Two cases need it. An <b>explicit null</b> in a PATCH body (absent means "leave alone",
        /// null means "clear"), which the typed request objects cannot express - send
        /// <c>new Dictionary&lt;string, object?&gt; { ["defaultAgentPools"] = null }</c> here
        /// instead. And an endpoint or member <b>newer than this SDK build</b>, which can be called
        /// directly and read as JSON.
        /// </para>
        /// </summary>
        /// <typeparam name="TResponse">The shape to deserialize the answer into - a generated view, or <see cref="JsonElement"/>.</typeparam>
        /// <param name="method">The HTTP method.</param>
        /// <param name="path">Root-relative path with its query, e.g. <c>/monitor?limit=2</c>.</param>
        /// <param name="body">The request body; null sends none.</param>
        /// <param name="idempotencyKey">An explicit key. Omit it and the usual auto-key policy applies.</param>
        /// <param name="cancellationToken">Stops the request.</param>
        public async Task<TResponse> SendJsonAsync<TResponse>(
            HttpMethod method,
            string path,
            object? body = null,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(method);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            // Not `TryCreate(..., Absolute)`: on Linux a rooted path is a valid file:// URI. See SdkUri.
            var uri = SdkUri.Resolve(BaseUrl, path, nameof(path));
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            if (idempotencyKey is not null)
                request.Headers.TryAddWithoutValidation(SdkHeaders.IdempotencyKey, idempotencyKey);
            if (body is not null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body, RawOptions), Encoding.UTF8, "application/json");
            }

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.Content is null || response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return default!;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var value = await JsonSerializer
                .DeserializeAsync<TResponse>(stream, SdkJson.Default, cancellationToken)
                .ConfigureAwait(false);
            return value!;
        }

        /// <summary>
        /// <see cref="SendJsonAsync{TResponse}"/> for callers that just want the JSON back.
        /// </summary>
        /// <param name="method">The HTTP method.</param>
        /// <param name="path">Root-relative path with its query.</param>
        /// <param name="body">The request body; null sends none.</param>
        /// <param name="idempotencyKey">An explicit key. Omit it and the usual auto-key policy applies.</param>
        /// <param name="cancellationToken">Stops the request.</param>
        public Task<JsonElement> SendJsonAsync(
            HttpMethod method,
            string path,
            object? body = null,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default) =>
            SendJsonAsync<JsonElement>(method, path, body, idempotencyKey, cancellationToken);
    }
}
