using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// For every request with a session cookie: asks the API for the current user
    /// (<see cref="IAuthApiClient.GetCurrentUserAsync"/>) and REPLACES <see cref="HttpContext.User"/> with the current
    /// id, name, roles and permissions, so changes made elsewhere apply to the web UI (menus, buttons, MVC policies)
    /// after at most <see cref="WebAuthOptions.CurrentUserCacheLifetime"/>.
    /// <list type="bullet">
    ///   <item>Results are cached per session (<see cref="CurrentUserCache"/>); re-executed requests
    ///   (<c>UseStatusCodePagesWithReExecute</c>, <c>UseExceptionHandler</c>) reuse the first result.</item>
    ///   <item>Session dead (<see cref="SessionExpiredException"/>) → the web session is dropped and the request
    ///   continues as a guest (pages that need login redirect by themselves).</item>
    ///   <item>API unreachable (network, 5xx, timeout <see cref="WebAuthOptions.CurrentUserTimeout"/>) → still
    ///   authenticated (session id only, no name) but WITHOUT any permission: admin pages answer "forbidden" instead of
    ///   opening on stale data.</item>
    /// </list>
    /// Place it AFTER <c>UseAuthentication</c> and BEFORE <c>UseAuthorization</c>.
    /// </summary>
    public sealed class CurrentUserMiddleware
    {
        /// <summary>Marks the request as resolved (survives re-execution: same <see cref="HttpContext"/>).</summary>
        private static readonly object ResolvedKey = new();

        private readonly RequestDelegate _next;
        private readonly ILogger<CurrentUserMiddleware> _logger;

        public CurrentUserMiddleware(RequestDelegate next, ILogger<CurrentUserMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IOptions<WebAuthOptions> options, IAuthApiClient client,
            CurrentUserCache cache, WebSessionPrincipalFactory principals, WebSessionManager sessions)
        {
            var sessionId = context.User.FindFirst(options.Value.SessionIdClaimType)?.Value;
            if (context.User.Identity?.IsAuthenticated == true && !string.IsNullOrEmpty(sessionId)
                && !context.Items.ContainsKey(ResolvedKey))
            {
                context.Items[ResolvedKey] = true;
                if (cache.Get(sessionId) is { } cached)
                    context.User = principals.Create(sessionId, cached);
                else
                    await ResolveAsync(context, options.Value, client, cache, principals, sessions, sessionId).ConfigureAwait(false);
            }

            await _next(context).ConfigureAwait(false);
        }

        private async Task ResolveAsync(HttpContext context, WebAuthOptions options, IAuthApiClient client, CurrentUserCache cache,
            WebSessionPrincipalFactory principals, WebSessionManager sessions, string sessionId)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(options.CurrentUserTimeout);
            try
            {
                var user = await client.GetCurrentUserAsync(timeout.Token).ConfigureAwait(false);
                cache.Set(sessionId, user);
                context.User = principals.Create(sessionId, user);
            }
            catch (SessionExpiredException)
            {
                await sessions.DropAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not load the current user from the API; this request runs without permissions.");
                context.User = principals.CreateSessionOnly(sessionId);
            }
        }
    }
}
