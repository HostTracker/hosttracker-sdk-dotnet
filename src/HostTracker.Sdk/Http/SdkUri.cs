using System;

namespace HostTracker.Sdk.Http
{
    /// <summary>
    /// Turns a path or a server-supplied URL into the absolute URI to dial, and refuses anything
    /// that is not http(s).
    /// </summary>
    /// <remarks>
    /// On Linux and macOS a rooted path such as <c>/monitor</c> parses as an absolute
    /// <c>file:///monitor</c> URI, and <c>new Uri(baseUrl, value)</c> keeps a scheme carried inside
    /// <c>value</c>. Both are why the scheme of the resolved URI is checked, not of the input.
    /// </remarks>
    internal static class SdkUri
    {
        /// <summary>
        /// Resolves <paramref name="value"/> - an absolute http(s) URL, a rooted path, or a relative
        /// one - against <paramref name="baseUrl"/>. An absolute http(s) URL wins; anything that
        /// resolves to another scheme is refused.
        /// </summary>
        /// <exception cref="ArgumentException">The value does not resolve to an http(s) URL.</exception>
        internal static Uri Resolve(Uri baseUrl, string value, string paramName)
        {
            if (!TryResolve(baseUrl, value, out var uri))
            {
                throw new ArgumentException(
                    $"'{value}' is neither a path under {baseUrl} nor an absolute http(s) URL.", paramName);
            }
            return uri!;
        }

        /// <summary>
        /// Resolves a URL the server handed back, pinned to the client's own origin: the path and
        /// query come from <paramref name="value"/> verbatim, the scheme and host stay those of
        /// <paramref name="baseUrl"/>. A quoted canonical host, or any other origin, therefore never
        /// receives the bearer token, and a client pointed at a proxy keeps talking to it.
        /// </summary>
        /// <remarks>
        /// The rebase leaves a same-origin URL unchanged. A base URL carrying a path prefix has that
        /// prefix restored by <see cref="BasePathHandler"/>, exactly as for a rooted path.
        /// </remarks>
        internal static bool TryResolveOnBase(Uri baseUrl, string value, out Uri? resolved)
        {
            resolved = null;
            if (!TryResolve(baseUrl, value, out var candidate)) return false;

            // PathAndQuery is the escaped form, and drops any fragment. A protocol-relative value
            // ("//other.example/x") resolves to a foreign origin too, so it is rebased here as well.
            resolved = new Uri(baseUrl, candidate!.PathAndQuery);
            return true;
        }

        /// <summary>The non-throwing form of <see cref="Resolve"/>, without the origin pin.</summary>
        internal static bool TryResolve(Uri baseUrl, string value, out Uri? resolved)
        {
            resolved = null;
            ArgumentNullException.ThrowIfNull(baseUrl);
            if (string.IsNullOrWhiteSpace(value)) return false;

            Uri candidate;
            if (IsHttpUrl(value, out var absolute))
            {
                candidate = absolute!;
            }
            else if (!Uri.TryCreate(baseUrl, value, out var combined))
            {
                return false;
            }
            else
            {
                candidate = combined;
            }

            // The resolved result is what gets dialled, so it is what must be http(s): a value
            // carrying its own scheme survives relative resolution untouched.
            if (!IsHttpScheme(candidate)) return false;

            resolved = candidate;
            return true;
        }

        /// <summary>
        /// True only for a well-formed absolute <c>http://</c> or <c>https://</c> URL. The scheme is
        /// checked twice: by prefix before parsing, then on the parsed result.
        /// </summary>
        internal static bool IsHttpUrl(string? value, out Uri? absolute)
        {
            absolute = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (!value!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || !IsHttpScheme(parsed))
                return false;
            absolute = parsed;
            return true;
        }

        private static bool IsHttpScheme(Uri uri) =>
            string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
    }
}
