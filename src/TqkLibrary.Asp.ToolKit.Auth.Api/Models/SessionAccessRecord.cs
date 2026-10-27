namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>
    /// What the app knows about a live session's user right now. Cached and shared between requests: do not mutate.
    /// </summary>
    public sealed record SessionAccessRecord
    {
        /// <summary>User id; must match the token's user id.</summary>
        public required string UserId { get; init; }

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
