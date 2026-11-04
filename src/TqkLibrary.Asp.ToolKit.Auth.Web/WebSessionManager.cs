using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Opens and closes the web session (scoped). The encrypted cookie ticket holds only immutable data:
    /// <list type="bullet">
    ///   <item>a principal with the API session id (<see cref="WebAuthOptions.SessionIdClaimType"/>) and the user id
    ///   (<c>ClaimTypes.NameIdentifier</c>);</item>
    ///   <item>the API session token <c>{sessionId}.{secret}</c> in <see cref="AuthenticationProperties"/>.</item>
    /// </list>
    /// Name, email, roles and permissions are NOT in the cookie (they would go stale);
    /// <see cref="CurrentUserMiddleware"/> asks the API (short <see cref="CurrentUserCache"/>). Nothing is stored
    /// server side, so only the Data Protection key ring must be persisted (and shared by every instance).
    /// </summary>
    public sealed class WebSessionManager
    {
        private readonly IServiceProvider _services;
        private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookieOptions;
        private readonly WebAuthOptions _options;
        private readonly WebSessionPrincipalFactory _principals;
        private readonly CurrentUserCache _currentUserCache;
        private readonly TimeProvider _clock;
        private readonly ILogger<WebSessionManager> _logger;

        public WebSessionManager(
            IServiceProvider services,
            IOptionsMonitor<CookieAuthenticationOptions> cookieOptions,
            IOptions<WebAuthOptions> options,
            WebSessionPrincipalFactory principals,
            CurrentUserCache currentUserCache,
            TimeProvider clock,
            ILogger<WebSessionManager> logger)
        {
            _services = services;
            _cookieOptions = cookieOptions;
            _options = options.Value;
            _principals = principals;
            _currentUserCache = currentUserCache;
            _clock = clock;
            _logger = logger;
        }

        /// <summary>
        /// Stores the session token in the ticket and issues the cookie. <paramref name="persistent"/> = "remember me":
        /// the cookie survives closing the browser; otherwise it is a session cookie. The ticket expires at
        /// <see cref="AuthSession.ExpiresAt"/> and is never reissued; the API decides on every call whether the
        /// session is still alive. <c>context.User</c> is set to the new session principal, so API calls made later in
        /// the same request already carry the bearer token.
        /// </summary>
        /// <exception cref="ArgumentException">The token is not <c>{sessionId}.{secret}</c> or the user id is empty.</exception>
        public async Task SignInAsync(HttpContext context, AuthSession session, bool persistent)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(session.UserId);
            var sessionId = GetSessionId(session.SessionToken)
                ?? throw new ArgumentException("The session token must have the form {sessionId}.{secret}.", nameof(session));

            var now = _clock.GetUtcNow();
            var properties = new AuthenticationProperties
            {
                IsPersistent = persistent,
                IssuedUtc = now,
                ExpiresUtc = session.ExpiresAt,
            };
            properties.StoreTokens([new AuthenticationToken { Name = WebSessionTicket.SessionTokenName, Value = session.SessionToken }]);

            _currentUserCache.Remove(sessionId);
            var principal = _principals.CreateSessionOnly(sessionId, session.UserId);
            await context.SignInAsync(_options.Scheme, principal, properties).ConfigureAwait(false);
            // SessionBearerHandler finds the session through context.User, which SignInAsync does not touch.
            context.User = principal;
            WebSessionTicket.For(context).Replace(session.SessionToken);
        }

        /// <summary>Revokes the session on the API with the cookie token (best effort, never throws for API errors), then clears the cookie.</summary>
        public async Task SignOutAsync(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var sessionId = GetSessionId(context.User);
            if (!string.IsNullOrEmpty(sessionId))
            {
                try
                {
                    var client = (IAuthApiClient?)_services.GetService(typeof(IAuthApiClient));
                    if (client is not null)
                        await client.LogoutAsync(context.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
                {
                    // Session already dead on the API, or API unreachable: the web side must sign out anyway.
                    _logger.LogInformation(ex, "Could not revoke the API session while signing out.");
                }
            }
            await DropAsync(context).ConfigureAwait(false);
        }

        /// <summary>Drops the web session after the API reported it dead (does not call the API).</summary>
        public async Task DropAsync(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            _currentUserCache.Remove(GetSessionId(context.User));
            WebSessionTicket.For(context).Clear();
            await context.SignOutAsync(_options.Scheme).ConfigureAwait(false);
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        private string? GetSessionId(ClaimsPrincipal user) => user.FindFirst(_options.SessionIdClaimType)?.Value;

        /// <summary>The part before the first '.' of a well-formed <c>{sessionId}.{secret}</c> token, otherwise null.</summary>
        private static string? GetSessionId(string? sessionToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken))
                return null;
            var dot = sessionToken.IndexOf('.');
            return dot <= 0 || dot == sessionToken.Length - 1 ? null : sessionToken[..dot];
        }
    }
}
