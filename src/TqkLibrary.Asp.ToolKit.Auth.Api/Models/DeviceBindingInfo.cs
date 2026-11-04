namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>
    /// The device binding (DBSC) of the current session, read from the principal by
    /// <see cref="Extensions.DeviceBindingClaimsPrincipalExtensions.GetDeviceBinding"/>. The app's <c>me</c> endpoint returns
    /// it to the web front end as <c>deviceKeyJwk</c> and <c>deviceKeyAlgorithm</c> (what the web uses: the session is
    /// bound when <c>deviceKeyJwk</c> is not empty) and <c>deviceBindingMode</c>; an <c>isDeviceBound</c> field is
    /// informational only.
    /// </summary>
    /// <param name="Jwk">The stored device public key (canonical public JWK).</param>
    /// <param name="Algorithm">
    /// Its algorithm (<c>ES256</c> or <c>RS256</c>); null when the app did not store it. Apps should store and return it;
    /// the web front end derives a missing one from the JWK key type (<c>EC</c> → <c>ES256</c>, <c>RSA</c> → <c>RS256</c>)
    /// only as a fallback.
    /// </param>
    /// <param name="Mode">The end user's own proof-failure mode (<see cref="SessionAccessRecord.DeviceBindingMode"/>); null = project default.</param>
    public sealed record DeviceBindingInfo(string Jwk, string? Algorithm, string? Mode);
}
