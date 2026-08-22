using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HostTracker.Sdk.Tests
{
    /// <summary>A recording transport: canned answers in, the requests that produced them out.</summary>
    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _answers = new();

        public List<RecordedRequest> Requests { get; } = new();

        public StubHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> answer)
        {
            _answers.Enqueue(answer);
            return this;
        }

        public StubHandler Json(HttpStatusCode status, string body,
            params (string Name, string Value)[] headers) =>
            Enqueue(_ => Build(status, body, "application/json", headers));

        public StubHandler Problem(HttpStatusCode status, string body,
            params (string Name, string Value)[] headers) =>
            Enqueue(_ => Build(status, body, "application/problem+json", headers));

        public StubHandler Raw(HttpStatusCode status, string body, string contentType,
            params (string Name, string Value)[] headers) =>
            Enqueue(_ => Build(status, body, contentType, headers));

        public StubHandler Throws(Exception exception) => Enqueue(_ => throw exception);

        /// <summary>An answer that takes <paramref name="takes"/> to arrive, honouring cancellation.</summary>
        public StubHandler Slow(TimeSpan takes, HttpStatusCode status, string body)
        {
            _slow.Enqueue(takes);
            return Enqueue(_ => Build(status, body, "application/json", Array.Empty<(string, string)>()));
        }

        private readonly Queue<TimeSpan> _slow = new();

        private static HttpResponseMessage Build(HttpStatusCode status, string body, string contentType,
            (string Name, string Value)[] headers)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            };
            foreach (var (name, value) in headers)
            {
                if (!response.Headers.TryAddWithoutValidation(name, value))
                    response.Content.Headers.TryAddWithoutValidation(name, value);
            }
            return response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The real transport refuses a non-http scheme; the stub must too, or a rooted path that
            // resolved to file:// would pass the unit tests and fail only on Linux.
            if (request.RequestUri is null || (request.RequestUri.Scheme != Uri.UriSchemeHttp &&
                                               request.RequestUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new NotSupportedException(
                    $"The '{request.RequestUri?.Scheme}' scheme is not supported ({request.RequestUri}).");
            }

            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                body));

            if (_slow.Count > 0)
                await Task.Delay(_slow.Dequeue(), cancellationToken).ConfigureAwait(false);

            if (_answers.Count == 0)
                throw new InvalidOperationException(
                    $"StubHandler ran out of answers at request #{Requests.Count}: {request.Method} {request.RequestUri}");
            return _answers.Dequeue()(request);
        }
    }

    internal sealed record RecordedRequest(
        string Method,
        Uri Uri,
        IReadOnlyDictionary<string, string[]> Headers,
        string? Body)
    {
        public string? Header(string name) =>
            Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
    }

    internal static class TestClient
    {
        public const string BaseUrl = "https://api2.example.test";

        public static (HostTrackerClient Client, StubHandler Handler, List<TimeSpan> Delays) Create(
            Action<HostTrackerOptions>? configure = null)
        {
            var handler = new StubHandler();
            var delays = new List<TimeSpan>();
            var options = new HostTrackerOptions
            {
                Token = "test-token",
                BaseUrl = BaseUrl,
                Handler = handler,
                Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; },
            };
            configure?.Invoke(options);
            return (new HostTrackerClient(options), handler, delays);
        }
    }
}
