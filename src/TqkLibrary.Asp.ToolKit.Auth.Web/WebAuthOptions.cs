using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>Options of the web session (cookie that carries the API tokens). Every value has a usable default.</summary>
    public sealed class WebAuthOptions
    {
        /// <summary>Cookie authentication scheme name.</summary>
        public string Scheme { get; set; } = CookieAuthenticationDefaults.AuthenticationScheme;

        /// <summary>
        /// The ONLY claim stored in the cookie principal: the API session id. Also the key of
        /// <see cref="CurrentUserCache"/>. Tokens are not claims; they live in the ticket's authentication properties.
        /// </summary>
        public string SessionIdClaimType { get; set; } = "tqk_sid";

        /// <summary>
        /// Claim added with the API session id as value to EVERY web principal (cookie, session-only and full), so
        /// ASP.NET Core antiforgery (which binds its token to <c>sub</c>, then <c>NameIdentifier</c>, then <c>upn</c>) sees
        /// the same identity whether or not <c>me</c> could be loaded. Default <c>"sub"</c> because antiforgery checks it
        /// first; antiforgery tokens are therefore bound to the session (a new sign-in needs a new form token). The user id
        /// stays in <c>ClaimTypes.NameIdentifier</c>. Null or empty = not added (antiforgery then uses NameIdentifier on
        /// the full principal and every claim on the session-only one, which differ).
        /// </summary>
        public string? StableIdentityClaimType { get; set; } = "sub";

        /// <summary>Claim type used for permissions on the principal built from the API user.</summary>
        public string PermissionClaimType { get; set; } = "permission";

        /// <summary>Name of the access token inside the ticket properties.</summary>
        public string AccessTokenName { get; set; } = "api_access_token";

        /// <summary>Name of the access token expiry (UTC, round-trip format) inside the ticket properties.</summary>
        public string AccessTokenExpiresName { get; set; } = "api_access_expires";

        /// <summary>Name of the refresh token inside the ticket properties.</summary>
        public string RefreshTokenName { get; set; } = "api_refresh_token";

        /// <summary>Name of the refresh token expiry (UTC, round-trip format) inside the ticket properties.</summary>
        public string RefreshTokenExpiresName { get; set; } = "api_refresh_expires";

        /// <summary>Refresh the access token before sending when it expires within this window.</summary>
        public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The current access token must stay valid at least this long AFTER a failed proactive refresh for the
        /// request to fall back to it.
        /// </summary>
        public TimeSpan RefreshFallbackMargin { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Decides whether a PROACTIVE refresh failure is transient (the request then falls back to the current,
        /// still valid access token). Arguments: the exception and the caller's cancellation token. <c>null</c> =
        /// <see cref="SessionBearerHandler.DefaultIsTransientRefreshFailure"/>. Set it when <see cref="Interfaces.IAuthApiClient"/>
        /// wraps HTTP errors in its own exception type, e.g. to treat its 5xx as transient and its 4xx as fatal.
        /// </summary>
        public Func<Exception, CancellationToken, bool>? IsTransientRefreshFailure { get; set; }

        /// <summary>Own timeout of one refresh call (it does NOT use the request's cancellation token).</summary>
        public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Timeout of the current-user lookup. It must cover a token refresh plus a cold API start, otherwise requests
        /// randomly run without permissions.
        /// </summary>
        public TimeSpan CurrentUserTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>How long a current-user lookup is cached per session.</summary>
        public TimeSpan CurrentUserCacheLifetime { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// What <see cref="CurrentUserMiddleware"/> does when <c>me</c> fails for another reason than an expired session
        /// (network, 5xx, timeout): <see cref="Web.CurrentUserFailureMode.SessionOnly"/> (default) or
        /// <see cref="Web.CurrentUserFailureMode.Throw"/>.
        /// </summary>
        public CurrentUserFailureMode CurrentUserFailureMode { get; set; } = CurrentUserFailureMode.SessionOnly;

        /// <summary>
        /// When set, a failed <c>me</c> lookup is remembered per session for this long: following requests of that
        /// session apply <see cref="CurrentUserFailureMode"/> at once instead of waiting <see cref="CurrentUserTimeout"/>
        /// again during an API outage. Null (default) = every request asks again.
        /// </summary>
        public TimeSpan? CurrentUserFailureCacheFor { get; set; }

        /// <summary>
        /// Requests for which <see cref="CurrentUserMiddleware"/> does not call <c>me</c> (static files, health checks,
        /// endpoints that need no user data). A cached user is still applied; otherwise the request keeps the
        /// session-only principal. Null (default) = never skip.
        /// </summary>
        public Func<HttpContext, bool>? SkipCurrentUser { get; set; }

        /// <summary>
        /// Decides whether <see cref="SessionExpiredMiddleware"/> answers a request as AJAX (401 body) instead of
        /// redirecting. Null = <see cref="SessionExpiredMiddleware.DefaultIsAjaxRequest"/>.
        /// </summary>
        public Func<HttpRequest, bool>? IsAjaxRequest { get; set; }

        /// <summary>
        /// Writes the AJAX response of <see cref="SessionExpiredMiddleware"/> (the session is already dropped and the
        /// response cleared; arguments: context, exception, login redirect URL with <c>returnUrl</c>). Null =
        /// <see cref="SessionExpiredMiddleware.DefaultWriteAjaxResponseAsync"/> (401 JSON <c>{ success, message, redirect }</c>).
        /// </summary>
        public Func<HttpContext, SessionExpiredException, string, Task>? WriteSessionExpiredAjaxResponse { get; set; }

        /// <summary>
        /// Path suffixes (case-insensitive) of anonymous API endpoints (login, refresh, password reset...): the bearer
        /// handler never attaches a token to them and never treats their 401 as an expired session.
        /// </summary>
        public IList<string> AnonymousPathSuffixes { get; set; } = new List<string>();

        /// <summary>Message returned to AJAX callers / carried by <see cref="SessionExpiredException"/>.</summary>
        public string SessionExpiredMessage { get; set; } = "Your session has expired. Please sign in again.";
    }
}
