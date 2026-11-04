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
        /// The session row <paramref name="sessionId"/> with its user's current data, as
        /// <see cref="SessionAccessResult.Grant"/> (or a <see cref="SessionAccessRecord"/>, converted implicitly), even
        /// when it is revoked or expired: the library checks the secret hash, <see cref="SessionAccessRecord.IsRevoked"/>
        /// and the expiry itself. Return <see cref="SessionAccessResult.Reject"/> (optional reason) when the session does
        /// not exist or the account is unusable (locked, deleted). Called through <see cref="SessionAccessCache"/>: a
        /// granted record is cached for <see cref="ApiAuthOptions.SessionAccessCacheLifetime"/>, a rejection never.
        /// </summary>
        Task<SessionAccessResult> GetAsync(string sessionId, CancellationToken cancellationToken);

        /// <summary>
        /// Persists the sliding expiry: LastAccessAt = <paramref name="lastAccessAt"/>, ExpiresAt =
        /// <paramref name="expiresAt"/> on session <paramref name="sessionId"/> (UTC). Called at most once per
        /// <see cref="ApiAuthOptions.SessionTouchInterval"/> per session and process. A failure is logged and does not
        /// fail the request. Should not touch a revoked session (e.g. <c>UPDATE ... WHERE Id = @id AND RevokedAt IS NULL</c>).
        /// </summary>
        Task TouchAsync(string sessionId, DateTime lastAccessAt, DateTime expiresAt, CancellationToken cancellationToken);
    }
}
