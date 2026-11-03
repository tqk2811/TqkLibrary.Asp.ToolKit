using TqkLibrary.Asp.ToolKit.ViewModels;
using TqkLibrary.Http;
using TqkLibrary.Http.Exceptions;

namespace TqkLibrary.Asp.ToolKit.Integration.Extensions
{
    /// <summary>
    /// Unwraps the <see cref="ApiResponse{T}"/> envelope of successful responses and turns error responses into
    /// <see cref="ApiException{T}"/> carrying an <see cref="ApiErrorResponse"/>.
    /// </summary>
    public static class RequestBuilderApiExtensions
    {
        /// <summary>
        /// Calls an endpoint returning <c>ApiResponse&lt;T&gt;</c> and returns its <c>Data</c>.
        /// <typeparamref name="T"/> must be a reference type; for value types use
        /// <see cref="ExecuteApiResponseAsync{T}"/> and read <c>Data</c>.
        /// </summary>
        /// <exception cref="ApiException{T}">The server returned an error status; <c>Body</c> is the <see cref="ApiErrorResponse"/>.</exception>
        /// <exception cref="ApiException">The server returned 2xx but the body or <c>data</c> is missing.</exception>
        public static async Task<T> ExecuteApiAsync<T>(
            this RequestBuilder requestBuilder,
            CancellationToken cancellationToken = default
            )
            where T : class
        {
            ApiResponse<T> response = await requestBuilder.ExecuteApiResponseAsync<T>(cancellationToken).ConfigureAwait(false);
            // Returning null here would push the error far from the endpoint that caused it.
            return response.Data
                ?? throw new ApiException($"Successful response but 'data' is missing for {typeof(T).Name}.");
        }

        /// <summary>Like <see cref="ExecuteApiAsync{T}"/> but keeps the envelope, to read <c>Meta</c> (paging) and <c>RequestId</c>.</summary>
        /// <exception cref="ApiException{T}">The server returned an error status; <c>Body</c> is the <see cref="ApiErrorResponse"/>.</exception>
        /// <exception cref="ApiException">The server returned 2xx with an empty body.</exception>
        public static async Task<ApiResponse<T>> ExecuteApiResponseAsync<T>(
            this RequestBuilder requestBuilder,
            CancellationToken cancellationToken = default
            )
        {
            ArgumentNullException.ThrowIfNull(requestBuilder);
            ApiResponse<T>? response = await requestBuilder.ExecuteAsync<ApiResponse<T>, ApiErrorResponse>(cancellationToken).ConfigureAwait(false);
            // An empty body deserializes to null instead of throwing.
            return response ?? throw new ApiException("Successful response but the body is empty, cannot read ApiResponse.");
        }

        /// <summary>Calls an endpoint without (or ignoring) result data. A 2xx response with an empty body does not throw.</summary>
        /// <exception cref="ApiException{T}">The server returned an error status; <c>Body</c> is the <see cref="ApiErrorResponse"/>.</exception>
        public static async Task ExecuteApiVoidAsync(
            this RequestBuilder requestBuilder,
            CancellationToken cancellationToken = default
            )
        {
            ArgumentNullException.ThrowIfNull(requestBuilder);
            await requestBuilder.ExecuteAsync<ApiResponse<object>, ApiErrorResponse>(cancellationToken).ConfigureAwait(false);
        }
    }
}
