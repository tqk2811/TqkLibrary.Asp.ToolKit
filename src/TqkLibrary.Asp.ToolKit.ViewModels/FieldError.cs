namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    /// <summary>Validation error of one request field.</summary>
    public sealed class FieldError
    {
        /// <summary>Request property path as the client sent it (camelCase, e.g. <c>displayName</c> or <c>items[0].name</c>); empty for the whole body.</summary>
        public string Field { get; init; } = string.Empty;

        /// <summary>Error code, e.g. <see cref="ApiErrorCodes.FieldRequired"/>.</summary>
        public string Code { get; init; } = string.Empty;

        /// <summary>Parameters of the code (e.g. <c>max</c> for <see cref="ApiErrorCodes.FieldTooLong"/>).</summary>
        public Dictionary<string, object>? Params { get; init; }

        /// <summary>Server formatted message, when one is available. UIs should prefer translating <see cref="Code"/>.</summary>
        public string? Message { get; init; }
    }
}
