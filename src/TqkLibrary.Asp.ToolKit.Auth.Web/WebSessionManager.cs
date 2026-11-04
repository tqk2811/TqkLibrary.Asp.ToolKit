using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
        /// <para>With <see cref="DeviceBoundSessionOptions.Enabled"/> and an HTTPS request, the response also carries
        /// <c>Secure-Session-Registration</c>, so a DBSC browser binds the new session to a device key right away (the API
        /// accepts a key only shortly after the session was created).</para>
        /// </summary>
        /// <exception cref="ArgumentException">The token is not a valid <c>{sessionId}.{secret}</c> (same rules as the API
        /// <c>SessionTokenCodec</c>), the user id is empty, or <see cref="AuthSession.ExpiresAt"/> is not in the future.</exception>
        public async Task SignInAsync(HttpContext context, AuthSession session, bool persistent)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(session.UserId);
            var sessionId = GetSessionId(session.SessionToken)
                ?? throw new ArgumentException("The session token must have the form {sessionId}.{secret}.", nameof(session));

            var now = _clock.GetUtcNow();
            if (session.ExpiresAt <= now)
                throw new ArgumentException("The session is already expired (ExpiresAt must be in the future).", nameof(session));
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
            if (_options.DeviceBound.Enabled)
                OfferDeviceBinding(context, sessionId);
        }

        /// <summary>
        /// DBSC: asks the browser to bind the new session (<c>Secure-Session-Registration</c>, HTTPS only) and deletes a
        /// proof cookie left by a previous session.
        /// </summary>
        private void OfferDeviceBinding(HttpContext context, string sessionId)
        {
            var proofCookie = _services.GetRequiredService<DeviceProofCookie>();
            if (context.Request.Cookies.ContainsKey(proofCookie.Name))
                proofCookie.Delete(context);
            if (!context.Request.IsHttps)
                return;
            var challenge = _services.GetRequiredService<DeviceChallengeService>().Issue(DeviceChallengeKind.Registration, sessionId);
            var options = _options.DeviceBound;
            var path = context.Request.PathBase.Add(options.RegistrationPath).ToUriComponent();
            context.Response.Headers[DeviceBoundSessionMiddleware.RegistrationHeader] =
                "(" + string.Join(' ', options.Algorithms) + ");path=" + DeviceBoundSessionMiddleware.SfString(path)
                + ";challenge=" + DeviceBoundSessionMiddleware.SfString(challenge);
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

        /// <summary>
        /// Drops the web session after the API reported it dead (does not call the API). With DBSC enabled the proof
        /// cookie is deleted too (and <c>Clear-Site-Data: "cookies"</c> sent when
        /// <see cref="DeviceBoundSessionOptions.ClearSiteDataOnSignOut"/>).
        /// </summary>
        public async Task DropAsync(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            _currentUserCache.Remove(GetSessionId(context.User));
            WebSessionTicket.For(context).Clear();
            await context.SignOutAsync(_options.Scheme).ConfigureAwait(false);
            if (_options.DeviceBound.Enabled && !context.Response.HasStarted)
            {
                _services.GetRequiredService<DeviceProofCookie>().Delete(context);
                if (_options.DeviceBound.ClearSiteDataOnSignOut)
                    context.Response.Headers["Clear-Site-Data"] = "\"cookies\"";
            }
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        private string? GetSessionId(ClaimsPrincipal user) => user.FindFirst(_options.SessionIdClaimType)?.Value;

        // Same limits as the API SessionTokenCodec (this package does not reference it): keep them in sync, a token
        // this check accepts must be one the API SessionTokenCodec.TryParse accepts.
        private const int MaxTokenLength = 1024;
        private const int MaxSessionIdLength = 128;
        private const int SecretLength = 43;

        /// <summary>
        /// The session id of a well-formed token (id of 1..128 chars <c>A-Z a-z 0-9 - _</c>, a '.', then a secret of
        /// exactly 43 base64url chars; see the API <c>SessionTokenCodec</c>), otherwise null.
        /// </summary>
        internal static string? GetSessionId(string? sessionToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken) || sessionToken.Length > MaxTokenLength)
                return null;
            var dot = sessionToken.IndexOf('.');
            if (dot <= 0 || dot > MaxSessionIdLength || sessionToken.Length - dot - 1 != SecretLength)
                return null;
            for (var i = 0; i < sessionToken.Length; i++)
            {
                if (i != dot && !IsBase64UrlChar(sessionToken[i]))
                    return null;
            }
            return sessionToken[..dot];
        }

        private static bool IsBase64UrlChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
    }
}
