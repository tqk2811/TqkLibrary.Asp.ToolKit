namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>
    /// Common reason codes of a <see cref="SessionRejection"/> (also usable as
    /// <see cref="SessionAccessRecord.UserRejectionReason"/>); apps may use their own strings.
    /// </summary>
    public static class SessionRejectionReasons
    {
        /// <summary>
        /// The token is malformed, names no session or its secret does not match the session (set by the library).
        /// Deliberately the same for all three, so a caller cannot probe which session ids exist.
        /// </summary>
        public const string InvalidToken = "invalid_token";

        /// <summary>The session was signed out / revoked (set by the library from <see cref="SessionAccessRecord.IsRevoked"/>).</summary>
        public const string Revoked = "revoked";

        /// <summary>The session expired or reached its absolute cap (set by the library).</summary>
        public const string Expired = "expired";

        /// <summary>The account is locked or disabled (suggested value of <see cref="SessionAccessRecord.UserRejectionReason"/>).</summary>
        public const string UserLocked = "user_locked";
    }
}
