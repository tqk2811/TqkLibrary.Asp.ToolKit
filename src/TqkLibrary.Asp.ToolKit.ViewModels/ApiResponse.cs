namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    /// <summary>
    /// Envelope of every successful API response: <c>{ requestId, data, meta }</c>.
    /// Errors use <see cref="ApiErrorResponse"/> instead.
    /// </summary>
    public sealed class ApiResponse<T>
    {
        /// <summary>Id of the request that produced this response (same value as the <c>X-Request-Id</c> header).</summary>
        public string RequestId { get; init; } = string.Empty;

        /// <summary>Payload; null for endpoints without a result.</summary>
        public T? Data { get; init; }

        /// <summary>Paging information; null when the endpoint is not paged.</summary>
        public ApiResponseMetadata? Meta { get; init; }

        public static ApiResponse<T> Ok(T data, string requestId, ApiResponseMetadata? meta = null)
            => new() { RequestId = requestId, Data = data, Meta = meta };

        /// <summary>Paged response; <see cref="ApiResponseMetadata.TotalPages"/> is 0 when <paramref name="pageSize"/> is not positive.</summary>
        public static ApiResponse<T> Ok(T data, string requestId, int page, int pageSize, int total)
            => new()
            {
                RequestId = requestId,
                Data = data,
                Meta = ApiResponseMetadata.Create(page, pageSize, total),
            };
    }
}
