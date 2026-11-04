using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// The API session token of the viewer's session for ONE request (kept in <see cref="HttpContext.Items"/>). Reads
    /// the cookie ticket once (<c>AuthenticateAsync</c>). The token is never rotated, so the cookie is never rewritten
    /// here; the only write is dropping it when the API reported the session dead.
    /// </summary>
    public sealed class WebSessionTicket
    {
        /// <summary>Name of the session token inside the ticket's authentication properties.</summary>
        internal const string SessionTokenName = "tqk_session_token";

        private static readonly object ItemKey = new();

        private readonly HttpContext _context;
        private readonly WebAuthOptions _options;
        private readonly SemaphoreSlim _gate = new(1, 1);
        // volatile: Replace/Clear write these outside _gate (they run on the request thread right after sign-in/drop,
        // while a parallel API call may be reading under the gate); the token is written before _loaded is published.
        private volatile bool _loaded;
        private volatile string? _sessionToken;
        private volatile bool _dropPending;
        private int _dropHooked;

        private WebSessionTicket(HttpContext context)
        {
            _context = context;
            _options = context.RequestServices.GetRequiredService<IOptions<WebAuthOptions>>().Value;
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

        /// <summary>The session token in the request cookie; null when the cookie has none.</summary>
        public async Task<string?> GetSessionTokenAsync(CancellationToken cancellationToken = default)
        {
            // Parallel API calls of one request: AuthenticateAsync touches the (not thread-safe) HttpContext once.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_loaded)
                {
                    var result = await _context.AuthenticateAsync(_options.Scheme).ConfigureAwait(false);
                    var sessionId = result.Succeeded ? result.Principal?.FindFirst(_options.SessionIdClaimType)?.Value : null;
                    _sessionToken = string.IsNullOrEmpty(sessionId) ? null : result.Properties?.GetTokenValue(SessionTokenName);
                    if (string.IsNullOrEmpty(_sessionToken))
                        _sessionToken = null;
                    _loaded = true;
                }
                return _sessionToken;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Sets the token of a ticket just issued (sign-in) so later API calls in the same request use it.</summary>
        internal void Replace(string sessionToken)
        {
            _dropPending = false;
            _sessionToken = sessionToken;
            _loaded = true;
        }

        /// <summary>Forgets the request token (session just dropped).</summary>
        internal void Clear()
        {
            _dropPending = false;
            _sessionToken = null;
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
    }
}
