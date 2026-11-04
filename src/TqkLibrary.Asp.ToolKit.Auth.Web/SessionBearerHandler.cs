using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Attaches the API session token of the session being VIEWED (read from the cookie through
    /// <see cref="WebSessionTicket"/>) as <c>Authorization: Bearer {sessionId}.{secret}</c> to every call of the API
    /// client it is added to. Exactly one send per call: there is no token refresh and no retry.
    /// <list type="bullet">
    ///   <item>Cookie without a session token → <see cref="SessionExpiredException"/> before anything is sent.</item>
    ///   <item>API answers 401 → <see cref="SessionExpiredException"/> (<see cref="SessionExpiredMiddleware"/> signs out
    ///   and redirects to login). The web session cookie is dropped before the response starts even when app code
    ///   catches the exception (<c>Response.OnStarting</c>).</item>
    ///   <item>Every other response (including 403 and 5xx) and every transport error is returned / propagated as is.</item>
    /// </list>
    /// <para>
    /// <c>IHttpClientFactory</c> reuses handlers for MANY users, so this one keeps no user state: everything is read
    /// from <see cref="IHttpContextAccessor"/> per call. Without a session (guest), inside
    /// <see cref="AnonymousApiScope"/>, when the caller set <c>Authorization</c> itself, or for a path listed in
    /// <see cref="WebAuthOptions.AnonymousPathSuffixes"/>, the request passes through untouched.
    /// </para>
    /// </summary>
    public sealed class SessionBearerHandler : DelegatingHandler
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IOptions<WebAuthOptions> _options;

        /// <summary>Creates the handler.</summary>
        /// <param name="httpContextAccessor">Source of the viewed request and its session.</param>
        /// <param name="options">Web session options.</param>
        public SessionBearerHandler(IHttpContextAccessor httpContextAccessor, IOptions<WebAuthOptions> options)
        {
            _httpContextAccessor = httpContextAccessor;
            _options = options;
        }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var options = _options.Value;
            var context = _httpContextAccessor.HttpContext;
            var sessionId = context?.User.FindFirst(options.SessionIdClaimType)?.Value;
            if (context is null || string.IsNullOrEmpty(sessionId) || request.Headers.Authorization is not null
                || AnonymousApiScope.IsActive || IsAnonymousPath(request, options))
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var ticket = WebSessionTicket.For(context);
            try
            {
                var token = await ticket.GetSessionTokenAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new SessionExpiredException(options.SessionExpiredMessage);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                    return response;
                // The API is the authority: the session is revoked, expired or unknown.
                response.Dispose();
                throw new SessionExpiredException(options.SessionExpiredMessage);
            }
            catch (SessionExpiredException)
            {
                // Drop the dead cookie even if the app catches the exception itself (see MarkSessionExpired).
                ticket.MarkSessionExpired();
                throw;
            }
        }

        private static bool IsAnonymousPath(HttpRequestMessage request, WebAuthOptions options)
        {
            if (options.AnonymousPathSuffixes.Count == 0)
                return false;
            var uri = request.RequestUri;
            var path = uri is null ? string.Empty : uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
            foreach (var suffix in options.AnonymousPathSuffixes)
            {
                if (!string.IsNullOrEmpty(suffix) && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
