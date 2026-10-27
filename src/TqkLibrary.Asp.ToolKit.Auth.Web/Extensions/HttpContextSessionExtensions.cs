using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Extensions
{
    public static class HttpContextSessionExtensions
    {
        /// <summary>The API session id of the viewer, or null for a guest.</summary>
        public static string? GetApiSessionId(this HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var options = context.RequestServices.GetService<IOptions<WebAuthOptions>>()?.Value ?? new WebAuthOptions();
            return context.User.FindFirst(options.SessionIdClaimType)?.Value;
        }

        /// <summary>
        /// True when the viewer has an API session: their API calls carry a bearer and may return viewer-specific data,
        /// so caches shared by every viewer must NOT be read or written for them.
        /// </summary>
        public static bool HasApiSession(this HttpContext context) => !string.IsNullOrEmpty(context.GetApiSessionId());
    }
}
