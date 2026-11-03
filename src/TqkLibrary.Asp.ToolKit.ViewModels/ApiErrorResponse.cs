namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    /// <summary>Envelope of every error response: <c>{ requestId, error }</c>.</summary>
    public sealed class ApiErrorResponse
    {
        public string RequestId { get; init; } = string.Empty;
        public ApiError Error { get; init; } = null!;

        public static ApiErrorResponse Create(string requestId, ApiError error)
            => new() { RequestId = requestId, Error = error };
    }
}
