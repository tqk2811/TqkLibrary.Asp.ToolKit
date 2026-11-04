namespace TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces
{
    /// <summary>
    /// Optional app-provided store of the device public key of a session (DBSC, Device Bound Session Credentials), used by
    /// <see cref="DeviceKeyRegistrar"/>. Only apps that support device binding register it; resolved from the request
    /// services when a key is registered, so it may be scoped (DbContext).
    /// </summary>
    public interface IDeviceKeyStore
    {
        /// <summary>
        /// Stores <paramref name="publicKeyJwk"/> (canonical public JWK, already validated) and
        /// <paramref name="algorithm"/> (<c>ES256</c> or <c>RS256</c>) on session <paramref name="sessionId"/> ONLY when
        /// the session is not revoked, not expired at <paramref name="now"/>, has no key yet and (when
        /// <paramref name="createdNotBefore"/> is set) was created at or after it, in ONE atomic statement, for example
        /// <c>UPDATE Sessions SET DevicePublicKey=@key, DeviceKeyAlgorithm=@alg WHERE Id=@id AND RevokedAt IS NULL AND DevicePublicKey IS NULL AND ExpiresAt &gt; @now AND (@createdNotBefore IS NULL OR CreatedAt &gt;= @createdNotBefore)</c>.
        /// Returns true iff exactly one row changed. False covers: already bound, revoked, expired, deleted, or the
        /// registration window closed (<see cref="ApiAuthOptions.DeviceKeyRegistrationWindow"/>). A key is registered at
        /// most once per session and never replaced: a stolen session token must not be able to rebind the session to
        /// the attacker's device.
        /// <para>
        /// Under SQL Server SNAPSHOT isolation a concurrent UPDATE throws an update-conflict error instead of returning
        /// 0 rows. READ COMMITTED / RCSI, PostgreSQL, MySQL and SQLite return 0 rows.
        /// </para>
        /// </summary>
        Task<bool> TrySetDeviceKeyAsync(string sessionId, string publicKeyJwk, string algorithm, DateTimeOffset now, DateTimeOffset? createdNotBefore, CancellationToken cancellationToken);
    }
}
