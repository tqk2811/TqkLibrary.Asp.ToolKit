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
        /// the session is not revoked and has no key yet, in ONE atomic statement, for example
        /// <c>UPDATE Sessions SET DevicePublicKey = @key, DeviceKeyAlgorithm = @alg WHERE Id = @id AND RevokedAt IS NULL AND DevicePublicKey IS NULL</c>.
        /// Returns true iff exactly one row changed. A key is registered at most once per session and never replaced:
        /// a stolen session token must not be able to rebind the session to the attacker's device.
        /// </summary>
        Task<bool> TrySetDeviceKeyAsync(string sessionId, string publicKeyJwk, string algorithm, CancellationToken cancellationToken);
    }
}
