using Microsoft.AspNetCore.Builder;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Extensions
{
    public static class WebAuthApplicationBuilderExtensions
    {
        /// <summary>
        /// <see cref="SessionExpiredMiddleware"/> then <see cref="CurrentUserMiddleware"/>; place right after
        /// <c>UseAuthentication()</c> and before <c>UseAuthorization()</c>.
        /// </summary>
        public static IApplicationBuilder UseTqkWebSession(this IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);
            return app.UseMiddleware<SessionExpiredMiddleware>().UseMiddleware<CurrentUserMiddleware>();
        }
    }
}
