namespace TqkLibrary.Asp.ToolKit.Auth.Web.Models
{
    /// <summary>
    /// The session the API opened at login, handed to <see cref="WebSessionManager.SignInAsync"/>. Only immutable
    /// data: the token is never rotated, so the cookie is written once at sign-in and never reissued.
    /// </summary>
    public sealed record AuthSession
    {
        /// <summary>Opaque session token <c>{sessionId}.{secret}</c> issued by the API; sent as bearer on every API call. Never log it.</summary>
        public required string SessionToken { get; init; }

        /// <summary>Id of the signed-in user (stored in the cookie as <c>ClaimTypes.NameIdentifier</c>).</summary>
        public required string UserId { get; init; }

        /// <summary>
        /// Cookie expiry (UTC): give the session's ABSOLUTE cap (sign-in + the API's maximum session lifetime), not its
        /// sliding expiry, because the cookie is not reissued while the API slides the session. Until then the API
        /// alone decides whether the session is alive (a 401 drops the cookie). Null = now + the cookie
        /// <c>ExpireTimeSpan</c>.
        /// </summary>
        public DateTime? ExpiresAt { get; init; }
    }
}
