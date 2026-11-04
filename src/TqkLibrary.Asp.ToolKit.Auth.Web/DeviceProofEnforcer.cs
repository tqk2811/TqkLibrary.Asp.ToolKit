using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Enforces the DBSC proof on ordinary requests (singleton), called by <see cref="CurrentUserMiddleware"/>:
    /// <see cref="CheckAsync"/> reads whether the ticket is bound and checks the proof cookie;
    /// <see cref="DecideAsync"/> picks the mode (project default → user choice when allowed → hook);
    /// <see cref="ApplyAsync"/> logs, counts and applies it.
    /// </summary>
    internal sealed class DeviceProofEnforcer
    {
        private readonly IOptions<WebAuthOptions> _options;
        private readonly DeviceProofCookie _proofCookie;
        private readonly DeviceBoundMetrics _metrics;
        private readonly ILogger<DeviceProofEnforcer> _logger;

        public DeviceProofEnforcer(IOptions<WebAuthOptions> options, DeviceProofCookie proofCookie, DeviceBoundMetrics metrics,
            ILogger<DeviceProofEnforcer> logger)
        {
            _options = options;
            _proofCookie = proofCookie;
            _metrics = metrics;
            _logger = logger;
        }

        public bool Enabled => _options.Value.DeviceBound.Enabled;

        /// <summary>Whether the decision depends on the user (<see cref="DeviceBoundSessionOptions.AllowUserChoice"/>).</summary>
        public bool NeedsUser => _options.Value.DeviceBound.AllowUserChoice;

        /// <summary>DBSC state of the request of session <paramref name="sessionId"/>.</summary>
        public async Task<DeviceProofCheck> CheckAsync(HttpContext context, string sessionId)
        {
            var options = _options.Value;
            if (!options.DeviceBound.Enabled || IsExcluded(context.Request.Path, options.DeviceBound))
                return DeviceProofCheck.NotApplicable;
            // Cached by the cookie handler: no second decryption.
            var ticket = await context.AuthenticateAsync(options.Scheme).ConfigureAwait(false);
            var bound = ticket.Succeeded && !string.IsNullOrEmpty(ticket.Properties?.GetTokenValue(WebSessionTicket.DeviceJwkName));
            if (!bound)
                return new DeviceProofCheck(true, false, null);
            return new DeviceProofCheck(true, true, _proofCookie.CheckRequest(context, sessionId));
        }

        /// <summary>
        /// The mode for a failed proof: <see cref="DeviceBoundSessionOptions.FailureMode"/>; with
        /// <see cref="DeviceBoundSessionOptions.AllowUserChoice"/> the user's <see cref="CurrentUserInfo.DeviceBindingMode"/>
        /// when it names an allowed mode; then <see cref="DeviceBoundSessionOptions.OnProofFailure"/>.
        /// </summary>
        public async ValueTask<DeviceProofFailureMode> DecideAsync(HttpContext context, string sessionId,
            DeviceProofFailureReason reason, CurrentUserInfo? user)
        {
            var options = _options.Value.DeviceBound;
            var mode = options.FailureMode;
            if (options.AllowUserChoice && !string.IsNullOrEmpty(user?.DeviceBindingMode))
            {
                if (TryParseMode(user.DeviceBindingMode, out var chosen) && options.AllowedUserModes.Contains(chosen))
                    mode = chosen;
                else
                    _logger.LogWarning("DBSC: device binding mode {UserMode} of session {SessionId} is unknown or not allowed; using {Mode}.",
                        Sanitize(user.DeviceBindingMode), sessionId, mode);
            }
            if (options.OnProofFailure is { } hook)
            {
                try
                {
                    var decided = await hook(new DeviceProofFailureContext
                    {
                        HttpContext = context,
                        SessionId = sessionId,
                        Reason = reason,
                        Mode = mode,
                        User = user,
                    }).ConfigureAwait(false);
                    if (Enum.IsDefined(decided))
                        mode = decided;
                    else
                        _logger.LogWarning("DBSC: OnProofFailure returned undefined mode {Mode}; keeping the decided mode.", (int)decided);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogError(ex, "DBSC: OnProofFailure threw; keeping mode {Mode}.", mode);
                }
            }
            return mode;
        }

        /// <summary>
        /// Logs, counts and applies <paramref name="mode"/>. True when the request now runs as a guest (Reject,
        /// SignOut), false for ReportOnly.
        /// </summary>
        public async Task<bool> ApplyAsync(HttpContext context, WebSessionManager sessions, string sessionId,
            DeviceProofFailureReason reason, DeviceProofFailureMode mode)
        {
            _metrics.ProofFailure(DeviceBoundMetrics.ReasonTag(reason), mode);
            _logger.LogWarning("DBSC proof failed for session {SessionId}: {Reason}; mode {Mode}.", sessionId, reason, mode);
            switch (mode)
            {
                case DeviceProofFailureMode.ReportOnly:
                    return false;
                case DeviceProofFailureMode.SignOut:
                    // Revokes on the API with the session token (still readable from the ticket), then drops both cookies.
                    await sessions.SignOutAsync(context).ConfigureAwait(false);
                    return true;
                default:
                    RejectRequest(context);
                    return true;
            }
        }

        /// <summary>
        /// The ticket has no key but the API says the session is bound: a copy of the main cookie taken before the
        /// binding. Always a guest (whatever the mode), counted with reason <c>unbound</c>.
        /// </summary>
        public void RejectUnbound(HttpContext context, string sessionId)
        {
            _metrics.ProofFailure("unbound", DeviceProofFailureMode.Reject);
            _logger.LogWarning("DBSC: session {SessionId} is device-bound but the cookie has no device key; request runs as a guest.", sessionId);
            RejectRequest(context);
        }

        /// <summary>Guest for this request only: the cookie is kept, the session token is not used.</summary>
        private static void RejectRequest(HttpContext context)
        {
            WebSessionTicket.For(context).Clear();
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
