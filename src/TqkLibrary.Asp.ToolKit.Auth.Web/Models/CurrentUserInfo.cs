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
    }
}
