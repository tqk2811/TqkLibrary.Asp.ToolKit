using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Catches <see cref="SessionExpiredException"/> thrown anywhere downstream (action, view component, filter):
    /// drops the web session, then redirects a normal navigation to the login page with <c>returnUrl</c>; an AJAX
    /// call gets 401 JSON <c>{ success: false, message, redirect }</c> so script can navigate itself (instead of fetch
    /// silently following a 302 and failing to parse JSON).
    /// </summary>
    public sealed class SessionExpiredMiddleware
    {
        private readonly RequestDelegate _next;

        public SessionExpiredMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context, IOptionsMonitor<CookieAuthenticationOptions> cookieOptions,
            IOptions<WebAuthOptions> options)
        {
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            // Once the response has started the cookie cannot be cleared (no more headers); the next request that hits
            // a 401 during refresh lands here and clears it.
            catch (SessionExpiredException ex) when (!context.Response.HasStarted)
            {
                var sessions = context.RequestServices.GetService(typeof(WebSessionManager)) as WebSessionManager;
                if (sessions is not null)
                    await sessions.DropAsync(context).ConfigureAwait(false);

                var cookie = cookieOptions.Get(options.Value.Scheme);
                var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                var redirect = context.Request.PathBase + cookie.LoginPath
                    + QueryString.Create(cookie.ReturnUrlParameter, returnUrl.ToString());

                context.Response.Clear();
                if (IsAjax(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    var body = JsonSerializer.Serialize(new { success = false, message = ex.Message, redirect = redirect.ToString() });
                    await context.Response.WriteAsync(body).ConfigureAwait(false);
                    return;
                }
                context.Response.Redirect(redirect.ToString());
            }
        }

        private static bool IsAjax(HttpRequest request)
            => string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
               || request.Headers.Accept.Any(a => a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
    }
}
