using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces
{
    /// <summary>
    /// The app's bridge to its API. Implement it on top of the app's own HTTP client.
    /// <para><b>Error contract</b> (the library relies on it to decide between "sign out" and "try again later"):</para>
    /// <list type="bullet">
    ///   <item>The API says the session is dead (401/403 on refresh, 401 on <c>me</c>) → throw
    ///   <see cref="SessionExpiredException"/>.</item>
    ///   <item>Anything else (5xx, network, timeout) → let the original exception propagate: it is treated as a
    ///   transient failure and the user is NOT signed out.</item>
    /// </list>
    /// </summary>
    public interface IAuthApiClient
    {
        /// <summary>
        /// Exchanges a refresh token for new tokens. Called inside <see cref="AnonymousApiScope"/>, so a client that
        /// goes through <see cref="SessionBearerHandler"/> is not re-entered. Must not consume the refresh token
        /// (single-use rotation breaks concurrent requests).
        /// </summary>
        Task<AuthTokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

        /// <summary>
        /// The current user of the session. Called through the app's client with <see cref="SessionBearerHandler"/>
        /// attached, which adds the bearer and refreshes it when needed.
        /// </summary>
        Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken cancellationToken);

        /// <summary>Revokes the current session on the API (bearer attached by <see cref="SessionBearerHandler"/>).</summary>
        Task LogoutAsync(CancellationToken cancellationToken);
    }
}
