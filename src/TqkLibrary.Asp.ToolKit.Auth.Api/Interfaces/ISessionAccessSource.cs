using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces
{
    /// <summary>
    /// App-provided session store used by <see cref="SessionTokenAuthenticationHandler"/>. Resolved from the request
    /// services, so it may be scoped (DbContext).
    /// </summary>
    public interface ISessionAccessSource
    {
        /// <summary>
        /// The session row <paramref name="sessionId"/> with its user's current data, even when it is revoked, expired or
        /// its user cannot sign in: the library checks the secret hash first, then
        /// <see cref="SessionAccessRecord.UserRejectionReason"/>, <see cref="SessionAccessRecord.IsRevoked"/> and the
        /// expiry itself, so none of those states is revealed to a caller who does not hold the secret. Return null only
        /// when no such session exists. Called through <see cref="SessionAccessCache"/>: the record (or null) is cached
        /// for <see cref="ApiAuthOptions.SessionAccessCacheLifetime"/>; call
        /// <see cref="SessionAccessCache.InvalidateSession"/> after creating a session whose id may have been probed.
        /// </summary>
        Task<SessionAccessRecord?> GetAsync(string sessionId, CancellationToken cancellationToken);

        /// <summary>
        /// Persists the sliding expiry: LastAccessAt = <paramref name="lastAccessAt"/>, ExpiresAt =
        /// <paramref name="expiresAt"/> on session <paramref name="sessionId"/> (UTC). Called at most once per
        /// <see cref="ApiAuthOptions.SessionTouchInterval"/> per session and process. A failure is logged and does not
        /// fail the request. Should not touch a revoked session (e.g. <c>UPDATE ... WHERE Id = @id AND RevokedAt IS NULL</c>).
        /// </summary>
        Task TouchAsync(string sessionId, DateTime lastAccessAt, DateTime expiresAt, CancellationToken cancellationToken);
    }
}
