using Microsoft.AspNetCore.Http;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions
{
    public static class HttpContextRequestIdExtensions
    {
        /// <summary><see cref="HttpContext.Items"/> key where <see cref="Middleware.RequestIdMiddleware"/> stores the request id.</summary>
        public const string RequestIdItemKey = "RequestId";

        /// <summary>Request id set by <c>UseTqkRequestId</c>; falls back to <see cref="HttpContext.TraceIdentifier"/>.</summary>
        public static string GetRequestId(this HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            if (httpContext.Items.TryGetValue(RequestIdItemKey, out object? value) && value is string { Length: > 0 } requestId)
                return requestId;
            return httpContext.TraceIdentifier;
        }
    }
}
