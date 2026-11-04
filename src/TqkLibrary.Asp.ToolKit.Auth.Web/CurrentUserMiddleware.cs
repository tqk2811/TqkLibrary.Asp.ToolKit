using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    ///   with <see cref="CurrentUserUnavailableException"/> instead; <see cref="WebAuthOptions.CurrentUserFailureCacheFor"/>
    ///   remembers the failure per session so an outage does not make every request wait for the timeout.</item>
    ///   <item><see cref="WebAuthOptions.SkipCurrentUser"/> → no API call for that request (a cached user still applies).</item>
    ///   <item>DBSC (<see cref="DeviceBoundSessionOptions.Enabled"/>): for a cookie bound to a device key, the proof
    ///   cookie is checked here (outside <see cref="DeviceBoundSessionOptions.ExcludedPaths"/>). A failure applies
    ///   <see cref="DeviceProofFailureMode"/>: with a fixed policy it is decided BEFORE <c>me</c> (a rejected request
    ///   does not call the API); with <see cref="DeviceBoundSessionOptions.AllowUserChoice"/> after the user is known,
    ///   so the user's <see cref="CurrentUserInfo.DeviceBindingMode"/> can apply. A cookie without key for a session
    ///   the API reports bound (<see cref="CurrentUserInfo.IsDeviceBound"/>) runs as a guest and the user is not
    ///   cached.</item>
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
                var device = options.Value.DeviceBound.Enabled ? context.RequestServices.GetRequiredService<DeviceProofEnforcer>() : null;
                var check = device is null ? DeviceProofCheck.NotApplicable : await device.CheckAsync(context, sessionId).ConfigureAwait(false);
                // Fixed DBSC policy: the decision needs no user, so it is taken before `me` and a rejected request never
                // calls the API. With user choice it waits for the user (cached or loaded) below.
                if (check.Failure is { } reason && !device!.NeedsUser)
                {
                    var mode = await device.DecideAsync(context, sessionId, reason, null).ConfigureAwait(false);
                    if (await device.ApplyAsync(context, sessions, sessionId, reason, mode).ConfigureAwait(false))
                    {
                        context.Items[ResolvedKey] = true;
                        await _next(context).ConfigureAwait(false);
                        return;
                    }
                    check = check with { Failure = null };
                }

                if (cache.Get(sessionId) is { } cached)
                {
                    context.Items[ResolvedKey] = true;
                    if (!await EnforceDeviceProofAsync(device, check, context, sessions, sessionId, cached).ConfigureAwait(false))
                        context.User = principals.Create(sessionId, cached);
                }
                else if (options.Value.SkipCurrentUser?.Invoke(context) == true)
                {
                    // Not marked resolved: a re-execution on another path may still load the user.
                    if (!await EnforceDeviceProofAsync(device, check, context, sessions, sessionId, null).ConfigureAwait(false))
                        context.User = principals.CreateSessionOnly(sessionId, GetUserId(context));
                }
                else
                {
                    context.Items[ResolvedKey] = true;
                    await ResolveAsync(context, options.Value, client, cache, principals, sessions, sessionId, device, check).ConfigureAwait(false);
                }
            }

            await _next(context).ConfigureAwait(false);
        }

        private async Task ResolveAsync(HttpContext context, WebAuthOptions options, IAuthApiClient client, CurrentUserCache cache,
            WebSessionPrincipalFactory principals, WebSessionManager sessions, string sessionId, DeviceProofEnforcer? device,
            DeviceProofCheck check)
        {
            if (options.CurrentUserFailureCacheFor is not null && cache.HasFailure(sessionId))
            {
                ApplyFailure(context, options, principals, sessionId, null);
                await EnforceDeviceProofAsync(device, check, context, sessions, sessionId, null).ConfigureAwait(false);
                return;
            }

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
                    return;
                }
                catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Could not load the current user from the API.");
                    // Only outages are remembered: a bug-type exception would otherwise hide behind the cache.
                    if (options.CurrentUserFailureCacheFor is { } remember && IsTransientFailure(ex))
                        cache.SetFailure(sessionId, remember);
                    ApplyFailure(context, options, principals, sessionId, ex);
                    await EnforceDeviceProofAsync(device, check, context, sessions, sessionId, null).ConfigureAwait(false);
                    return;
                }
            }

            // A cookie copy without the device key of a bound session must not leave a full user behind in the cache.
            if (!IsDowngrade(check, user))
                cache.Set(sessionId, user);
            if (!await EnforceDeviceProofAsync(device, check, context, sessions, sessionId, user).ConfigureAwait(false))
                context.User = principals.Create(sessionId, user);
        }

        private static bool IsDowngrade(DeviceProofCheck check, CurrentUserInfo? user)
            => check.Applicable && !check.Bound && user?.IsDeviceBound == true;

        /// <summary>
        /// DBSC on an ordinary request (no-op when off/excluded): a downgrade (API says bound, ticket has no key) is a
        /// guest; a failed proof is decided with <paramref name="user"/> (null = unknown) and applied. True when the
        /// request now runs as a guest.
        /// </summary>
        private static async Task<bool> EnforceDeviceProofAsync(DeviceProofEnforcer? device, DeviceProofCheck check, HttpContext context,
            WebSessionManager sessions, string sessionId, CurrentUserInfo? user)
        {
            if (device is null || !check.Applicable)
                return false;
            if (IsDowngrade(check, user))
            {
                device.RejectUnbound(context, sessionId);
                return true;
            }
            if (check.Failure is not { } reason)
                return false;
            var mode = await device.DecideAsync(context, sessionId, reason, user).ConfigureAwait(false);
            return await device.ApplyAsync(context, sessions, sessionId, reason, mode).ConfigureAwait(false);
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

        private static void ApplyFailure(HttpContext context, WebAuthOptions options, WebSessionPrincipalFactory principals,
            string sessionId, Exception? error)
        {
            if (options.CurrentUserFailureMode == CurrentUserFailureMode.Throw)
                throw new CurrentUserUnavailableException("The current user could not be loaded from the API.", error);
            context.User = principals.CreateSessionOnly(sessionId, GetUserId(context));
        }

        /// <summary>User id stored in the cookie at sign-in.</summary>
        private static string? GetUserId(HttpContext context)
            => context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }
}
