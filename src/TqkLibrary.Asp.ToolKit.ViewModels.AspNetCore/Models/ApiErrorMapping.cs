using Microsoft.Extensions.Logging;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Models
{
    /// <summary>Result of <see cref="Interfaces.IApiExceptionMapper.Map"/>.</summary>
    public sealed record ApiErrorMapping(int StatusCode, string Code, string Message, Dictionary<string, object>? Details = null)
    {
        /// <summary>Per-field errors, written to <see cref="ApiError.Fields"/>.</summary>
        public List<FieldError>? Fields { get; init; }

        /// <summary>Written to <see cref="ApiError.Retryable"/>.</summary>
        public bool? Retryable { get; init; }

        /// <summary>Response headers to set with the error (e.g. <c>Retry-After</c>).</summary>
        public Dictionary<string, string>? Headers { get; init; }

        /// <summary>
        /// Log level of this error. Null (default): Error for 5xx, Warning otherwise. The exception is attached to the
        /// log entry when the level is Error or higher.
        /// </summary>
        public LogLevel? LogLevel { get; init; }
    }
}
