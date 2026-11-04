namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>
    /// The session row and its user's current data, returned by <see cref="Interfaces.ISessionAccessSource.GetAsync"/>.
    /// The library checks the secret, revocation and expiry itself from these values. Cached and shared between
    /// requests: do not mutate. Every <see cref="DateTime"/> is UTC: the library treats <see cref="DateTimeKind.Unspecified"/>
    /// as UTC and converts <see cref="DateTimeKind.Local"/>.
    /// <para>
    /// More per-session data (for example a device-bound public key) can be added later as optional properties
    /// without changing the token format.
    /// </para>
    /// </summary>
    public sealed record SessionAccessRecord
    {
        /// <summary>Owner of the session (becomes <c>ClaimTypes.NameIdentifier</c> and <see cref="ApiAuthOptions.UserIdClaimType"/>).</summary>
        public required string UserId { get; init; }

        /// <summary><see cref="IssuedSessionToken.SecretHash"/> stored at sign-in; compared in fixed time with the presented secret.</summary>
        public required string SecretHash { get; init; }

        /// <summary>Sign-in time; the absolute cap <see cref="ApiAuthOptions.MaxSessionLifetime"/> counts from it.</summary>
        public required DateTime CreatedAt { get; init; }

        /// <summary>
        /// Last time the library extended the session (<see cref="Interfaces.ISessionAccessSource.TouchAsync"/>);
        /// null = never (counted from <see cref="CreatedAt"/>).
        /// </summary>
        public DateTime? LastAccessAt { get; init; }

        /// <summary>Current (sliding) expiry; requests at or after it are rejected.</summary>
        public required DateTime ExpiresAt { get; init; }

        /// <summary>"Remember me" session: picks <see cref="SessionLifetimes.Persistent"/> when sliding.</summary>
        public bool IsPersistent { get; init; }

        /// <summary>
        /// Reason code (see <see cref="SessionRejectionReasons"/>, for example <see cref="SessionRejectionReasons.UserLocked"/>)
        /// when the user may not sign in right now (locked, disabled); null = the user may sign in. Only reported to a
        /// caller whose secret is correct.
        /// </summary>
        public string? UserRejectionReason { get; init; }

        /// <summary>True when the session was signed out / revoked (the row may stay until the cleanup job deletes it).</summary>
        public bool IsRevoked { get; init; }

        /// <summary>
        /// The app's specific revoke code (for example <c>password_changed</c>, <c>signed_out_remotely</c>), used as
        /// <see cref="SessionRejection.Reason"/> when <see cref="IsRevoked"/> and the secret is correct; null = the
        /// generic <see cref="SessionRejectionReasons.Revoked"/>.
        /// </summary>
        public string? RevokedReason { get; init; }

        /// <summary>User name (becomes <c>ClaimTypes.Name</c>).</summary>
        public required string UserName { get; init; }

        /// <summary>Email, optional (becomes <c>ClaimTypes.Email</c>).</summary>
        public string? Email { get; init; }

        /// <summary>Current role names.</summary>
        public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

        /// <summary>Current permission names.</summary>
        public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();
    }
}
