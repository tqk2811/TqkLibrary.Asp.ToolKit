using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Enforces the DBSC proof on ordinary requests (singleton), called by <see cref="CurrentUserMiddleware"/>. Whether a
    /// session is bound, and to which key, comes from the API (<c>me</c>) through <see cref="DeviceBindingCache"/>:
    /// <see cref="Check"/> uses what the cache knows; <see cref="LearnAsync"/> completes it from a <c>me</c> result;
    /// <see cref="EnforceAsync"/> asks <see cref="DeviceProofFailureHandler"/> and applies the outcome.
    /// </summary>
    internal sealed class DeviceProofEnforcer
    {
        private readonly IOptions<WebAuthOptions> _options;
        private readonly DeviceProofCookie _proofCookie;
        private readonly DeviceBindingCache _bindings;
        private readonly DeviceBoundMetrics _metrics;
        private readonly DeviceProofFailureHandler _handler;
        private readonly ILogger<DeviceProofEnforcer> _logger;

        public DeviceProofEnforcer(IOptions<WebAuthOptions> options, DeviceProofCookie proofCookie, DeviceBindingCache bindings,
            DeviceBoundMetrics metrics, DeviceProofFailureHandler handler, ILogger<DeviceProofEnforcer> logger)
        {
            _options = options;
            _proofCookie = proofCookie;
            _bindings = bindings;
            _metrics = metrics;
            _handler = handler;
            _logger = logger;
        }

        /// <summary>Whether the decision depends on the user (<see cref="DeviceBoundSessionOptions.AllowUserChoice"/>).</summary>
        public bool NeedsUser => _options.Value.DeviceBound.AllowUserChoice;

        /// <summary>
        /// DBSC state of the request from what the binding cache knows: not applicable (off / excluded path), known not
        /// bound (a fresh <c>me</c> said so within <see cref="DeviceBoundSessionOptions.UnboundRecheckInterval"/>), unknown
        /// (<see cref="DeviceProofCheck.BindingUnknown"/>: <see cref="LearnAsync"/> completes it from a fresh <c>me</c>), or
        /// bound with the result of the proof cookie check against the stored key.
        /// </summary>
        public DeviceProofCheck Check(HttpContext context, string sessionId)
        {
            var options = _options.Value.DeviceBound;
            if (!options.Enabled || IsExcluded(context.Request.Path, options))
                return DeviceProofCheck.NotApplicable;
            if (_bindings.Get(sessionId) is not { } key)
                return new DeviceProofCheck(true, false, null, _bindings.IsKnownUnbound(sessionId));
            return new DeviceProofCheck(true, true, _proofCookie.CheckRequest(context, sessionId, key.Thumbprint));
        }

        /// <summary>
        /// Records the binding reported by <paramref name="user"/> (<c>me</c>) in the binding cache and, for a request whose
        /// binding was unknown, checks the proof against it. A bound user whose key cannot be read (corrupt, unsupported)
        /// fails as <see cref="DeviceProofFailureReason.Invalid"/>. "Not bound" is recorded only from a
        /// <paramref name="fresh"/> <c>me</c> answer (negative entry for <see cref="DeviceBoundSessionOptions.UnboundRecheckInterval"/>);
        /// a cached user saying "not bound" changes nothing.
        /// </summary>
        public async Task<DeviceProofCheck> LearnAsync(HttpContext context, string sessionId, DeviceProofCheck check, CurrentUserInfo? user,
            bool fresh)
        {
            var options = _options.Value.DeviceBound;
            if (!options.Enabled || user is null)
                return check;
            if (!user.IsDeviceBound)
            {
                if (!fresh)
                    return check;
                _bindings.MarkUnbound(sessionId, options.UnboundRecheckInterval);
                return check.Applicable && !check.Bound ? check with { KnownUnbound = true } : check;
            }
            if (_bindings.Get(sessionId) is not null)
                return check;
            var key = DeviceKey.TryParseStored(user.DeviceKeyJwk, user.DeviceKeyAlgorithm);
            if (key is null)
            {
                _logger.LogError("DBSC: the API reports session {SessionId} as bound but its device key ({Algorithm}) cannot be used.",
                    sessionId, user.DeviceKeyAlgorithm is null ? "none" : Sanitize(user.DeviceKeyAlgorithm));
                return check.Applicable && !check.Bound ? new DeviceProofCheck(true, true, DeviceProofFailureReason.Invalid) : check;
            }
            key = _bindings.Set(sessionId, key, await GetSessionExpiryAsync(context).ConfigureAwait(false));
            if (!check.Applicable || check.Bound)
                return check;
            return new DeviceProofCheck(true, true, _proofCookie.CheckRequest(context, sessionId, key.Thumbprint));
        }

        /// <summary>Absolute expiry of the web session (the cookie ticket's <c>ExpiresUtc</c>; cached by the cookie handler).</summary>
        internal async Task<DateTimeOffset?> GetSessionExpiryAsync(HttpContext context)
        {
            var ticket = await context.AuthenticateAsync(_options.Value.Scheme).ConfigureAwait(false);
            return ticket.Succeeded ? ticket.Properties?.ExpiresUtc : null;
        }

        /// <summary>
        /// When <paramref name="check"/> carries a failure: asks the handler (with <paramref name="user"/>, null = unknown),
        /// logs, counts and applies the outcome.
        /// </summary>
        public async Task<DeviceProofResult> EnforceAsync(HttpContext context, WebSessionManager sessions, string sessionId,
            DeviceProofCheck check, CurrentUserInfo? user)
        {
            if (!check.Applicable || check.Failure is not { } reason)
                return DeviceProofResult.Proceed;
            var outcome = await DecideAsync(context, sessionId, reason, user).ConfigureAwait(false);
            if (outcome == DeviceProofOutcome.SignOut && context.Response.HasStarted)
            {
                // The handler already wrote the response: the cookies can no longer be deleted (read-only headers).
                _logger.LogDebug("DBSC: the proof failure handler returned SignOut for session {SessionId} after starting the response; treated as Handled.", sessionId);
                outcome = DeviceProofOutcome.Handled;
            }
            _metrics.ProofFailure(DeviceBoundMetrics.ReasonTag(reason), outcome);
            // Debug, not Warning: a browser that lost its DBSC session fails on EVERY request until the next sign-in;
            // the counter tqk.auth.dbsc.proof_failures is the signal to watch.
            _logger.LogDebug("DBSC proof failed for session {SessionId}: {Reason}; outcome {Outcome}.", sessionId, reason, outcome);
            switch (outcome)
            {
                case DeviceProofOutcome.Continue:
                    return DeviceProofResult.Proceed;
                case DeviceProofOutcome.SignOut:
                    // Revokes on the API with the session token (still readable from the ticket), then drops both cookies.
                    await sessions.SignOutAsync(context).ConfigureAwait(false);
                    RejectRequest(context);
                    return DeviceProofResult.Guest;
                case DeviceProofOutcome.Handled:
                    RejectRequest(context);
                    if (!context.Response.HasStarted)
                    {
                        // Nothing written: answer 403 (no body) rather than an empty 200.
                        _logger.LogDebug("DBSC: the proof failure handler returned Handled for session {SessionId} without starting the response; answering 403.", sessionId);
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    }
                    return DeviceProofResult.Handled;
                default:
                    RejectRequest(context);
                    return DeviceProofResult.Guest;
            }
        }

        /// <summary>The handler's outcome; its exception or an undefined value falls back to the default outcome.</summary>
        private async ValueTask<DeviceProofOutcome> DecideAsync(HttpContext context, string sessionId, DeviceProofFailureReason reason,
            CurrentUserInfo? user)
        {
            var defaultMode = DefaultMode(sessionId, user);
            var fallback = DeviceProofFailureHandler.ToOutcome(defaultMode);
            DeviceProofOutcome outcome;
            try
            {
                outcome = await _handler.HandleAsync(new DeviceProofFailureContext
                {
                    HttpContext = context,
                    SessionId = sessionId,
                    Reason = reason,
                    DefaultMode = defaultMode,
                    User = user,
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The request was aborted: it must not keep the session token for whatever still runs (Response.OnCompleted,
                // an exception handler) while the cancellation propagates.
                RejectRequest(context);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DBSC: the proof failure handler threw; using the default outcome {Outcome}.", fallback);
                return fallback;
            }
            if (Enum.IsDefined(outcome))
                return outcome;
            _logger.LogWarning("DBSC: the proof failure handler returned undefined outcome {Outcome}; using the default outcome {Fallback}.",
                (int)outcome, fallback);
            return fallback;
        }

        /// <summary>
        /// <see cref="DeviceBoundSessionOptions.FailureMode"/>; with <see cref="DeviceBoundSessionOptions.AllowUserChoice"/>
        /// the user's <see cref="CurrentUserInfo.DeviceBindingMode"/> when it names an allowed mode.
        /// </summary>
        private DeviceProofFailureMode DefaultMode(string sessionId, CurrentUserInfo? user)
        {
            var options = _options.Value.DeviceBound;
            var mode = options.FailureMode;
            if (options.AllowUserChoice && !string.IsNullOrWhiteSpace(user?.DeviceBindingMode))
            {
                if (TryParseMode(user.DeviceBindingMode, out var chosen) && options.AllowedUserModes.Contains(chosen))
                    mode = chosen;
                else
                    _logger.LogDebug("DBSC: device binding mode {UserMode} of session {SessionId} is unknown or not allowed; using {Mode}.",
                        Sanitize(user.DeviceBindingMode), sessionId, mode);
            }
            return mode;
        }

        /// <summary>
        /// Guest for this request only, WITHOUT the session token: the cookie is kept, but the ticket is marked rejected so
        /// <see cref="SessionBearerHandler"/> sends no token even when an endpoint re-authenticates the cookie scheme or the
        /// request is re-executed.
        /// </summary>
        private static void RejectRequest(HttpContext context)
        {
            WebSessionTicket.For(context).Reject();
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        internal static bool IsExcluded(PathString path, DeviceBoundSessionOptions options)
        {
            foreach (var excluded in options.ExcludedPaths)
            {
                if (path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Exact enum names only (no numbers, no flags), case-insensitive.</summary>
        internal static bool TryParseMode(string value, out DeviceProofFailureMode mode)
        {
            foreach (var candidate in Enum.GetValues<DeviceProofFailureMode>())
            {
                if (string.Equals(candidate.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    mode = candidate;
                    return true;
                }
            }
            mode = default;
            return false;
        }

        /// <summary>Untrusted text for a log line: printable ASCII, at most 64 chars.</summary>
        internal static string Sanitize(string value)
        {
            var span = value.Length > 64 ? value[..64] : value;
            return new string(span.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        }
    }
}
