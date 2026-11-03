using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Attaches the access token of the session being VIEWED (read from the cookie through
    /// <see cref="WebSessionTicket"/>) to every call of the API client it is added to.
    /// <list type="bullet">
    ///   <item>Token about to expire (<see cref="WebAuthOptions.RefreshSkew"/>) → refresh before sending.</item>
    ///   <item>API answers 401 → refresh EXACTLY once and resend. Upload bodies (multipart/stream) are not buffered by
    ///   default, so they are not resent: a 401 for them means the session expired. Opt in per request with <see cref="ReplayBufferLimitOption"/>.</item>
    ///   <item>Still 401 after a refresh, or the refresh rejected → <see cref="SessionExpiredException"/>
    ///   (<see cref="SessionExpiredMiddleware"/> signs out and redirects to login). The web session cookie is dropped
    ///   before the response starts even when app code catches the exception (<c>Response.OnStarting</c>).</item>
    ///   <item>Transient refresh failures propagate as they are; the cookie is kept. The exception is the
    ///   PROACTIVE refresh (token only about to expire): if it fails transiently
    ///   (<see cref="WebAuthOptions.IsTransientRefreshFailure"/>) while the current access token is still valid for at
    ///   least <see cref="WebAuthOptions.RefreshFallbackMargin"/>, a warning is logged and the request is sent with the
    ///   current token.</item>
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
        private readonly TimeProvider _clock;
        private readonly IOptions<WebAuthOptions> _options;
        private readonly ILogger? _logger;

        /// <summary>Creates the handler.</summary>
        /// <param name="httpContextAccessor">Source of the viewed request and its session.</param>
        /// <param name="clock">Clock used to decide whether the access token is still valid.</param>
        /// <param name="options">Web session options.</param>
        /// <param name="logger">Optional; logs the fallback to the current token after a failed proactive refresh.</param>
        public SessionBearerHandler(IHttpContextAccessor httpContextAccessor, TimeProvider clock, IOptions<WebAuthOptions> options,
            ILogger<SessionBearerHandler>? logger = null)
        {
            _httpContextAccessor = httpContextAccessor;
            _clock = clock;
            _options = options;
            _logger = logger;
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
                return await SendWithSessionAsync(request, ticket, options, cancellationToken).ConfigureAwait(false);
            }
            catch (SessionExpiredException)
            {
                // Drop the dead cookie even if the app catches the exception itself (see MarkSessionExpired).
                ticket.MarkSessionExpired();
                throw;
            }
        }

        private async Task<HttpResponseMessage> SendWithSessionAsync(HttpRequestMessage request, WebSessionTicket ticket,
            WebAuthOptions options, CancellationToken cancellationToken)
        {
            var tokens = await ticket.GetTokensAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new SessionExpiredException(options.SessionExpiredMessage);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (tokens.IsAccessTokenExpiring(now, options.RefreshSkew))
            {
                try
                {
                    tokens = await ticket.RefreshAsync(null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not SessionExpiredException
                    && (options.IsTransientRefreshFailure ?? DefaultIsTransientRefreshFailure)(ex, cancellationToken)
                    && tokens.AccessTokenExpiresAt > _clock.GetUtcNow().UtcDateTime + options.RefreshFallbackMargin)
                {
                    // Proactive refresh only: the current token still works (checked AFTER the refresh attempt, with a
                    // margin), so a TRANSIENT refresh failure must not fail the call. Caller cancellation, 4xx (config or
                    // contract bugs) and unknown exceptions propagate. If the API rejects the old token, the 401 path
                    // below refreshes (and surfaces the failure) as usual.
                    _logger?.LogWarning(ex, "Proactive token refresh failed; sending the request with the current access token.");
                }
            }

            // Upload bodies may be huge and StreamContent can be read once: buffered (hence resendable) only when the
            // app opted in and the length is known and within the limit.
            var replayable = await PrepareReplayAsync(request, options, cancellationToken).ConfigureAwait(false);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return response;

            response.Dispose();
            if (!replayable)
                throw new SessionExpiredException(options.SessionExpiredMessage);

            var refreshed = await ticket.RefreshAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return response;

            // A freshly refreshed token is still rejected: the session is dead on the API side.
            response.Dispose();
            throw new SessionExpiredException(options.SessionExpiredMessage);
        }

        /// <summary>
        /// Default transient test for a failed proactive refresh: network errors without a status or with a
        /// non-4xx status, and timeouts (cancellation not requested by the caller). Everything else is not transient.
        /// </summary>
        public static bool DefaultIsTransientRefreshFailure(Exception exception, CancellationToken callerToken) => exception switch
        {
            HttpRequestException http => http.StatusCode is not { } status || (int)status is < 400 or >= 500,
            OperationCanceledException => !callerToken.IsCancellationRequested,
            TimeoutException => true,
            _ => false,
        };

        /// <summary>
        /// Per-request opt-in to buffering an upload (<c>StreamContent</c>, <c>MultipartContent</c>) so it can be resent
        /// after a 401 + refresh (bytes, at most <see cref="int.MaxValue"/>; default / 0 = never buffer, a 401 on the
        /// upload then means <see cref="SessionExpiredException"/>). The body length must be known up front and within
        /// the limit; a body that turns out longer than declared makes the send fail with <see cref="HttpRequestException"/>
        /// before anything is sent:
        /// <c>request.Options.Set(SessionBearerHandler.ReplayBufferLimitOption, 10_000_000)</c>.
        /// </summary>
        public static readonly HttpRequestOptionsKey<long> ReplayBufferLimitOption = new("TqkLibrary.Asp.ToolKit.Auth.Web.ReplayBufferLimit");

        /// <summary>
        /// Buffers the body when it can be resent after a 401. Bodies other than multipart/stream (JSON, form, string...)
        /// always; multipart and stream uploads only when the per-request limit is positive and their
        /// length is known and within it. Never reads a body it cannot keep whole, so a refused upload is sent intact once.
        /// </summary>
        private static async Task<bool> PrepareReplayAsync(HttpRequestMessage request, WebAuthOptions options, CancellationToken cancellationToken)
        {
            var content = request.Content;
            if (content is null)
                return true;
            if (content is MultipartContent or StreamContent)
            {
                var limit = request.Options.TryGetValue(ReplayBufferLimitOption, out var perRequest) ? perRequest : 0;
                if (limit <= 0 || content.Headers.ContentLength is not { } length || length > limit)
                    return false;
                // The ContentLength header can lie (a stream longer than declared): enforce the limit while reading.
                // A failure here (HttpRequestException) leaves the stream half-read, so it propagates instead of
                // sending a truncated body.
                await content.LoadIntoBufferAsync(Math.Min(limit, int.MaxValue), cancellationToken).ConfigureAwait(false);
                return true;
            }
            await content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            return true;
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
