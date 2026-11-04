using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Extensions
{
    public static class WebAuthApplicationBuilderExtensions
    {
        /// <summary>
        /// <see cref="SessionExpiredMiddleware"/>, then <see cref="DeviceBoundSessionMiddleware"/> (only when
        /// <see cref="DeviceBoundSessionOptions.Enabled"/>), then <see cref="CurrentUserMiddleware"/>; place right after
        /// <c>UseAuthentication()</c> and before <c>UseAuthorization()</c>.
        /// </summary>
        public static IApplicationBuilder UseTqkWebSession(this IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);
            app.UseMiddleware<SessionExpiredMiddleware>();
            if (app.ApplicationServices.GetService<IOptions<WebAuthOptions>>()?.Value.DeviceBound.Enabled == true)
                app.UseMiddleware<DeviceBoundSessionMiddleware>();
            return app.UseMiddleware<CurrentUserMiddleware>();
        }
    }
}
