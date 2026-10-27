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
    /// Opens and closes the web session (scoped). The encrypted cookie ticket holds:
    /// <list type="bullet">
    ///   <item>a principal with ONE claim, <see cref="WebAuthOptions.SessionIdClaimType"/> = API session id;</item>
    ///   <item>the API tokens (<see cref="WebSessionTokens"/>) in <see cref="AuthenticationProperties"/>.</item>
    /// </list>
    /// Id, name, email, roles and permissions are NOT in the cookie (they would go stale);
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
        /// Stores the tokens in the ticket and issues the cookie. <paramref name="persistent"/> = "remember me": the
        /// cookie survives closing the browser; otherwise it is a session cookie. The ticket expires at
        /// min(now + cookie ExpireTimeSpan, refresh token expiry).
        /// </summary>
        public async Task SignInAsync(HttpContext context, AuthTokenPair tokens, bool persistent)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(tokens);
            ArgumentException.ThrowIfNullOrEmpty(tokens.SessionId);

            var sessionTokens = WebSessionTokens.From(tokens);
            var properties = new AuthenticationProperties { IsPersistent = persistent };
            sessionTokens.WriteTo(properties, _options);
            WebSessionTicket.ApplyLifetime(properties, sessionTokens, _clock.GetUtcNow(),
                _cookieOptions.Get(_options.Scheme).ExpireTimeSpan);

            _currentUserCache.Remove(tokens.SessionId);
            await context.SignInAsync(_options.Scheme, _principals.CreateSessionOnly(tokens.SessionId), properties).ConfigureAwait(false);
            WebSessionTicket.For(context).Replace(tokens.SessionId, sessionTokens, properties);
        }

        /// <summary>Revokes the session on the API with the cookie tokens (best effort, never throws for API errors), then clears the cookie.</summary>
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
    }
}
