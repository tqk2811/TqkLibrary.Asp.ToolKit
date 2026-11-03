namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    /// <summary>Error body inside <see cref="ApiErrorResponse"/>.</summary>
    public sealed class ApiError
    {
        /// <summary>Machine readable code, see <see cref="ApiErrorCodes"/>.</summary>
        public string Code { get; init; } = string.Empty;

        /// <summary>Human readable message (for logs and developers; UIs should translate <see cref="Code"/>).</summary>
        public string Message { get; init; } = string.Empty;

        public Dictionary<string, object>? Details { get; init; }

        /// <summary>Whether calling again later may succeed; null when not applicable.</summary>
        public bool? Retryable { get; init; }

        /// <summary>Per-field validation errors; each entry carries its own code so the UI can translate it next to the field.</summary>
        public List<FieldError>? Fields { get; init; }

        public static ApiError Create(string code, string message, Dictionary<string, object>? details = null)
            => new() { Code = code, Message = message, Details = details };
    }
}
