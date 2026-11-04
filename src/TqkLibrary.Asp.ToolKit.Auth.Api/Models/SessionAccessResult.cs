using System.Diagnostics.CodeAnalysis;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>
    /// Answer of <see cref="Interfaces.ISessionAccessSource"/>: either the current access data of a live session
    /// (<see cref="Grant"/>, or implicitly from a <see cref="SessionAccessRecord"/>) or a rejection with an optional
    /// reason code (<see cref="Reject"/>). Granted results are cached and shared between requests: do not mutate.
    /// </summary>
    public sealed record SessionAccessResult
    {
        private static readonly SessionAccessResult RejectedWithoutReason = new(null, null);

        private SessionAccessResult(SessionAccessRecord? access, string? reason)
        {
            Access = access;
            Reason = reason;
        }

        /// <summary>Access data when granted, otherwise null.</summary>
        public SessionAccessRecord? Access { get; }

        /// <summary>
        /// Rejection reason code chosen by the app (see <see cref="SessionRejectionReasons"/> for common values) or
        /// null when not given. Always null when granted.
        /// </summary>
        public string? Reason { get; }

        /// <summary>True when the session is alive and <see cref="Access"/> is set.</summary>
        [MemberNotNullWhen(true, nameof(Access))]
        public bool IsGranted => Access is not null;

        /// <summary>The session is alive with <paramref name="access"/>.</summary>
        public static SessionAccessResult Grant(SessionAccessRecord access)
        {
            ArgumentNullException.ThrowIfNull(access);
            return new SessionAccessResult(access, null);
        }

        /// <summary>The session must not be used (request gets 401); <paramref name="reason"/> is optional.</summary>
        public static SessionAccessResult Reject(string? reason = null)
            => reason is null ? RejectedWithoutReason : new SessionAccessResult(null, reason);

        /// <summary>Same as <see cref="Grant"/>.</summary>
        public static implicit operator SessionAccessResult(SessionAccessRecord access) => Grant(access);
    }

    /// <summary>Common reason codes for <see cref="SessionAccessResult.Reject"/>; apps may use their own strings.</summary>
    public static class SessionRejectionReasons
    {
        /// <summary>
        /// The token is malformed or its secret does not match the session (set by the library). Deliberately the same
        /// for both, so a caller cannot probe which session ids exist.
        /// </summary>
        public const string InvalidToken = "invalid_token";

        /// <summary>The session was signed out / revoked (set by the library from <see cref="SessionAccessRecord.IsRevoked"/>).</summary>
        public const string Revoked = "revoked";

        /// <summary>The session expired or reached its absolute cap (set by the library).</summary>
        public const string Expired = "expired";

        /// <summary>The account is locked or disabled.</summary>
        public const string UserLocked = "user_locked";
    }
}
