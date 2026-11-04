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
    /// silently following a 302 and failing to parse JSON). The AJAX test and body are customisable through
    /// <see cref="WebAuthOptions.IsAjaxRequest"/> and <see cref="WebAuthOptions.WriteSessionExpiredAjaxResponse"/>.
    /// <para>
    /// When <see cref="SessionBearerHandler"/> throws the exception and something else catches it (an MVC exception
    /// filter, a try/catch in the action), the cookie is still dropped: the handler registers a
    /// <c>Response.OnStarting</c> callback that drops the session unless it was dropped already.
    /// </para>
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
            // gets a 401 from the API again and lands here.
            catch (SessionExpiredException ex) when (!context.Response.HasStarted)
            {
                // Clear the partial response FIRST, then drop the session: the sign-out writes the Set-Cookie header
                // that deletes the cookie, and Clear() afterwards would erase that header (the browser would keep the
                // dead cookie and every following request would be rejected by the API again).
                context.Response.Clear();
                var sessions = context.RequestServices.GetService(typeof(WebSessionManager)) as WebSessionManager;
                if (sessions is not null)
                    await sessions.DropAsync(context).ConfigureAwait(false);

                var cookie = cookieOptions.Get(options.Value.Scheme);
                var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                var redirect = context.Request.PathBase + cookie.LoginPath
                    + QueryString.Create(cookie.ReturnUrlParameter, returnUrl.ToString());

                if ((options.Value.IsAjaxRequest ?? DefaultIsAjaxRequest)(context.Request))
                {
                    await (options.Value.WriteSessionExpiredAjaxResponse ?? DefaultWriteAjaxResponseAsync)(context, ex, redirect.ToString())
                        .ConfigureAwait(false);
                    return;
                }
                context.Response.Redirect(redirect.ToString());
            }
        }

        /// <summary>Default AJAX test: <c>X-Requested-With: XMLHttpRequest</c>, or an <c>Accept</c> header containing <c>application/json</c>.</summary>
        public static bool DefaultIsAjaxRequest(HttpRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
                   || request.Headers.Accept.Any(a => a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
        }

        /// <summary>Default AJAX response: 401 JSON <c>{ success: false, message, redirect }</c>.</summary>
        public static Task DefaultWriteAjaxResponseAsync(HttpContext context, SessionExpiredException exception, string redirect)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(exception);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            var body = JsonSerializer.Serialize(new { success = false, message = exception.Message, redirect });
            return context.Response.WriteAsync(body);
        }
    }
}
