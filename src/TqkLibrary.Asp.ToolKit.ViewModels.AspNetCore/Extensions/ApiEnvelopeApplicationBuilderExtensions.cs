using Microsoft.AspNetCore.Builder;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Middleware;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions
{
    public static class ApiEnvelopeApplicationBuilderExtensions
    {
        /// <summary>Adds <see cref="RequestIdMiddleware"/>. Call it first, so every later middleware and the logs see the id.</summary>
        public static IApplicationBuilder UseTqkRequestId(this IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);
            return app.UseMiddleware<RequestIdMiddleware>();
        }

        /// <summary>Adds <see cref="ApiExceptionWrapperMiddleware"/>. Call it after <see cref="UseTqkRequestId"/> and before routing/controllers.</summary>
        public static IApplicationBuilder UseTqkApiExceptionWrapper(this IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);
            return app.UseMiddleware<ApiExceptionWrapperMiddleware>();
        }
    }
}
