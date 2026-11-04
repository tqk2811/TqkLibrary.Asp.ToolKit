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
    ///   authenticated (session id and user id only, no name) but WITHOUT any permission: admin pages answer "forbidden" instead of
    ///   opening on stale data. <see cref="WebAuthOptions.CurrentUserFailureMode"/> = <c>Throw</c> fails the request
    ///   with <see cref="CurrentUserUnavailableException"/> instead (unless a DBSC proof failure already made it a guest);
    ///   <see cref="WebAuthOptions.CurrentUserFailureCacheFor"/> remembers the failure per session so an outage does not
    ///   make every request wait for the timeout.</item>
    ///   <item><see cref="WebAuthOptions.SkipCurrentUser"/> → no API call for that request (a cached user still applies).</item>
    ///   <item>DBSC (<see cref="DeviceBoundSessionOptions.Enabled"/>), outside <see cref="DeviceBoundSessionOptions.ExcludedPaths"/>:
    ///   a session is bound when <see cref="DeviceBindingCache"/> knows it or <c>me</c> (cached or fresh) reports it;
    ///   a bound session needs a valid proof cookie for the stored key. A known binding with the fixed policy is decided
    ///   BEFORE <c>me</c> (a rejected request does not call the API); otherwise after the user is known. When <c>me</c>
    ///   fails or is skipped, a known binding is still enforced (unknown user); an unknown binding counts as not bound.
    ///   The outcome comes from <see cref="DeviceProofFailureHandler"/>; <see cref="DeviceProofOutcome.Handled"/> ends
    ///   the pipeline here. A rejected request stays a guest for the rest of the request, re-executions included.</item>
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
            if (WebSessionTicket.IsRejected(context))
            {
                // A DBSC rejection is final for the request: a re-execution where UseAuthentication restored the cookie
                // principal still runs as a guest.
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
            else if (context.User.Identity?.IsAuthenticated == true && !string.IsNullOrEmpty(sessionId)
                && !context.Items.ContainsKey(ResolvedKey))
            {
                var device = options.Value.DeviceBound.Enabled ? context.RequestServices.GetRequiredService<DeviceProofEnforcer>() : null;
                var check = device is null ? DeviceProofCheck.NotApplicable : device.Check(context, sessionId);
                // Fixed DBSC policy and a session known to be bound: the decision needs no user, so it is taken before
                // `me` and a rejected request never calls the API. Otherwise it waits for the user (cached or loaded).
                if (check.Failure is not null && !device!.NeedsUser)
                {
                    var early = await device.EnforceAsync(context, sessions, sessionId, check, null).ConfigureAwait(false);
                    if (early == DeviceProofResult.Handled)
                        return;
                    if (early == DeviceProofResult.Guest)
                    {
                        context.Items[ResolvedKey] = true;
                        await _next(context).ConfigureAwait(false);
                        return;
                    }
                    check = check with { Failure = null };
                }

                DeviceProofResult result;
                if (cache.Get(sessionId) is { } cached)
                {
                    context.Items[ResolvedKey] = true;
                    result = await EnforceDeviceProofAsync(device, check, context, sessions, sessionId, cached).ConfigureAwait(false);
                    if (result == DeviceProofResult.Proceed)
                        context.User = principals.Create(sessionId, cached);
                }
                else if (options.Value.SkipCurrentUser?.Invoke(context) == true)
                {
                    // Not marked resolved: a re-execution on another path may still load the user.
                    result = await EnforceDeviceProofAsync(device, check, context, sessions, sessionId, null).ConfigureAwait(false);
                    if (result == DeviceProofResult.Proceed)
                        context.User = principals.CreateSessionOnly(sessionId, GetUserId(context));
                }
                else
                {
                    context.Items[ResolvedKey] = true;
                    result = await ResolveAsync(context, options.Value, client, cache, principals, sessions, sessionId, device, check).ConfigureAwait(false);
                }
                if (result == DeviceProofResult.Handled)
                    return;
            }

            await _next(context).ConfigureAwait(false);
        }

        private async Task<DeviceProofResult> ResolveAsync(HttpContext context, WebAuthOptions options, IAuthApiClient client, CurrentUserCache cache,
            WebSessionPrincipalFactory principals, WebSessionManager sessions, string sessionId, DeviceProofEnforcer? device,
            DeviceProofCheck check)
        {
            if (options.CurrentUserFailureCacheFor is not null && cache.HasFailure(sessionId))
                return await FailAsync(context, options, principals, sessions, sessionId, device, check, null).ConfigureAwait(false);

            CurrentUserInfo user;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
            {
                timeout.CancelAfter(options.CurrentUserTimeout);
                try
                {
                    user = await client.GetCurrentUserAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (SessionExpiredException)
                {
                    await sessions.DropAsync(context).ConfigureAwait(false);
                    return DeviceProofResult.Proceed;
                }
                catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Could not load the current user from the API.");
                    // Only outages are remembered: a bug-type exception would otherwise hide behind the cache.
                    if (options.CurrentUserFailureCacheFor is { } remember && IsTransientFailure(ex))
                        cache.SetFailure(sessionId, remember);
                    return await FailAsync(context, options, principals, sessions, sessionId, device, check, ex).ConfigureAwait(false);
                }
            }

            var result = await EnforceDeviceProofAsync(device, check, context, sessions, sessionId, user).ConfigureAwait(false);
            // A Handled request ends here: nothing of it is kept.
            if (result != DeviceProofResult.Handled)
                cache.Set(sessionId, user);
            if (result == DeviceProofResult.Proceed)
                context.User = principals.Create(sessionId, user);
            return result;
        }

        /// <summary>
        /// <c>me</c> unavailable: the DBSC proof of a session known to be bound is enforced FIRST (unknown user), so a
        /// rejected request is a guest before <see cref="CurrentUserFailureMode.Throw"/> could re-execute an error page
        /// with the cookie principal. Only a request that is not a guest then gets the configured failure handling.
        /// </summary>
        private static async Task<DeviceProofResult> FailAsync(HttpContext context, WebAuthOptions options, WebSessionPrincipalFactory principals,
            WebSessionManager sessions, string sessionId, DeviceProofEnforcer? device, DeviceProofCheck check, Exception? error)
        {
            var result = await EnforceDeviceProofAsync(device, check, context, sessions, sessionId, null).ConfigureAwait(false);
            if (result != DeviceProofResult.Proceed)
                return result;
            if (options.CurrentUserFailureMode == CurrentUserFailureMode.Throw)
                throw new CurrentUserUnavailableException("The current user could not be loaded from the API.", error);
            context.User = principals.CreateSessionOnly(sessionId, GetUserId(context));
            return result;
        }

        /// <summary>
        /// DBSC on an ordinary request (no-op when off): learns the binding from <paramref name="user"/> (null = unknown),
        /// then enforces a failed proof.
        /// </summary>
        private static async Task<DeviceProofResult> EnforceDeviceProofAsync(DeviceProofEnforcer? device, DeviceProofCheck check, HttpContext context,
            WebSessionManager sessions, string sessionId, CurrentUserInfo? user)
        {
            if (device is null)
                return DeviceProofResult.Proceed;
            check = await device.LearnAsync(context, sessionId, check, user).ConfigureAwait(false);
            return await device.EnforceAsync(context, sessions, sessionId, check, user).ConfigureAwait(false);
        }

        /// <summary>
        /// Outage-type failures: network errors without a status, 5xx and 429, timeouts (the caller already checked the
        /// request itself was not aborted).
        /// </summary>
        private static bool IsTransientFailure(Exception exception) => exception switch
        {
            HttpRequestException http => http.StatusCode is not { } status || (int)status >= 500 || status == System.Net.HttpStatusCode.TooManyRequests,
            OperationCanceledException => true,
            TimeoutException => true,
            _ => false,
        };

        /// <summary>User id stored in the cookie at sign-in.</summary>
        private static string? GetUserId(HttpContext context)
            => context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
