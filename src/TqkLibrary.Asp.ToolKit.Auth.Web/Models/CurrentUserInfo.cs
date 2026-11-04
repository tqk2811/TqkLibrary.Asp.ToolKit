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
        /// DBSC: the API session has a device key (<see cref="Interfaces.IDeviceKeyApiClient.RegisterDeviceKeyAsync"/>
        /// succeeded). When true but the request's cookie ticket has no key (a copy of the cookie taken before binding),
        /// the request runs as a guest. Ignored when <see cref="DeviceBoundSessionOptions.Enabled"/> is off.
        /// </summary>
        public bool IsDeviceBound { get; init; }

        /// <summary>
        /// DBSC: the user's own choice of <see cref="DeviceProofFailureMode"/> (name, case-insensitive:
        /// <c>ReportOnly</c>, <c>Reject</c>, <c>SignOut</c>), stored by the app (account settings). Used only when
        /// <see cref="DeviceBoundSessionOptions.AllowUserChoice"/> is on and the value is in
        /// <see cref="DeviceBoundSessionOptions.AllowedUserModes"/>; null = the project default.
        /// </summary>
        public string? DeviceBindingMode { get; init; }
    }
}
