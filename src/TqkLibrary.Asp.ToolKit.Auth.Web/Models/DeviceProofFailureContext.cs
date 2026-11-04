using Microsoft.AspNetCore.Http;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Models
{
    /// <summary>Argument of <see cref="DeviceProofFailureHandler.HandleAsync"/>.</summary>
    public sealed class DeviceProofFailureContext
    {
        /// <summary>The request whose proof failed.</summary>
        public required HttpContext HttpContext { get; init; }

        /// <summary>API session id of the request.</summary>
        public required string SessionId { get; init; }

        /// <summary>Why the proof failed.</summary>
        public required DeviceProofFailureReason Reason { get; init; }

        /// <summary>
        /// The mode of the default handling: <see cref="DeviceBoundSessionOptions.FailureMode"/>, replaced by the user's
        /// choice when <see cref="DeviceBoundSessionOptions.AllowUserChoice"/> is on and the choice is allowed. The base
        /// <see cref="DeviceProofFailureHandler.HandleAsync"/> returns it as an outcome.
        /// </summary>
        public required DeviceProofFailureMode DefaultMode { get; init; }

        /// <summary>
        /// The user as reported by <c>me</c> (cached or just loaded) when it is known; null when the decision is taken
        /// before <c>me</c> (fixed policy and a session already known to be bound), when
        /// <see cref="WebAuthOptions.SkipCurrentUser"/> skipped it, or when <c>me</c> failed.
        /// </summary>
        public CurrentUserInfo? User { get; init; }
    }
}
