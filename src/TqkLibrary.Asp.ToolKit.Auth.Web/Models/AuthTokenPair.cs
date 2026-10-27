namespace TqkLibrary.Asp.ToolKit.Auth.Web.Models
{
    /// <summary>
    /// Tokens issued by the API on login or refresh. Times are UTC. The API must NOT rotate refresh tokens as
    /// single-use: concurrent requests of one browser carry the same refresh token.
    /// </summary>
    public sealed record AuthTokenPair
    {
        /// <summary>API session id (stored as the only claim of the cookie principal).</summary>
        public required string SessionId { get; init; }

        /// <summary>Bearer access token.</summary>
        public required string AccessToken { get; init; }

        /// <summary>Access token expiry (UTC).</summary>
        public required DateTime AccessTokenExpiresAt { get; init; }

        /// <summary>Refresh token.</summary>
        public required string RefreshToken { get; init; }

        /// <summary>Refresh token / session expiry (UTC); the cookie never outlives it.</summary>
        public required DateTime RefreshTokenExpiresAt { get; init; }
    }
}
