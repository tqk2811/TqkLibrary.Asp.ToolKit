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
    }
}
