using Microsoft.AspNetCore.Http;
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
    ///   <item>API answers 401 → refresh EXACTLY once and resend. Upload bodies (multipart/stream) are not buffered,
    ///   so they are not resent: a 401 for them means the session expired.</item>
    ///   <item>Still 401 after a refresh, or the refresh rejected → <see cref="SessionExpiredException"/>
    ///   (<see cref="SessionExpiredMiddleware"/> signs out and redirects to login).</item>
    ///   <item>Transient refresh failures propagate as they are; the cookie is kept.</item>
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

        public SessionBearerHandler(IHttpContextAccessor httpContextAccessor, TimeProvider clock, IOptions<WebAuthOptions> options)
        {
            _httpContextAccessor = httpContextAccessor;
            _clock = clock;
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
            var tokens = await ticket.GetTokensAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new SessionExpiredException(options.SessionExpiredMessage);
            if (tokens.IsAccessTokenExpiring(_clock.GetUtcNow().UtcDateTime, options.RefreshSkew))
                tokens = await ticket.RefreshAsync(null, cancellationToken).ConfigureAwait(false);

            // Upload bodies may be huge and StreamContent can be read once: not buffered, hence not resendable.
            var replayable = IsReplayable(request.Content);
            if (replayable && request.Content is not null)
                await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);

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

        /// <summary>Bufferable bodies (JSON, form, string...) can be resent; multipart and stream uploads cannot.</summary>
        private static bool IsReplayable(HttpContent? content)
            => content is not (MultipartContent or StreamContent);

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
