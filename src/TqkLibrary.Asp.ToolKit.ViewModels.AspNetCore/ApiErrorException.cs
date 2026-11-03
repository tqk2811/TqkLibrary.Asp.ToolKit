using System.Net;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore
{
    /// <summary>
    /// Business error carrying an HTTP status. Actions throw it instead of returning
    /// <c>NotFound(...)</c>/<c>Forbid()</c>; <see cref="Middleware.ApiExceptionWrapperMiddleware"/> turns it into an
    /// <see cref="ApiErrorResponse"/> with that status and code.
    /// </summary>
    public class ApiErrorException : Exception
    {
        public ApiErrorException(
            HttpStatusCode statusCode,
            string code,
            string message,
            Dictionary<string, object>? details = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = (int)statusCode;
            Code = code;
            Details = details;
        }

        /// <summary>HTTP status returned to the client.</summary>
        public int StatusCode { get; }

        /// <summary>Business error code, goes to <see cref="ApiError.Code"/>.</summary>
        public string Code { get; }

        /// <summary>Extra information, goes to <see cref="ApiError.Details"/>.</summary>
        public Dictionary<string, object>? Details { get; }

        public ApiError ToApiError() => ApiError.Create(Code, Message, Details);

        public static ApiErrorException BadRequest(string code, string message, Dictionary<string, object>? details = null)
            => new(HttpStatusCode.BadRequest, code, message, details);

        public static ApiErrorException Unauthorized(
            string code = ApiErrorCodes.Unauthorized,
            string message = "Authentication is required.",
            Dictionary<string, object>? details = null)
            => new(HttpStatusCode.Unauthorized, code, message, details);

        /// <summary>Replaces <c>return Forbid()</c>, but with a JSON body telling the client why.</summary>
        public static ApiErrorException Forbidden(
            string code = ApiErrorCodes.Forbidden,
            string message = "You do not have permission to perform this action.",
            Dictionary<string, object>? details = null)
            => new(HttpStatusCode.Forbidden, code, message, details);

        public static ApiErrorException NotFound(
            string code = ApiErrorCodes.NotFound,
            string message = "The resource was not found.",
            Dictionary<string, object>? details = null)
            => new(HttpStatusCode.NotFound, code, message, details);

        public static ApiErrorException Conflict(
            string code = ApiErrorCodes.Conflict,
            string message = "The request conflicts with the current state.",
            Dictionary<string, object>? details = null)
            => new(HttpStatusCode.Conflict, code, message, details);

        /// <summary>Called too often; retrying after a while may succeed.</summary>
        public static ApiErrorException TooManyRequests(string code, string message, Dictionary<string, object>? details = null)
            => new(HttpStatusCode.TooManyRequests, code, message, details);
    }
}
