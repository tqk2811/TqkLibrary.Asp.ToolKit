using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// The API tokens of the viewer's session for ONE request (kept in <see cref="HttpContext.Items"/>). Reads the
    /// cookie ticket once (<c>AuthenticateAsync</c>), refreshes when needed and rewrites the cookie.
    /// <para>
    /// The API refresh token is not rotated (reusable; a refresh only slides the session), so nothing has to be
    /// synchronised between requests or web instances: any request that sees an old access token refreshes on its
    /// own. The lock here only merges parallel API calls of the SAME request into one refresh and keeps
    /// <see cref="HttpContext"/> (not thread-safe) safe.
    /// </para>
    /// </summary>
    public sealed class WebSessionTicket
    {
        private static readonly object ItemKey = new();

        private readonly HttpContext _context;
        private readonly WebAuthOptions _options;
        private readonly TimeProvider _clock;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _loaded;
        private string? _sessionId;
        private WebSessionTokens? _tokens;
        private AuthenticationProperties? _properties;
        private volatile bool _dropPending;
        private int _dropHooked;

        private WebSessionTicket(HttpContext context)
        {
            _context = context;
            _options = context.RequestServices.GetRequiredService<IOptions<WebAuthOptions>>().Value;
            _clock = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        }

        /// <summary>The ticket of request <paramref name="context"/> (created on first use).</summary>
        public static WebSessionTicket For(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            // Items is not thread-safe, not even its getter (created lazily), so lock on the context itself.
            lock (context)
            {
                if (context.Items.TryGetValue(ItemKey, out var existing) && existing is WebSessionTicket ticket)
                    return ticket;
                ticket = new WebSessionTicket(context);
                context.Items[ItemKey] = ticket;
                return ticket;
            }
        }

        /// <summary>The tokens in the request cookie; null when the cookie has no (or incomplete) tokens.</summary>
        public async Task<WebSessionTokens?> GetTokensAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await LoadAsync().ConfigureAwait(false);
                return _tokens;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Refreshes the access token with the refresh token from the cookie, then rewrites the cookie (when the
        /// response has not started). If another call of the same request just refreshed, its result is reused.
        /// </summary>
        /// <param name="staleAccessToken">Access token the API just rejected; null for a proactive refresh (about to expire).</param>
        /// <param name="cancellationToken">Only used to wait for the lock; the refresh call has its own timeout.</param>
        /// <exception cref="SessionExpiredException">No tokens in the cookie, or the API rejected the session.</exception>
        public async Task<WebSessionTokens> RefreshAsync(string? staleAccessToken, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await LoadAsync().ConfigureAwait(false);
                var current = _tokens ?? throw new SessionExpiredException(_options.SessionExpiredMessage);

                var alreadyFresh = !current.IsAccessTokenExpiring(_clock.GetUtcNow().UtcDateTime, _options.RefreshSkew)
                    && (staleAccessToken is null || current.AccessToken != staleAccessToken);
                if (alreadyFresh)
                    return current;

                // NOT the request token: a request cancelled midway (for example the current-user timeout) should
                // still obtain and persist the new tokens; the own timeout prevents hanging forever.
                using var refreshCts = new CancellationTokenSource(_options.RefreshTimeout);
                AuthTokenPair refreshed;
                try
                {
                    var client = _context.RequestServices.GetRequiredService<IAuthApiClient>();
                    // Anonymous scope: a client wired through SessionBearerHandler must not recurse into it.
                    refreshed = await AnonymousApiScope
                        .RunAsync(() => client.RefreshAsync(current.RefreshToken, refreshCts.Token))
                        .ConfigureAwait(false);
                }
                catch (SessionExpiredException ex)
                {
                    // Session is dead on the API side. Transient failures (5xx, network) propagate untouched.
                    _context.RequestServices.GetService<ILogger<WebSessionTicket>>()?
                        .LogInformation(ex, "Token refresh rejected by the API; the user must sign in again.");
                    _tokens = null;
                    throw;
                }

                var next = WebSessionTokens.From(refreshed);
                _tokens = next;
                await PersistAsync(next).ConfigureAwait(false);
                return next;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Sets the tokens of a ticket just issued (sign-in) so later API calls in the same request use them.</summary>
        internal void Replace(string sessionId, WebSessionTokens tokens, AuthenticationProperties properties)
        {
            _dropPending = false;
            _sessionId = sessionId;
            _tokens = tokens;
            _properties = properties;
            _loaded = true;
        }

        /// <summary>Forgets the request tokens (session just dropped).</summary>
        internal void Clear()
        {
            _dropPending = false;
            _sessionId = null;
            _tokens = null;
            _properties = null;
            _loaded = true;
        }

        /// <summary>
        /// The session was reported dead (a <see cref="SessionExpiredException"/> is being thrown). Registers once a
        /// <c>Response.OnStarting</c> callback that drops the web session (<see cref="WebSessionManager.DropAsync"/>)
        /// when nobody did it before the headers are sent, so the dead cookie is deleted even when the exception is
        /// caught by app code instead of <see cref="SessionExpiredMiddleware"/>. Nothing happens once the response has
        /// started (headers can no longer be written).
        /// </summary>
        internal void MarkSessionExpired()
        {
            if (_context.Response.HasStarted)
                return;
            _dropPending = true;
            if (Interlocked.Exchange(ref _dropHooked, 1) == 1)
                return;
            _context.Response.OnStarting(async () =>
            {
                if (!_dropPending)
                    return;
                // Never throw from an OnStarting callback: it would abort writing the response headers.
                try
                {
                    var sessions = _context.RequestServices.GetService<WebSessionManager>();
                    if (sessions is not null)
                        await sessions.DropAsync(_context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<WebSessionTicket>()
                        .LogWarning(ex, "Could not drop the expired web session cookie.");
                }
            });
        }

        /// <summary>
        /// Ticket lifetime: never beyond the refresh token. The cookie slides with the session:
        /// min(now + cookie ExpireTimeSpan, refresh token expiry).
        /// </summary>
        internal static void ApplyLifetime(AuthenticationProperties properties, WebSessionTokens tokens, DateTimeOffset now,
            TimeSpan cookieExpireTimeSpan)
        {
            var cookieExpires = now.Add(cookieExpireTimeSpan);
            var refreshExpires = tokens.RefreshTokenExpiresAtOffset;
            properties.IssuedUtc = now;
            properties.ExpiresUtc = cookieExpires < refreshExpires ? cookieExpires : refreshExpires;
        }

        private async Task LoadAsync()
        {
            if (_loaded)
                return;
            var result = await _context.AuthenticateAsync(_options.Scheme).ConfigureAwait(false);
            _properties = result.Succeeded ? result.Properties : null;
            _sessionId = result.Succeeded ? result.Principal?.FindFirst(_options.SessionIdClaimType)?.Value : null;
            _tokens = _properties is null || string.IsNullOrEmpty(_sessionId) ? null : WebSessionTokens.Read(_properties, _options);
            _loaded = true;
        }

        /// <summary>
        /// Writes the new tokens into the cookie. Once the response has started no header can be written: skip it,
        /// the refresh token did not change so the next request refreshes again from the old cookie.
        /// </summary>
        private async Task PersistAsync(WebSessionTokens tokens)
        {
            if (_properties is null || string.IsNullOrEmpty(_sessionId) || _context.Response.HasStarted)
                return;

            tokens.WriteTo(_properties, _options);
            var cookieOptions = _context.RequestServices
                .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(_options.Scheme);
            ApplyLifetime(_properties, tokens, _clock.GetUtcNow(), cookieOptions.ExpireTimeSpan);

            var principal = _context.RequestServices.GetRequiredService<WebSessionPrincipalFactory>().CreateSessionOnly(_sessionId);
            await _context.SignInAsync(_options.Scheme, principal, _properties).ConfigureAwait(false);
        }
    }
}
