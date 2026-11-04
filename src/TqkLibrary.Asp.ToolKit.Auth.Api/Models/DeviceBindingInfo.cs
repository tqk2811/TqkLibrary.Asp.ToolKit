namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>
    /// The device binding (DBSC) of the current session, read from the principal by
    /// <see cref="Extensions.DeviceBindingClaimsPrincipalExtensions.GetDeviceBinding"/>. The app's <c>me</c> endpoint returns
    /// it to the web front end as <c>isDeviceBound</c> (true), <c>deviceKeyJwk</c>, <c>deviceKeyAlgorithm</c> and
    /// <c>deviceBindingMode</c>.
    /// </summary>
    /// <param name="Jwk">The stored device public key (canonical public JWK).</param>
    /// <param name="Algorithm">Its algorithm (<c>ES256</c> or <c>RS256</c>); null when the app did not store it.</param>
    /// <param name="Mode">The end user's own proof-failure mode (<see cref="SessionAccessRecord.DeviceBindingMode"/>); null = project default.</param>
    public sealed record DeviceBindingInfo(string Jwk, string? Algorithm, string? Mode);
}
