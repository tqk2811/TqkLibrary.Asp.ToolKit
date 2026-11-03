using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Middleware
{
    /// <summary>
    /// Takes the request id from <see cref="TqkApiEnvelopeOptions.RequestIdHeader"/> (or generates one), stores it for
    /// <see cref="HttpContextRequestIdExtensions.GetRequestId"/> and echoes it on the response header.
    /// </summary>
    public sealed class RequestIdMiddleware
    {
        /// <summary>
        /// Longer incoming ids, and ids with non-printable or non-ASCII characters, are ignored and a new one is generated.
        /// </summary>
        public const int MaxIncomingLength = 128;

        readonly RequestDelegate _next;
        readonly IOptions<TqkApiEnvelopeOptions> _options;

        public RequestIdMiddleware(RequestDelegate next, IOptions<TqkApiEnvelopeOptions> options)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(_options.Value.RequestIdHeader))
                throw new InvalidOperationException($"{nameof(TqkApiEnvelopeOptions)}.{nameof(TqkApiEnvelopeOptions.RequestIdHeader)} must not be null or empty.");
        }

        public Task InvokeAsync(HttpContext context)
        {
            TqkApiEnvelopeOptions options = _options.Value;
            string? incoming = context.Request.Headers[options.RequestIdHeader].FirstOrDefault();
            string requestId = IsValid(incoming) ? incoming! : options.RequestIdPrefix + Guid.NewGuid().ToString("N");

            context.Items[HttpContextRequestIdExtensions.RequestIdItemKey] = requestId;
            context.Response.Headers[options.RequestIdHeader] = requestId;
            return _next(context);
        }

        /// <summary>Only printable ASCII is echoed: anything else would be rejected (or worse) when written as a header.</summary>
        static bool IsValid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIncomingLength) return false;
            foreach (char c in value)
            {
                if (c < 0x21 || c > 0x7E) return false;
            }
            return true;
        }
    }
}
