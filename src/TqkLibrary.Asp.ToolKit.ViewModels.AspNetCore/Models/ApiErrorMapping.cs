using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Models
{
    /// <summary>Result of <see cref="Interfaces.IApiExceptionMapper.Map"/>.</summary>
    public sealed record ApiErrorMapping(int StatusCode, string Code, string Message, Dictionary<string, object>? Details = null)
    {
        /// <summary>Per-field errors, written to <see cref="ApiError.Fields"/>.</summary>
        public List<FieldError>? Fields { get; init; }

        /// <summary>Written to <see cref="ApiError.Retryable"/>.</summary>
        public bool? Retryable { get; init; }

        /// <summary>
        /// Response headers to set with the error (e.g. <c>Retry-After</c>), several values allowed. An empty value removes
        /// a header set earlier in the pipeline. <c>Content-Type</c> cannot be overridden (the body is always JSON).
        /// Prefer <see cref="WithHeader"/> to add one, it keeps the existing headers and ignores name case.
        /// </summary>
        public IReadOnlyDictionary<string, StringValues>? Headers { get; init; }

        /// <summary>Copy of this mapping with header <paramref name="name"/> set (replaced whatever its case).</summary>
        public ApiErrorMapping WithHeader(string name, StringValues value)
        {
            Dictionary<string, StringValues> headers = new(StringComparer.OrdinalIgnoreCase);
            if (Headers is not null)
            {
                foreach (KeyValuePair<string, StringValues> header in Headers)
                    headers[header.Key] = header.Value;
            }
            headers[name] = value;
            return this with { Headers = headers };
        }

        /// <summary>
        /// Log level of this error. Null (default): Error for 5xx, Warning otherwise. The exception is attached to the
        /// log entry when the level is Error or higher.
        /// </summary>
        public LogLevel? LogLevel { get; init; }
    }
}
