using System;
using System.Collections.Generic;
using System.Linq;

namespace GrowthBook
{
    /// <summary>
    /// Rejects request headers the SDK manages itself and a streaming host that is not a usable URL.
    /// </summary>
    /// <remarks>
    /// Shared because there are two ways in. A <see cref="Context"/> is the ordinary one, but
    /// <see cref="Api.FeatureRefreshWorker"/> is public and takes a
    /// <see cref="GrowthBookConfigurationOptions"/> straight from the caller. Validating only the first
    /// left the second free to send a caller's own <c>If-None-Match</c> before anything was cached, or
    /// to retry a streaming URL that can never resolve.
    /// </remarks>
    internal static class HeaderAndStreamingHostValidation
    {
        private static readonly HashSet<string> ReservedHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "User-Agent",
            "If-None-Match",
            "Cache-Control"
        };

        public static void Validate(
            IDictionary<string, string> apiHostRequestHeaders,
            IDictionary<string, string> streamingHostRequestHeaders,
            string streamingHost,
            string parameterName)
        {
            if (HasReservedHeader(apiHostRequestHeaders, out var reservedName))
            {
                throw new ArgumentException($"'{reservedName}' is a reserved header managed by the SDK and cannot be set via ApiHostRequestHeaders", parameterName);
            }

            if (HasReservedHeader(streamingHostRequestHeaders, out reservedName))
            {
                throw new ArgumentException($"'{reservedName}' is a reserved header managed by the SDK and cannot be set via StreamingHostRequestHeaders", parameterName);
            }

            if (!string.IsNullOrWhiteSpace(streamingHost) && !IsValidHttpUrl(streamingHost))
            {
                throw new ArgumentException($"StreamingHost '{streamingHost}' is not a valid absolute http or https URL", parameterName);
            }
        }

        private static bool HasReservedHeader(IDictionary<string, string> headers, out string reservedName)
        {
            reservedName = headers?.Keys.FirstOrDefault(ReservedHeaderNames.Contains);

            return reservedName != null;
        }

        private static bool IsValidHttpUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
