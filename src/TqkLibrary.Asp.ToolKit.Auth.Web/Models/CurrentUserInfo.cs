namespace TqkLibrary.Asp.ToolKit.Auth.Web.Models
{
    /// <summary>The signed-in user as reported by the API (<c>me</c> endpoint). Shared between requests: do not mutate.</summary>
    public sealed record CurrentUserInfo
    {
        /// <summary>User id (becomes <c>ClaimTypes.NameIdentifier</c>).</summary>
        public required string Id { get; init; }

        /// <summary>User name (becomes <c>ClaimTypes.Name</c>).</summary>
        public required string UserName { get; init; }

        /// <summary>Email, optional.</summary>
        public string? Email { get; init; }

        /// <summary>Role names (become <c>ClaimTypes.Role</c>).</summary>
        public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

        /// <summary>Permission names (become <see cref="WebAuthOptions.PermissionClaimType"/> claims).</summary>
        public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Additional claims added to the request principal as they are (tenant, display name, feature flags...). Cached
        /// with the rest in memory (<see cref="CurrentUserCache"/>), never written to the cookie. Claims whose type is
        /// <see cref="WebAuthOptions.SessionIdClaimType"/> or <see cref="WebAuthOptions.StableIdentityClaimType"/> are
        /// ignored (they would break the session binding).
        /// </summary>
        public IReadOnlyList<CurrentUserClaim> ExtraClaims { get; init; } = Array.Empty<CurrentUserClaim>();

        /// <summary>
        /// DBSC: the device public key stored on the API session (canonical public JWK, <c>me</c> field
        /// <c>deviceKeyJwk</c>); null = the session is not bound. The API is the single source of truth for the key: the
        /// web verifies refresh proofs with it and requires a proof cookie made for it on every request of the session.
        /// Ignored when <see cref="DeviceBoundSessionOptions.Enabled"/> is off.
        /// </summary>
        public string? DeviceKeyJwk { get; init; }

        /// <summary>
        /// DBSC: algorithm of <see cref="DeviceKeyJwk"/> (<c>ES256</c> or <c>RS256</c>, <c>me</c> field <c>deviceKeyAlgorithm</c>).
        /// Null or empty = derived from the JWK key type (<c>EC</c> → <c>ES256</c>, <c>RSA</c> → <c>RS256</c>) as a fallback.
        /// </summary>
        public string? DeviceKeyAlgorithm { get; init; }

        /// <summary>
        /// DBSC: the API session has a device key (derived: <see cref="DeviceKeyJwk"/> is not empty). A <c>me</c> field
        /// <c>isDeviceBound</c> is not read; the key itself decides.
        /// </summary>
        public bool IsDeviceBound => !string.IsNullOrEmpty(DeviceKeyJwk);

        /// <summary>
        /// DBSC: the user's own choice of <see cref="DeviceProofFailureMode"/> (name, case-insensitive:
        /// <c>ReportOnly</c>, <c>Reject</c>, <c>SignOut</c>), stored by the app (account settings). Used only when
        /// <see cref="DeviceBoundSessionOptions.AllowUserChoice"/> is on and the value is in
        /// <see cref="DeviceBoundSessionOptions.AllowedUserModes"/>; null = the project default.
        /// </summary>
        public string? DeviceBindingMode { get; init; }
    }
}
